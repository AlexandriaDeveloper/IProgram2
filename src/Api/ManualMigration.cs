using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.IO;

public static class ManualMigration
{
    public static void Execute(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SupabaseConnection");
        Console.WriteLine($"Migration: Connecting to {connectionString}...");

        using (var conn = new NpgsqlConnection(connectionString))
        {
            conn.Open();
            // Read the SQL file
            var sqlFile = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "migration.sql"); 
            // Note: CWD is src/Api, so ..\..\migration.sql gets to root.
            
            if (!File.Exists(sqlFile))
            {
               // Fallback: try relative to where we think it is
               sqlFile = Path.Combine(Directory.GetCurrentDirectory(), "migration.sql");
               if (!File.Exists(sqlFile))
               {
                   // Fallback 2: absolute path
                   sqlFile = @"f:\Prog-Projects\IProgram\migration.sql";
               }
            }

            Console.WriteLine($"Reading SQL from {sqlFile}");
            var sql = File.ReadAllText(sqlFile);

            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.CommandTimeout = 300; // 5 minutes
                Console.WriteLine("Executing Migration SQL...");
                cmd.ExecuteNonQuery();
                Console.WriteLine("Migration SQL Executed Successfully!");
            }
        }
    }
}
