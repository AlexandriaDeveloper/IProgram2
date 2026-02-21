using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Auth.Infrastructure.Hubs;

namespace Auth.Infrastructure.Services
{
    public class DataMigrationService
    {
        private readonly string _sqlServerConn;
        private readonly string _supabaseConn;
        private readonly IHubContext<MigrationHub> _hubContext;

        public DataMigrationService(Microsoft.Extensions.Configuration.IConfiguration configuration, IHubContext<MigrationHub> hubContext)
        {
            _hubContext = hubContext;
            _sqlServerConn = configuration.GetConnectionString("DefaultConnection")
                ?? "Server=localhost,1433;Database=IProgramDb;User Id=sa;Password=123;TrustServerCertificate=True;Encrypt=False";
            _supabaseConn = configuration.GetConnectionString("SupabaseConnection")
                ?? "Host=aws-1-eu-west-3.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.iztxgikxmcpzoqowomtp;Password=FNsGxA0IN0qzqSDC;SSL Mode=Require;Trust Server Certificate=true;";
        }

        public async Task<SyncResult> FullSyncToSupabaseAsync(bool force = false)
        {
            var result = new SyncResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Console.WriteLine($"=== Starting FULL SYNC: SQL Server -> Supabase (Force={force}) ===");

                // 0. Check for version conflict
                if (!force)
                {
                    var isNewer = await CheckSupabaseIsNewerAsync();
                    if (isNewer)
                    {
                        result.Success = false;
                        result.Error = "VERSION_CONFLICT"; // Special error code for frontend
                        Console.WriteLine("SYNC ABORTED: Supabase has newer data.");
                        return result;
                    }
                }

                // Sync in order of dependencies
                await SyncTableAsync("AspNetRoles", new[] { "Id", "Name", "NormalizedName", "ConcurrencyStamp" }, result);
                await SyncTableAsync("AspNetUsers", new[] {
                    "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                    "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
                    "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd",
                    "LockoutEnabled", "AccessFailedCount", "DisplayName", "DisplayImage"
                }, result);
                await SyncTableAsync("AspNetUserRoles", new[] { "UserId", "RoleId" }, result, compositeKey: new[] { "UserId", "RoleId" });
                await SyncTableAsync("Departments", new[] {
                    "Id", "Name", "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await SyncTableAsync("Employees", new[] {
                    "Id", "Name", "TabCode", "TegaraCode", "Section", "Email", "Collage", "DepartmentId",
                    "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await SyncTableAsync("Daily", new[] {
                    "Id", "Name", "DailyDate", "IsActive", "Closed", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await SyncTableAsync("DailyReference", new[] {
                    "Id", "DailyId", "ReferencePath", "Description", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await SyncTableAsync("Form", new[] {
                    "Id", "Name", "Description", "Index", "DailyId", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await SyncTableAsync("FormDetails", new[] {
                    "Id", "FormId", "EmployeeId", "Amount", "OrderNum", "IsReviewed", "IsReviewedBy",
                    "ReviewedAt", "ReviewComments", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await SyncTableAsync("FormRefernce", new[] {
                    "Id", "FormId", "Name", "FilePath", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);

                stopwatch.Stop();
                result.Success = true;
                result.Duration = stopwatch.Elapsed;
                Console.WriteLine($"=== FULL SYNC Completed in {stopwatch.Elapsed.TotalSeconds:F2}s ===");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.Error = ex.Message;
                result.Duration = stopwatch.Elapsed;
                Console.WriteLine($"SYNC ERROR: {ex.Message}");
            }

            return result;
        }

        private async Task SyncTableAsync(string tableName, string[] columns, SyncResult result, string[]? compositeKey = null)
        {
            Console.WriteLine($"Syncing {tableName}...");
            var tableResult = new TableSyncResult { TableName = tableName };
            var keyColumn = compositeKey ?? new[] { "Id" };

            try
            {
                // 1. Read all data from SQL Server
                var sourceData = new List<Dictionary<string, object?>>();
                await using (var sqlConn = new SqlConnection(_sqlServerConn))
                {
                    await sqlConn.OpenAsync();
                    var selectCmd = new SqlCommand($"SELECT * FROM [{tableName}]", sqlConn);
                    await using var reader = await selectCmd.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (var col in columns)
                        {
                            try
                            {
                                var value = reader[col];
                                row[col] = value == DBNull.Value ? null : value;
                            }
                            catch
                            {
                                row[col] = null;
                            }
                        }
                        sourceData.Add(row);
                    }
                }

                tableResult.SourceCount = sourceData.Count;
                Console.WriteLine($"  Source: {sourceData.Count} records");

                if (sourceData.Count == 0)
                {
                    // Delete all from target if source is empty
                    await using var pgConn = new NpgsqlConnection(_supabaseConn);
                    await pgConn.OpenAsync();
                    await using var deleteCmd = new NpgsqlCommand($"DELETE FROM \"{tableName}\"", pgConn);
                    tableResult.Deleted = await deleteCmd.ExecuteNonQueryAsync();
                    Console.WriteLine($"  Deleted all ({tableResult.Deleted}) from target");
                }
                else
                {
                    await using var pgConn = new NpgsqlConnection(_supabaseConn);
                    await pgConn.OpenAsync();

                    // 2. Get existing IDs from Supabase
                    var existingIds = new HashSet<string>();
                    var keySelect = string.Join(" || '|' || ", keyColumn.Select(k => $"COALESCE(\"{k}\"::text, '')"));
                    await using (var selectCmd = new NpgsqlCommand($"SELECT {keySelect} AS key FROM \"{tableName}\"", pgConn))
                    await using (var reader = await selectCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            existingIds.Add(reader.GetString(0));
                        }
                    }

                    // 3. Build source ID set
                    var sourceIds = new HashSet<string>();
                    foreach (var row in sourceData)
                    {
                        var key = string.Join("|", keyColumn.Select(k => row[k]?.ToString() ?? ""));
                        sourceIds.Add(key);
                    }

                    // 4. Delete records not in source
                    var toDelete = existingIds.Except(sourceIds).ToList();
                    if (toDelete.Count > 0)
                    {
                        foreach (var idToDelete in toDelete)
                        {
                            var whereClauses = new List<string>();
                            var idParts = idToDelete.Split('|');
                            for (int i = 0; i < keyColumn.Length; i++)
                            {
                                whereClauses.Add($"\"{keyColumn[i]}\" = '{idParts[i]}'");
                            }
                            var where = string.Join(" AND ", whereClauses);
                            await using var deleteCmd = new NpgsqlCommand($"DELETE FROM \"{tableName}\" WHERE {where}", pgConn);
                            await deleteCmd.ExecuteNonQueryAsync();
                        }
                        tableResult.Deleted = toDelete.Count;
                        Console.WriteLine($"  Deleted: {toDelete.Count} records");
                    }

                    // 5. Upsert all source data using batch
                    var batchSize = 100;
                    var batches = sourceData.Chunk(batchSize).ToArray();
                    var upserted = 0;

                    foreach (var batch in batches)
                    {
                        var colNames = string.Join(", ", columns.Select(c => $"\"{c}\""));
                        var conflictCols = string.Join(", ", keyColumn.Select(k => $"\"{k}\""));
                        var updateCols = string.Join(", ", columns.Where(c => !keyColumn.Contains(c)).Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));

                        var sb = new StringBuilder();
                        sb.AppendLine($"INSERT INTO \"{tableName}\" ({colNames}) VALUES");

                        var values = new List<string>();
                        var paramIndex = 0;
                        var cmd = new NpgsqlCommand();
                        cmd.Connection = pgConn;

                        foreach (var row in batch)
                        {
                            var rowParams = new List<string>();
                            foreach (var col in columns)
                            {
                                var paramName = $"@p{paramIndex++}";
                                rowParams.Add(paramName);
                                cmd.Parameters.AddWithValue(paramName, row[col] ?? DBNull.Value);
                            }
                            values.Add($"({string.Join(", ", rowParams)})");
                        }

                        sb.AppendLine(string.Join(",\n", values));

                        if (updateCols.Length > 0)
                        {
                            sb.AppendLine($"ON CONFLICT ({conflictCols}) DO UPDATE SET {updateCols}");
                        }
                        else
                        {
                            sb.AppendLine($"ON CONFLICT ({conflictCols}) DO NOTHING");
                        }

                        cmd.CommandText = sb.ToString();
                        upserted += await cmd.ExecuteNonQueryAsync();
                        await _hubContext.Clients.All.SendAsync("ReceiveProgress", $"{tableName}: Uploading {upserted} of {sourceData.Count} records...");
                    }

                    tableResult.Upserted = upserted;
                    Console.WriteLine($"  Upserted: {upserted} records");
                }

                tableResult.Success = true;
            }
            catch (Exception ex)
            {
                tableResult.Success = false;
                tableResult.Error = ex.Message;
                Console.WriteLine($"  ERROR: {ex.Message}");
            }

            result.Tables.Add(tableResult);
        }

        /// <summary>
        /// Pull data from Supabase to SQL Server (Reverse Sync)
        /// </summary>
        public async Task<SyncResult> PullFromSupabaseAsync()
        {
            var result = new SyncResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Console.WriteLine("=== Starting PULL: Supabase -> SQL Server ===");

                // Pull in order of dependencies
                await PullTableAsync("AspNetRoles", new[] { "Id", "Name", "NormalizedName", "ConcurrencyStamp" }, result);
                await PullTableAsync("AspNetUsers", new[] {
                    "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                    "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
                    "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd",
                    "LockoutEnabled", "AccessFailedCount", "DisplayName", "DisplayImage"
                }, result);
                await PullTableAsync("AspNetUserRoles", new[] { "UserId", "RoleId" }, result, compositeKey: new[] { "UserId", "RoleId" });
                await PullTableAsync("Departments", new[] {
                    "Id", "Name", "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await PullTableAsync("Employees", new[] {
                    "Id", "Name", "TabCode", "TegaraCode", "Section", "Email", "Collage", "DepartmentId",
                    "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await PullTableAsync("Daily", new[] {
                    "Id", "Name", "DailyDate", "IsActive", "Closed", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await PullTableAsync("DailyReference", new[] {
                    "Id", "DailyId", "ReferencePath", "Description", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await PullTableAsync("Form", new[] {
                    "Id", "Name", "Description", "Index", "DailyId", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await PullTableAsync("FormDetails", new[] {
                    "Id", "FormId", "EmployeeId", "Amount", "OrderNum", "IsReviewed", "IsReviewedBy",
                    "ReviewedAt", "ReviewComments", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);
                await PullTableAsync("FormRefernce", new[] {
                    "Id", "FormId", "FilePath", "IsActive", "CreatedAt", "CreatedBy",
                    "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
                }, result);

                stopwatch.Stop();
                result.Success = true;
                result.Duration = stopwatch.Elapsed;
                Console.WriteLine($"=== PULL Completed in {stopwatch.Elapsed.TotalSeconds:F2}s ===");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.Error = ex.Message;
                result.Duration = stopwatch.Elapsed;
                Console.WriteLine($"PULL ERROR: {ex.Message}");
            }

            return result;
        }

        private async Task PullTableAsync(string tableName, string[] columns, SyncResult result, string[]? compositeKey = null)
        {
            Console.WriteLine($"Pulling {tableName}...");
            await _hubContext.Clients.All.SendAsync("ReceiveProgress", $"Pulling {tableName}...");
            var tableResult = new TableSyncResult { TableName = tableName };
            var keyColumn = compositeKey ?? new[] { "Id" };

            try
            {
                // 1. Read all data from Supabase (PostgreSQL)
                var sourceData = new List<Dictionary<string, object?>>();
                await using (var pgConn = new NpgsqlConnection(_supabaseConn))
                {
                    await pgConn.OpenAsync();
                    var columnsQuoted = string.Join(", ", columns.Select(c => $"\"{c}\""));
                    await using var cmd = new NpgsqlCommand($"SELECT {columnsQuoted} FROM \"{tableName}\"", pgConn);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (var col in columns)
                        {
                            var value = reader[col];
                            row[col] = value == DBNull.Value ? null : value;
                        }
                        sourceData.Add(row);
                    }
                }

                tableResult.SourceCount = sourceData.Count;
                Console.WriteLine($"  Source (Supabase): {sourceData.Count} records");

                if (sourceData.Count == 0)
                {
                    Console.WriteLine($"  No data to pull");
                    tableResult.Success = true;
                    result.Tables.Add(tableResult);
                    return;
                }

                // 2. Upsert to SQL Server using batched MERGE with temp table
                await using (var sqlConn = new SqlConnection(_sqlServerConn))
                {
                    await sqlConn.OpenAsync();

                    var hasIdentity = !tableName.StartsWith("AspNet");
                    var columnList = string.Join(", ", columns.Select(c => $"[{c}]"));
                    var updateList = string.Join(", ", columns.Where(c => !keyColumn.Contains(c)).Select(c => $"target.[{c}] = source.[{c}]"));
                    var keyCondition = string.Join(" AND ", keyColumn.Select(k => $"target.[{k}] = source.[{k}]"));

                    int upserted = 0;
                    var batchSize = 100;
                    var batches = sourceData.Chunk(batchSize).ToArray();

                    foreach (var batch in batches)
                    {
                        var sb = new StringBuilder();

                        // Create temp table with same structure
                        sb.AppendLine($"SELECT TOP 0 {columnList} INTO #TempPull FROM [{tableName}];");

                        // Insert batch rows into temp table
                        var paramIndex = 0;
                        var cmd = new SqlCommand();
                        cmd.Connection = sqlConn;

                        var valueRows = new List<string>();
                        foreach (var row in batch)
                        {
                            var rowParams = new List<string>();
                            foreach (var col in columns)
                            {
                                var paramName = $"@p{paramIndex++}";
                                rowParams.Add(paramName);
                                var value = row[col];
                                // Convert UTC DateTime to Local for SQL Server
                                if (value is DateTime dt && dt.Kind == DateTimeKind.Utc)
                                {
                                    value = dt.ToLocalTime();
                                }
                                cmd.Parameters.AddWithValue(paramName, value ?? DBNull.Value);
                            }
                            valueRows.Add($"({string.Join(", ", rowParams)})");
                        }

                        sb.AppendLine($"INSERT INTO #TempPull ({columnList}) VALUES");
                        sb.AppendLine(string.Join(",\n", valueRows) + ";");

                        // MERGE from temp table
                        if (hasIdentity) sb.AppendLine($"SET IDENTITY_INSERT [{tableName}] ON;");

                        sb.AppendLine($"MERGE [{tableName}] AS target");
                        sb.AppendLine($"USING #TempPull AS source");
                        sb.AppendLine($"ON {keyCondition}");

                        if (!string.IsNullOrEmpty(updateList))
                        {
                            sb.AppendLine($"WHEN MATCHED THEN UPDATE SET {updateList}");
                        }

                        var paramListForInsert = string.Join(", ", columns.Select(c => $"source.[{c}]"));
                        sb.AppendLine($"WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({paramListForInsert});");

                        if (hasIdentity) sb.AppendLine($"SET IDENTITY_INSERT [{tableName}] OFF;");

                        sb.AppendLine("DROP TABLE #TempPull;");

                        cmd.CommandText = sb.ToString();
                        upserted += await cmd.ExecuteNonQueryAsync();

                        await _hubContext.Clients.All.SendAsync("ReceiveProgress", $"{tableName}: Downloading {upserted} of {sourceData.Count} records...");
                    }

                    tableResult.Upserted = upserted;
                    Console.WriteLine($"  Upserted: {upserted} records");
                    await _hubContext.Clients.All.SendAsync("ReceiveProgress", $"{tableName}: Inserted/Updated {upserted} records");
                }

                tableResult.Success = true;
            }
            catch (Exception ex)
            {
                tableResult.Success = false;
                tableResult.Error = ex.Message;
                Console.WriteLine($"  ERROR: {ex.Message}");
            }

            result.Tables.Add(tableResult);
        }

        // Legacy method for compatibility
        public async Task MigrateToSupabaseAsync()
        {
            await FullSyncToSupabaseAsync(true); // Always force for legacy calls
        }

        private async Task<bool> CheckSupabaseIsNewerAsync()
        {
            try
            {
                // Compare Max(UpdatedAt) AND Max(CreatedAt) for critical tables
                var tables = new[] { "Daily", "Form" };

                foreach (var table in tables)
                {
                    DateTime? localMaxUpdate = null;
                    DateTime? remoteMaxUpdate = null;
                    DateTime? localMaxCreate = null;
                    DateTime? remoteMaxCreate = null;

                    // Get Local Max UpdatedAt & CreatedAt
                    await using (var sqlConn = new SqlConnection(_sqlServerConn))
                    {
                        await sqlConn.OpenAsync();
                        // Check UpdatedAt
                        var cmdUpdate = new SqlCommand($"SELECT MAX(UpdatedAt) FROM [{table}]", sqlConn);
                        var valUpdate = await cmdUpdate.ExecuteScalarAsync();
                        if (valUpdate != DBNull.Value) localMaxUpdate = (DateTime)valUpdate;

                        // Check CreatedAt
                        var cmdCreate = new SqlCommand($"SELECT MAX(CreatedAt) FROM [{table}]", sqlConn);
                        var valCreate = await cmdCreate.ExecuteScalarAsync();
                        if (valCreate != DBNull.Value) localMaxCreate = (DateTime)valCreate;
                    }

                    // Get Remote Max UpdatedAt & CreatedAt
                    await using (var pgConn = new NpgsqlConnection(_supabaseConn))
                    {
                        await pgConn.OpenAsync();
                        // Check UpdatedAt
                        var cmdUpdate = new NpgsqlCommand($"SELECT MAX(\"UpdatedAt\") FROM \"{table}\"", pgConn);
                        var valUpdate = await cmdUpdate.ExecuteScalarAsync();
                        if (valUpdate != DBNull.Value) remoteMaxUpdate = (DateTime)valUpdate;

                        // Check CreatedAt
                        var cmdCreate = new NpgsqlCommand($"SELECT MAX(\"CreatedAt\") FROM \"{table}\"", pgConn);
                        var valCreate = await cmdCreate.ExecuteScalarAsync();
                        if (valCreate != DBNull.Value) remoteMaxCreate = (DateTime)valCreate;
                    }

                    // 1. Check UpdatedAt Conflict
                    if (localMaxUpdate.HasValue && remoteMaxUpdate.HasValue)
                    {
                        if (remoteMaxUpdate.Value > localMaxUpdate.Value.AddMinutes(1))
                        {
                            Console.WriteLine($"[VersionCheck] {table} UpdatedAt: Remote ({remoteMaxUpdate}) is newer than Local ({localMaxUpdate})");
                            return true;
                        }
                    }

                    // 2. Check CreatedAt Conflict (New records added remotely)
                    if (localMaxCreate.HasValue && remoteMaxCreate.HasValue)
                    {
                        if (remoteMaxCreate.Value > localMaxCreate.Value.AddMinutes(1))
                        {
                            Console.WriteLine($"[VersionCheck] {table} CreatedAt: Remote ({remoteMaxCreate}) is newer than Local ({localMaxCreate})");
                            return true;
                        }
                    }
                    // Edge case: Remote has records but local has none (fresh local DB)
                    else if (!localMaxCreate.HasValue && remoteMaxCreate.HasValue)
                    {
                        Console.WriteLine($"[VersionCheck] {table}: Remote has records but Local is empty.");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VersionCheck] Error: {ex.Message}");
                return false;
            }
        }
    }

    public class SyncResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public TimeSpan Duration { get; set; }
        public List<TableSyncResult> Tables { get; set; } = new();
    }

    public class TableSyncResult
    {
        public string TableName { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int SourceCount { get; set; }
        public int Upserted { get; set; }
        public int Deleted { get; set; }
    }
}
