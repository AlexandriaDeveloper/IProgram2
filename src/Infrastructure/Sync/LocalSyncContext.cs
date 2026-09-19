using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Sync
{
    public class LocalSyncContext : DbContext
    {
        public const string MigrationsHistoryTableName = "__EFMigrationsHistory_LocalSync";
        public const string MigrationsHistoryTableSchema = "sync";

        public DbSet<LocalOutbox> LocalOutboxes { get; set; }
        public DbSet<LocalState> LocalStates { get; set; }
        public DbSet<LocalBootstrapManifest> BootstrapManifests { get; set; }

        public LocalSyncContext(DbContextOptions<LocalSyncContext> options) : base(options)
        {
            ValidateLocalDatabaseConnection();
        }

        public void ValidateLocalDatabaseConnection()
        {
            if (Database.IsRelational())
            {
                var connection = Database.GetDbConnection();
                var physicalDbName = connection?.Database;
                var dataSource = connection?.DataSource;

                DatabaseBindingValidator.ValidateLocalBinding(dataSource, physicalDbName);
            }
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.HasDefaultSchema("sync");

            builder.Entity<LocalOutbox>(entity =>
            {
                entity.ToTable("LocalOutbox");
                entity.HasKey(e => e.ClientOperationId);
                entity.Property(e => e.DatabaseId).HasMaxLength(32).IsRequired();
                entity.Property(e => e.AggregateType).HasMaxLength(50).IsRequired();
                entity.Property(e => e.CommandName).HasMaxLength(100).IsRequired();
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.HasIndex(e => new { e.DatabaseId, e.Status, e.CreatedAtUtc })
                    .HasDatabaseName("IX_LocalOutbox_Queue");
            });

            builder.Entity<LocalState>(entity =>
            {
                entity.ToTable("LocalState");
                entity.HasKey(e => e.DatabaseId);
                entity.Property(e => e.DatabaseId).HasMaxLength(32);
                entity.Property(e => e.DeviceName).HasMaxLength(100);
            });

            builder.Entity<LocalBootstrapManifest>(entity =>
            {
                entity.ToTable("BootstrapManifest");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DatabaseId).HasMaxLength(32).IsRequired();
                entity.Property(e => e.AzureServerSource).HasMaxLength(255);
                entity.Property(e => e.TargetLocalEngine).HasMaxLength(100);
                entity.Property(e => e.MigrationHistoryHash).HasMaxLength(64);
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            });

            base.OnModelCreating(builder);
        }
    }
}
