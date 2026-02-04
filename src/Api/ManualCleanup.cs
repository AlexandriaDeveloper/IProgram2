using Microsoft.Extensions.Configuration;
using Npgsql;
using System;

public static class ManualCleanup
{
    public static void Execute(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SupabaseConnection");
        Console.WriteLine($"Cleanup: Connecting to Supabase...");

        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();
            using (var cmd = new NpgsqlCommand())
            {
                cmd.Connection = conn;
                // Truncate main tables in correct order or usage CASCADE to handle FKs
                // Tables to clean: AspNetUsers, Daily, Departments, Employees, Form, ...
                // Safe to truncate all?
                // Just use CASCADE on the roots.
                
                var sql = @"
                    TRUNCATE TABLE 
                        ""AspNetUsers"", 
                        ""AspNetRoles"", 
                        ""Daily"", 
                        ""Departments"" 
                    RESTART IDENTITY CASCADE;
                ";
                
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300; 
                
                Console.WriteLine("Executing Cleanup SQL (TRUNCATE CASCADE)...");
                cmd.ExecuteNonQuery();
                Console.WriteLine("Cleanup Executed Successfully!");
            }
        }
    }
}
