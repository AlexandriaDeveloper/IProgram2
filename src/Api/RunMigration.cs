using Microsoft.Data.SqlClient;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;

public static class RunMigration
{
    private static string SqlServerConn = "Server=localhost,1433;Database=IProgramDb;User Id=sa;Password=123;TrustServerCertificate=True;Encrypt=False";
    private static string SupabaseConn = "Host=aws-1-eu-west-3.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.iztxgikxmcpzoqowomtp;Password=FNsGxA0IN0qzqSDC;SSL Mode=Require;Trust Server Certificate=true;";

    public static void Execute()
    {
        Console.WriteLine("=== Starting Data Migration: SQL Server -> Supabase ===");
        
        try
        {
            // 1. Migrate Roles
            MigrateTable("AspNetRoles", new[] { "Id", "Name", "NormalizedName", "ConcurrencyStamp" });
            
            // 2. Migrate Users
            MigrateTable("AspNetUsers", new[] { 
                "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", 
                "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
                "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd",
                "LockoutEnabled", "AccessFailedCount", "DisplayName", "DisplayImage"
            });
            
            // 3. Migrate UserRoles
            MigrateTable("AspNetUserRoles", new[] { "UserId", "RoleId" });
            
            // 4. Migrate Departments
            MigrateTable("Departments", new[] { 
                "Id", "Name", "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy" 
            });
            
            // 5. Migrate Employees
            MigrateTable("Employees", new[] { 
                "Id", "Name", "TabCode", "TegaraCode", "Section", "Email", "Collage", "DepartmentId",
                "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
            });
            
            // 6. Migrate Daily
            MigrateTable("Daily", new[] { 
                "Id", "Name", "RecordDate", "IsActive", "IsClosed", "CreatedAt", "CreatedBy", 
                "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
            });
            
            // 7. Migrate DailyReferences
            MigrateTable("DailyReferences", new[] { 
                "Id", "DailyId", "Name", "FilePath", "IsActive", "CreatedAt", "CreatedBy", 
                "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
            });
            
            // 8. Migrate Form
            MigrateTable("Form", new[] { 
                "Id", "Name", "Description", "Index", "DailyId", "IsActive", "CreatedAt", "CreatedBy", 
                "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
            });
            
            // 9. Migrate FormDetails
            MigrateTable("FormDetails", new[] { 
                "Id", "FormId", "EmployeeId", "Amount", "OrderNum", "IsReviewed", "IsReviewedBy",
                "ReviewedAt", "ReviewComments", "IsActive", "CreatedAt", "CreatedBy", 
                "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy"
            });
            
            Console.WriteLine("=== Migration Completed Successfully! ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static void MigrateTable(string tableName, string[] columns)
    {
        Console.WriteLine($"Migrating {tableName}...");
        
        var data = new List<Dictionary<string, object>>();
        
        // Read from SQL Server
        using (var sqlConn = new SqlConnection(SqlServerConn))
        {
            sqlConn.Open();
            var selectCmd = new SqlCommand($"SELECT * FROM [{tableName}]", sqlConn);
            using var reader = selectCmd.ExecuteReader();
            
            while (reader.Read())
            {
                var row = new Dictionary<string, object>();
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
                data.Add(row);
            }
        }
        
        Console.WriteLine($"  Found {data.Count} records in SQL Server");
        
        if (data.Count == 0) return;
        
        // Write to Supabase (PostgreSQL)
        using (var pgConn = new NpgsqlConnection(SupabaseConn))
        {
            pgConn.Open();
            
            foreach (var row in data)
            {
                var colNames = string.Join(", ", columns.Select(c => $"\"{c}\""));
                var paramNames = string.Join(", ", columns.Select((c, i) => $"@p{i}"));
                var insertSql = $"INSERT INTO \"{tableName}\" ({colNames}) VALUES ({paramNames}) ON CONFLICT DO NOTHING";
                
                using var cmd = new NpgsqlCommand(insertSql, pgConn);
                for (int i = 0; i < columns.Length; i++)
                {
                    var value = row[columns[i]];
                    cmd.Parameters.AddWithValue($"@p{i}", value ?? DBNull.Value);
                }
                
                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error inserting row: {ex.Message}");
                }
            }
        }
        
        Console.WriteLine($"  Migrated {data.Count} records to Supabase");
    }
}
