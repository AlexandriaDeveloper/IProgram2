using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Sync
{
    public class AzureSyncContext : DbContext
    {
        public const string MigrationsHistoryTableName = "__EFMigrationsHistory_AzureSync";
        public const string MigrationsHistoryTableSchema = "sync";

        public DbSet<ServerState> ServerStates { get; set; }
        public DbSet<ServerChangeFeed> ServerChangeFeeds { get; set; }
        public DbSet<ServerTombstone> Tombstones { get; set; }
        public DbSet<ProcessedOperation> ProcessedOperations { get; set; }

        public AzureSyncContext(DbContextOptions<AzureSyncContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.HasDefaultSchema("sync");

            builder.Entity<ServerState>(entity =>
            {
                entity.ToTable("ServerState");
                entity.HasKey(e => e.DatabaseId);
                entity.Property(e => e.DatabaseId).HasMaxLength(32);
            });

            builder.Entity<ServerChangeFeed>(entity =>
            {
                entity.ToTable("ServerChangeFeed");
                entity.HasKey(e => e.FeedId);
                entity.Property(e => e.DatabaseId).HasMaxLength(32).IsRequired();
                entity.Property(e => e.EntityType).HasMaxLength(50).IsRequired();
                entity.Property(e => e.OperationType).HasMaxLength(20).IsRequired();
                entity.HasIndex(e => new { e.DatabaseId, e.ServerVersion })
                    .HasDatabaseName("IX_ServerChangeFeed_Pull");
            });

            builder.Entity<ServerTombstone>(entity =>
            {
                entity.ToTable("Tombstones");
                entity.HasKey(e => new { e.DatabaseId, e.EntityType, e.EntitySyncId });
                entity.Property(e => e.DatabaseId).HasMaxLength(32);
                entity.Property(e => e.EntityType).HasMaxLength(50);
                entity.Property(e => e.NaturalKey).HasMaxLength(50);
                entity.HasIndex(e => new { e.DatabaseId, e.ServerVersion })
                    .HasDatabaseName("IX_Tombstones_Pull");
            });

            builder.Entity<ProcessedOperation>(entity =>
            {
                entity.ToTable("ProcessedOperations");
                entity.HasKey(e => new { e.DatabaseId, e.ClientOperationId });
                entity.Property(e => e.DatabaseId).HasMaxLength(32);
                entity.Property(e => e.CommandName).HasMaxLength(100).IsRequired();
                entity.Property(e => e.RequestHash).HasMaxLength(64).IsRequired().IsUnicode(false);
                entity.Property(e => e.EntityType).HasMaxLength(50);
                entity.Property(e => e.ResultStatus).HasMaxLength(20).IsRequired();
            });

            base.OnModelCreating(builder);
        }

        public static void InitializeServerState(AzureSyncContext context, string databaseId)
        {
            if (databaseId != "2026" && databaseId != "2027")
                throw new System.ArgumentException($"Invalid canonical databaseId '{databaseId}'. Expected '2026' or '2027'.", nameof(databaseId));

            var existing = context.ServerStates.Find(databaseId);
            if (existing == null)
            {
                context.ServerStates.Add(new ServerState
                {
                    DatabaseId = databaseId,
                    CurrentVersion = 0,
                    LastUpdatedUtc = System.DateTime.UtcNow
                });
                context.SaveChanges();
            }
        }
    }
}
