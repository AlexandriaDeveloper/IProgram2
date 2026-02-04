using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using System.Text;

namespace Auth.Infrastructure.Services
{
    public class DataMigrationService
    {
        private readonly string _sqlServerConn;
        private readonly string _supabaseConn;

        public DataMigrationService(Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _sqlServerConn = configuration.GetConnectionString("DefaultConnection") 
                ?? "Server=localhost,1433;Database=IProgramDb;User Id=sa;Password=123;TrustServerCertificate=True;Encrypt=False";
            _supabaseConn = configuration.GetConnectionString("SupabaseConnection") 
                ?? "Host=aws-1-eu-west-3.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.iztxgikxmcpzoqowomtp;Password=FNsGxA0IN0qzqSDC;SSL Mode=Require;Trust Server Certificate=true;";
        }

        public async Task<SyncResult> FullSyncToSupabaseAsync()
        {
            var result = new SyncResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Console.WriteLine("=== Starting FULL SYNC: SQL Server -> Supabase ===");

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

        // Legacy method for compatibility
        public async Task MigrateToSupabaseAsync()
        {
            await FullSyncToSupabaseAsync();
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
