# Deterministic Data Integrity and Schema Verifier for IProgram Phase 4 Slice 4.2B
param(
    [string]$SourceConnectionString = "",
    [string]$TargetConnectionString = "",
    [ValidateSet("CaptureSource", "CaptureTarget", "Compare", "VerifyOperationalLocal")]
    [string]$Mode = "Compare",
    [string]$OutputJsonPath = ""
)

$ErrorActionPreference = "Stop"

# Ensure C# TableHasher type is compiled and loaded
if (-not ([System.Management.Automation.PSTypeName]'TableHasher').Type) {
    $hasherCode = @"
using System;
using System.IO;
using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Collections.Generic;

public class TableHasher
{
    public class TableAuditResult
    {
        public string SchemaName { get; set; }
        public string TableName { get; set; }
        public long RowCount { get; set; }
        public string Sha256Hash { get; set; }
        public string PrimaryKeyName { get; set; }
        public string PrimaryKeyColumns { get; set; }
        public int ColumnCount { get; set; }
        public List<string> ColumnNames { get; set; }
        public List<string> ColumnTypes { get; set; }
        public List<string> ColumnFullTypes { get; set; }
        public List<bool> ColumnNullability { get; set; }
        public bool HasIdentity { get; set; }
        public string IdentityColumn { get; set; }
        public string IdentCurrent { get; set; }
        public int ForeignKeyCount { get; set; }
        public List<string> ForeignKeyDefinitions { get; set; }
        public int IndexCount { get; set; }
        public List<string> IndexDefinitions { get; set; }
        public long SyncIdNullCount { get; set; }
        public long SyncIdDuplicateCount { get; set; }
        public double HashElapsedSeconds { get; set; }

        public TableAuditResult()
        {
            ColumnNames = new List<string>();
            ColumnTypes = new List<string>();
            ColumnFullTypes = new List<string>();
            ColumnNullability = new List<bool>();
            ForeignKeyDefinitions = new List<string>();
            IndexDefinitions = new List<string>();
        }
    }

    public static TableAuditResult AuditAndHashTable(string connectionString, string schemaName, string tableName, string pkColumns, bool isSyncable)
    {
        var result = new TableAuditResult
        {
            SchemaName = schemaName,
            TableName = tableName
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using (var conn = new SqlConnection(connectionString))
        {
            conn.Open();

            // 1. Column Inventory, Types, Lengths, Nullability, Identity
            using (var cmdCols = conn.CreateCommand())
            {
                cmdCols.CommandText = @"
                    SELECT 
                        c.name, 
                        tp.name AS type_name, 
                        c.max_length,
                        c.precision,
                        c.scale,
                        c.is_nullable,
                        c.is_identity
                    FROM sys.columns c
                    INNER JOIN sys.tables t ON c.object_id = t.object_id
                    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                    INNER JOIN sys.types tp ON c.user_type_id = tp.user_type_id
                    WHERE s.name = @schema AND t.name = @table
                    ORDER BY c.column_id";
                cmdCols.Parameters.AddWithValue("@schema", schemaName);
                cmdCols.Parameters.AddWithValue("@table", tableName);
                using (var reader = cmdCols.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var colName = reader.GetString(0);
                        var typeName = reader.GetString(1);
                        var maxLen = reader.GetInt16(2);
                        var prec = reader.GetByte(3);
                        var scale = reader.GetByte(4);
                        var isNull = reader.GetBoolean(5);
                        var isIdent = reader.GetBoolean(6);

                        result.ColumnNames.Add(colName);
                        result.ColumnTypes.Add(typeName);
                        result.ColumnNullability.Add(isNull);
                        result.ColumnFullTypes.Add(string.Format("{0}|{1}|len:{2}|prec:{3}|scale:{4}|null:{5}|ident:{6}",
                            colName, typeName, maxLen, prec, scale, isNull, isIdent));

                        if (isIdent)
                        {
                            result.HasIdentity = true;
                            result.IdentityColumn = colName;
                        }
                    }
                }
            }
            result.ColumnCount = result.ColumnNames.Count;

            // 2. Foreign Keys with Complete Relationship & Cascade Actions
            using (var cmdFk = conn.CreateCommand())
            {
                cmdFk.CommandText = @"
                    SELECT 
                        fk.name AS FkName,
                        c_parent.name AS ParentCol,
                        s_ref.name AS RefSchema,
                        t_ref.name AS RefTable,
                        c_ref.name AS RefCol,
                        fk.delete_referential_action_desc AS DeleteAction,
                        fk.update_referential_action_desc AS UpdateAction
                    FROM sys.foreign_keys fk
                    INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
                    INNER JOIN sys.tables t ON fk.parent_object_id = t.object_id
                    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                    INNER JOIN sys.tables t_ref ON fk.referenced_object_id = t_ref.object_id
                    INNER JOIN sys.schemas s_ref ON t_ref.schema_id = s_ref.schema_id
                    INNER JOIN sys.columns c_parent ON fkc.parent_object_id = c_parent.object_id AND fkc.parent_column_id = c_parent.column_id
                    INNER JOIN sys.columns c_ref ON fkc.referenced_object_id = c_ref.object_id AND fkc.referenced_column_id = c_ref.column_id
                    WHERE s.name = @schema AND t.name = @table
                    ORDER BY c_parent.name, s_ref.name, t_ref.name, c_ref.name";
                cmdFk.Parameters.AddWithValue("@schema", schemaName);
                cmdFk.Parameters.AddWithValue("@table", tableName);
                using (var reader = cmdFk.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var fkDef = string.Format("{0} -> [{1}].[{2}]({3}) [DEL:{4} UPD:{5}]",
                            reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
                        result.ForeignKeyDefinitions.Add(fkDef);
                    }
                }
                result.ForeignKeyCount = result.ForeignKeyDefinitions.Count;
            }

            // 3. Index Definitions (Detecting Drift in Keys, Ordering, Includes, Filter, Uniqueness)
            using (var cmdIdx = conn.CreateCommand())
            {
                cmdIdx.CommandText = @"
                    SELECT 
                        i.name AS IndexName,
                        i.type_desc AS IndexType,
                        i.is_unique AS IsUnique,
                        i.has_filter AS HasFilter,
                        ISNULL(i.filter_definition, '') AS FilterDef,
                        ISNULL(
                            STUFF((
                                SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
                                FROM sys.index_columns ic
                                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                                ORDER BY ic.key_ordinal
                                FOR XML PATH('')
                            ), 1, 2, ''),
                            ''
                        ) AS KeyCols,
                        ISNULL(
                            STUFF((
                                SELECT ', ' + c.name
                                FROM sys.index_columns ic
                                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                                ORDER BY ic.index_column_id
                                FOR XML PATH('')
                            ), 1, 2, ''),
                            ''
                        ) AS IncludedCols
                    FROM sys.indexes i
                    INNER JOIN sys.tables t ON i.object_id = t.object_id
                    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = @schema AND t.name = @table AND i.type > 0
                    ORDER BY KeyCols, IncludedCols";
                cmdIdx.Parameters.AddWithValue("@schema", schemaName);
                cmdIdx.Parameters.AddWithValue("@table", tableName);
                using (var reader = cmdIdx.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var idxDef = string.Format("TYPE:{0}|UQ:{1}|KEYS:[{2}]|INC:[{3}]|FILTER:{4}",
                            reader.GetString(1), reader.GetBoolean(2), reader.GetString(5), reader.GetString(6), reader.GetString(4));
                        result.IndexDefinitions.Add(idxDef);
                    }
                }
                result.IndexCount = result.IndexDefinitions.Count;
            }

            // 4. Identity Value (IDENT_CURRENT)
            if (result.HasIdentity)
            {
                using (var cmdId = conn.CreateCommand())
                {
                    cmdId.CommandText = string.Format("SELECT IDENT_CURRENT('[{0}].[{1}]')", schemaName, tableName);
                    var val = cmdId.ExecuteScalar();
                    result.IdentCurrent = (val == DBNull.Value || val == null) ? null : Convert.ToString(val, CultureInfo.InvariantCulture);
                }
            }

            // 5. SyncId Checks (if syncable)
            if (isSyncable && result.ColumnNames.Contains("SyncId"))
            {
                using (var cmdSync = conn.CreateCommand())
                {
                    cmdSync.CommandText = string.Format(@"
                        SELECT 
                            SUM(CASE WHEN SyncId IS NULL OR SyncId = '00000000-0000-0000-0000-000000000000' THEN 1 ELSE 0 END) AS NullCount,
                            COUNT(SyncId) - COUNT(DISTINCT SyncId) AS DupCount
                        FROM [{0}].[{1}]", schemaName, tableName);
                    using (var reader = cmdSync.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            result.SyncIdNullCount = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0));
                            result.SyncIdDuplicateCount = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1));
                        }
                    }
                }
            }

            // 6. Deterministic Streaming Hash
            var colListSql = "[" + string.Join("], [", result.ColumnNames) + "]";
            var orderBySql = string.IsNullOrWhiteSpace(pkColumns) ? colListSql : pkColumns;

            using (var cmdData = conn.CreateCommand())
            {
                cmdData.CommandTimeout = 300;
                cmdData.CommandText = string.Format("SELECT {0} FROM [{1}].[{2}] ORDER BY {3}", colListSql, schemaName, tableName, orderBySql);

                using (var sha = SHA256.Create())
                using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                using (var bw = new BinaryWriter(cs, Encoding.UTF8, true))
                {
                    using (var reader = cmdData.ExecuteReader())
                    {
                        long rowCount = 0;
                        int colCount = result.ColumnNames.Count;

                        while (reader.Read())
                        {
                            rowCount++;
                            bw.Write((byte)0xFF); // Row marker

                            for (int i = 0; i < colCount; i++)
                            {
                                if (reader.IsDBNull(i))
                                {
                                    bw.Write((byte)0x00); // NULL marker
                                    continue;
                                }

                                var typeName = result.ColumnTypes[i];
                                switch (typeName)
                                {
                                    case "bit":
                                        bw.Write((byte)0x01);
                                        bw.Write(reader.GetBoolean(i));
                                        break;
                                    case "int":
                                        bw.Write((byte)0x02);
                                        bw.Write(reader.GetInt32(i));
                                        break;
                                    case "bigint":
                                        bw.Write((byte)0x03);
                                        bw.Write(reader.GetInt64(i));
                                        break;
                                    case "float":
                                        bw.Write((byte)0x04);
                                        bw.Write(reader.GetDouble(i));
                                        break;
                                    case "uniqueidentifier":
                                        bw.Write((byte)0x05);
                                        bw.Write(reader.GetGuid(i).ToByteArray());
                                        break;
                                    case "datetime2":
                                    case "datetime":
                                        bw.Write((byte)0x06);
                                        var dt = reader.GetDateTime(i);
                                        bw.Write(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                                        break;
                                    case "datetimeoffset":
                                        bw.Write((byte)0x07);
                                        var dto = (DateTimeOffset)reader.GetValue(i);
                                        bw.Write(dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff+00:00", CultureInfo.InvariantCulture));
                                        break;
                                    case "nvarchar":
                                    case "varchar":
                                    case "nchar":
                                    case "char":
                                    case "text":
                                    case "ntext":
                                        bw.Write((byte)0x08);
                                        bw.Write(reader.GetString(i));
                                        break;
                                    case "varbinary":
                                    case "binary":
                                    case "image":
                                        bw.Write((byte)0x09);
                                        var bytes = (byte[])reader.GetValue(i);
                                        bw.Write(bytes.Length);
                                        bw.Write(bytes);
                                        break;
                                    default:
                                        bw.Write((byte)0x0A);
                                        bw.Write(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
                                        break;
                                }
                            }
                        }

                        result.RowCount = rowCount;
                    }

                    bw.Flush();
                    cs.FlushFinalBlock();
                    result.Sha256Hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToUpperInvariant();
                }
            }
        }

        sw.Stop();
        result.HashElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }
}
"@
    Add-Type -TypeDefinition $hasherCode -ReferencedAssemblies "System.Data", "System.Xml"
}

$syncableEntities = @(
    "Daily",
    "DailyReference",
    "Departments",
    "EmployeeBank",
    "EmployeeNetPays",
    "EmployeeRefernce",
    "Employees",
    "EmployeeWatchLists",
    "Form",
    "FormDetails",
    "FormRefernce"
)

function Get-DatabaseTableList($cs) {
    Add-Type -AssemblyName 'System.Data'
    $conn = New-Object System.Data.SqlClient.SqlConnection($cs)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 120
    $cmd.CommandText = @"
SELECT 
    s.name AS SchemaName,
    t.name AS TableName,
    ISNULL(pk.name, '') AS PrimaryKeyName,
    ISNULL(
        STUFF((
            SELECT ', ' + c.name
            FROM sys.index_columns ic
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE ic.object_id = pk.parent_object_id AND ic.index_id = pk.unique_index_id
            ORDER BY ic.key_ordinal
            FOR XML PATH('')
        ), 1, 2, ''),
        ''
    ) AS KeyColumns
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
LEFT JOIN sys.key_constraints pk ON t.object_id = pk.parent_object_id AND pk.type = 'PK'
ORDER BY s.name, t.name
"@
    $tables = @()
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) {
        $tables += [PSCustomObject]@{
            SchemaName = $reader["SchemaName"]
            TableName = $reader["TableName"]
            PrimaryKeyName = $reader["PrimaryKeyName"]
            KeyColumns = $reader["KeyColumns"]
        }
    }
    $reader.Close()
    $conn.Close()
    return $tables
}

if ([string]::IsNullOrWhiteSpace($SourceConnectionString)) {
    # Attempt to retrieve from User Secrets in-memory without printing
    $apiProj = Resolve-Path (Join-Path $PSScriptRoot "../../src/Api/Auth.Api.csproj") -ErrorAction SilentlyContinue
    if ($apiProj) {
        $secrets = dotnet user-secrets list --project $apiProj 2>$null
        foreach ($line in $secrets) {
            if ($line.StartsWith("ConnectionStrings:DefaultConnection = ")) {
                $SourceConnectionString = $line.Substring("ConnectionStrings:DefaultConnection = ".Length).Trim()
                break
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($TargetConnectionString) -and ($Mode -ne "CaptureSource")) {
    $TargetConnectionString = "Server=localhost;Database=IProgramLocalDb2026;Integrated Security=True;TrustServerCertificate=True;"
}

function Audit-Database($cs, $label, [bool]$ignoreLocalSyncTables = $false) {
    Write-Host "`nAuditing database [$label]..." -ForegroundColor Cyan
    $tableList = Get-DatabaseTableList $cs
    $results = [ordered]@{}
    $totalRows = 0

    $localOnlySyncTables = @(
        "sync.LocalOutbox",
        "sync.LocalState",
        "sync.BootstrapManifest",
        "sync.__EFMigrationsHistory_LocalSync"
    )

    $filteredTables = @()
    foreach ($t in $tableList) {
        $fullName = "$($t.SchemaName).$($t.TableName)"
        if ($ignoreLocalSyncTables -and ($localOnlySyncTables -contains $fullName)) {
            Write-Host "  Ignoring local-only metadata table: $fullName" -ForegroundColor DarkGray
            continue
        }
        $filteredTables += $t
    }

    foreach ($t in $filteredTables) {
        $fullName = "$($t.SchemaName).$($t.TableName)"
        $isSyncable = $syncableEntities -contains $t.TableName
        $pkColsFormatted = ""
        if (-not [string]::IsNullOrWhiteSpace($t.KeyColumns)) {
            $pkColsFormatted = "[" + ($t.KeyColumns -split ", " -join "], [") + "]"
        }

        Write-Host "  Hashing $fullName..." -NoNewline
        $audit = [TableHasher]::AuditAndHashTable($cs, $t.SchemaName, $t.TableName, $pkColsFormatted, $isSyncable)
        $audit.PrimaryKeyName = $t.PrimaryKeyName
        $audit.PrimaryKeyColumns = $t.KeyColumns
        $results[$fullName] = $audit
        $totalRows += $audit.RowCount
        Write-Host " $($audit.RowCount) rows, Hash: $($audit.Sha256Hash) ($([math]::Round($audit.HashElapsedSeconds, 2))s)" -ForegroundColor Green
    }

    return [ordered]@{
        Label = $label
        TableCount = $filteredTables.Count
        TotalRows = $totalRows
        Tables = $results
    }
}

$report = [ordered]@{
    ReportType = "DataIntegrityAuditReport"
    Slice = "4.2B"
    GeneratedUtc = (Get-Date).ToUniversalTime().ToString("o")
    Mode = $Mode
    OverallStatus = "UNKNOWN"
}

if ($Mode -eq "CaptureSource") {
    $sourceAudit = Audit-Database $SourceConnectionString "Source_Azure_2026" $false
    $report.Source = $sourceAudit
    $report.OverallStatus = "CAPTURED"
}
elseif ($Mode -eq "CaptureTarget") {
    $targetAudit = Audit-Database $TargetConnectionString "Target_Local_2026" $true
    $report.Target = $targetAudit
    $report.OverallStatus = "CAPTURED"
}
elseif ($Mode -eq "Compare") {
    Write-Host "Starting Comparative Verification between Source (Azure) and Target (Local)..." -ForegroundColor Yellow
    $sourceAudit = Audit-Database $SourceConnectionString "Source_Azure_2026" $false
    $targetAudit = Audit-Database $TargetConnectionString "Target_Local_2026" $true

    $report.SourceSummary = [ordered]@{ TableCount = $sourceAudit.TableCount; TotalRows = $sourceAudit.TotalRows }
    $report.TargetSummary = [ordered]@{ TableCount = $targetAudit.TableCount; TotalRows = $targetAudit.TotalRows }

    $comparisons = [ordered]@{}
    $allPassed = $true
    $mismatchCount = 0

    # Verify table count
    if ($sourceAudit.TableCount -ne $targetAudit.TableCount) {
        $allPassed = $false
        Write-Warning "Table count mismatch! Source: $($sourceAudit.TableCount), Target: $($targetAudit.TableCount)"
    }

    foreach ($tableName in $sourceAudit.Tables.Keys) {
        $src = $sourceAudit.Tables[$tableName]
        $tgt = $targetAudit.Tables[$tableName]

        $tableStatus = "PASS"
        $mismatchDetails = @()

        if (-not $tgt) {
            $tableStatus = "FAIL_MISSING_IN_TARGET"
            $mismatchDetails += "Table missing in target"
            $allPassed = $false
            $mismatchCount++
        }
        else {
            # 1. Row count
            if ($src.RowCount -ne $tgt.RowCount) {
                $tableStatus = "FAIL_ROWCOUNT_MISMATCH"
                $mismatchDetails += "RowCount source ($($src.RowCount)) != target ($($tgt.RowCount))"
                $allPassed = $false
                $mismatchCount++
            }

            # 2. SHA-256 Data Hash
            if ($src.Sha256Hash -ne $tgt.Sha256Hash) {
                $tableStatus = "FAIL_HASH_MISMATCH"
                $mismatchDetails += "SHA256 source ($($src.Sha256Hash)) != target ($($tgt.Sha256Hash))"
                $allPassed = $false
                $mismatchCount++
            }

            # 3. Column Count
            if ($src.ColumnCount -ne $tgt.ColumnCount) {
                $mismatchDetails += "ColumnCount source ($($src.ColumnCount)) != target ($($tgt.ColumnCount))"
                $allPassed = $false
                $mismatchCount++
            }

            # 4. Ordered Column Names
            $srcCols = $src.ColumnNames -join ","
            $tgtCols = $tgt.ColumnNames -join ","
            if ($srcCols -ne $tgtCols) {
                $mismatchDetails += "ColumnNames mismatch: source ($srcCols) != target ($tgtCols)"
                $allPassed = $false
                $mismatchCount++
            }

            # 5. Exact Column Types, Max Lengths, Precision, Scale, Nullability, Identity
            $srcColTypes = $src.ColumnFullTypes -join "; "
            $tgtColTypes = $tgt.ColumnFullTypes -join "; "
            if ($srcColTypes -ne $tgtColTypes) {
                $mismatchDetails += "ColumnFullTypes mismatch: source ($srcColTypes) != target ($tgtColTypes)"
                $allPassed = $false
                $mismatchCount++
            }

            # 6. Identity Column Presence & Name
            if ($src.HasIdentity -ne $tgt.HasIdentity -or $src.IdentityColumn -ne $tgt.IdentityColumn) {
                $mismatchDetails += "Identity column mismatch: source (HasIdent=$($src.HasIdentity), Col=$($src.IdentityColumn)) != target (HasIdent=$($tgt.HasIdentity), Col=$($tgt.IdentityColumn))"
                $allPassed = $false
                $mismatchCount++
            }

            # 7. Identity Current Value (IDENT_CURRENT) - Enforced Gate Failure
            if ($src.HasIdentity) {
                if ($src.IdentCurrent -ne $tgt.IdentCurrent) {
                    $mismatchDetails += "IDENT_CURRENT mismatch: source ($($src.IdentCurrent)) != target ($($tgt.IdentCurrent))"
                    $allPassed = $false
                    $mismatchCount++
                }
            }

            # 8. Primary Key Columns
            if ($src.PrimaryKeyColumns -ne $tgt.PrimaryKeyColumns) {
                $mismatchDetails += "PK Columns source ($($src.PrimaryKeyColumns)) != target ($($tgt.PrimaryKeyColumns))"
                $allPassed = $false
                $mismatchCount++
            }

            # 9. Foreign Key Definitions (Parent Column, Ref Table, Ref Column, Delete/Update Actions)
            $srcFk = ($src.ForeignKeyDefinitions | Sort-Object) -join "; "
            $tgtFk = ($tgt.ForeignKeyDefinitions | Sort-Object) -join "; "
            if ($srcFk -ne $tgtFk) {
                $mismatchDetails += "ForeignKeyDefinitions mismatch: source ($srcFk) != target ($tgtFk)"
                $allPassed = $false
                $mismatchCount++
            }

            # 10. Index Definitions (Key Columns, Order ASC/DESC, Included Columns, Uniqueness, Filter)
            $srcIdx = ($src.IndexDefinitions | Sort-Object) -join "; "
            $tgtIdx = ($tgt.IndexDefinitions | Sort-Object) -join "; "
            if ($srcIdx -ne $tgtIdx) {
                $mismatchDetails += "IndexDefinitions mismatch: source ($srcIdx) != target ($tgtIdx)"
                $allPassed = $false
                $mismatchCount++
            }

            # 11. SyncId Invariants
            if ($src.SyncIdNullCount -ne $tgt.SyncIdNullCount -or $tgt.SyncIdNullCount -gt 0) {
                $mismatchDetails += "SyncId nulls detected: $($tgt.SyncIdNullCount)"
                $allPassed = $false
                $mismatchCount++
            }
            if ($src.SyncIdDuplicateCount -ne $tgt.SyncIdDuplicateCount -or $tgt.SyncIdDuplicateCount -gt 0) {
                $mismatchDetails += "SyncId duplicates detected: $($tgt.SyncIdDuplicateCount)"
                $allPassed = $false
                $mismatchCount++
            }
        }

        if ($mismatchDetails.Count -gt 0 -and $tableStatus -eq "PASS") {
            $tableStatus = "FAIL_SCHEMA_DRIFT"
        }

        $comparisons[$tableName] = [ordered]@{
            Status = $tableStatus
            SourceRowCount = $src.RowCount
            TargetRowCount = if ($tgt) { $tgt.RowCount } else { $null }
            SourceHash = $src.Sha256Hash
            TargetHash = if ($tgt) { $tgt.Sha256Hash } else { $null }
            SourceIdentCurrent = $src.IdentCurrent
            TargetIdentCurrent = if ($tgt) { $tgt.IdentCurrent } else { $null }
            IdentMatch = if ($tgt) { ($src.IdentCurrent -eq $tgt.IdentCurrent) } else { $false }
            ForeignKeyCount = $src.ForeignKeyCount
            IndexCount = $src.IndexCount
            Mismatches = $mismatchDetails
        }
    }

    $report.Comparisons = $comparisons
    $report.MismatchCount = $mismatchCount
    $report.OverallStatus = if ($allPassed) { "PASS" } else { "FAIL" }

    Write-Host "`n=== COMPARISON SUMMARY ===" -ForegroundColor Cyan
    Write-Host "Overall Status: $($report.OverallStatus)" -ForegroundColor $(if ($allPassed) { "Green" } else { "Red" })
    Write-Host "Total Tables Compared: $($sourceAudit.TableCount)"
    Write-Host "Total Rows: Source=$($sourceAudit.TotalRows), Target=$($targetAudit.TotalRows)"
    Write-Host "Mismatches: $mismatchCount"
}
elseif ($Mode -eq "VerifyOperationalLocal") {
    Write-Host "Starting Operational Local Database Verification on $TargetConnectionString..." -ForegroundColor Yellow
    
    $expectedAzureOnlyTables = @(
        "sync.ServerState",
        "sync.ServerChangeFeed",
        "sync.Tombstones",
        "sync.ProcessedOperations",
        "sync.__EFMigrationsHistory_AzureSync"
    )

    $expectedLocalOnlyTables = @(
        "sync.LocalOutbox",
        "sync.LocalState",
        "sync.BootstrapManifest",
        "sync.__EFMigrationsHistory_LocalSync"
    )

    $allLocalTables = Get-DatabaseTableList $TargetConnectionString
    $allLocalTableNames = $allLocalTables | ForEach-Object { "$($_.SchemaName).$($_.TableName)" }

    $operationalCheck = [ordered]@{
        AzureOnlyTablesCleanedUp = $true
        LocalSyncTablesPresent = $true
        Violations = @()
    }

    foreach ($azTbl in $expectedAzureOnlyTables) {
        if ($allLocalTableNames -contains $azTbl) {
            $operationalCheck.AzureOnlyTablesCleanedUp = $false
            $operationalCheck.Violations += "Azure-only sync table still present in operational local DB: $azTbl"
        }
    }

    foreach ($locTbl in $expectedLocalOnlyTables) {
        if (-not ($allLocalTableNames -contains $locTbl)) {
            $operationalCheck.LocalSyncTablesPresent = $false
            $operationalCheck.Violations += "Required local sync table missing: $locTbl"
        }
    }

    # Audit the remaining tables (which should be the 19 business tables + 4 local sync tables)
    $localAudit = Audit-Database $TargetConnectionString "Operational_Local_2026" $true
    $report.OperationalCheck = $operationalCheck
    $report.OperationalLocalAudit = $localAudit
    $report.OverallStatus = if ($operationalCheck.AzureOnlyTablesCleanedUp -and $operationalCheck.LocalSyncTablesPresent) { "PASS" } else { "FAIL" }
    
    Write-Host "`n=== OPERATIONAL LOCAL SUMMARY ===" -ForegroundColor Cyan
    Write-Host "Azure-Only Tables Removed: $($operationalCheck.AzureOnlyTablesCleanedUp)"
    Write-Host "Local-Only Sync Tables Present: $($operationalCheck.LocalSyncTablesPresent)"
    Write-Host "Business Tables Count: $($localAudit.TableCount)"
    Write-Host "Total Business Rows: $($localAudit.TotalRows)"
    Write-Host "Overall Status: $($report.OverallStatus)" -ForegroundColor $(if ($report.OverallStatus -eq "PASS") { "Green" } else { "Red" })
}

if ($OutputJsonPath) {
    $parentDir = Split-Path -Parent $OutputJsonPath
    if (-not (Test-Path $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    $json = $report | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($OutputJsonPath, $json, [System.Text.Encoding]::UTF8)
    Write-Host "Report saved to: $OutputJsonPath" -ForegroundColor Green
}

if ($report.OverallStatus -eq "FAIL") {
    exit 1
} else {
    exit 0
}
