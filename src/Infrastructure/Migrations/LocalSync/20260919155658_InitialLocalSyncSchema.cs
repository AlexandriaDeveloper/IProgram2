using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations.LocalSync
{
    /// <inheritdoc />
    public partial class InitialLocalSyncSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sync");

            migrationBuilder.CreateTable(
                name: "BootstrapManifest",
                schema: "sync",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BootstrapTimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AzureServerSource = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    TargetLocalEngine = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MigrationHistoryHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TableCheckJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IdentityCheckJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsWriteAllowed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BootstrapManifest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalOutbox",
                schema: "sync",
                columns: table => new
                {
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CommandName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntitySyncId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LockToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalOutbox", x => x.ClientOperationId);
                });

            migrationBuilder.CreateTable(
                name: "LocalState",
                schema: "sync",
                columns: table => new
                {
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastSuccessfulPushUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessfulPullUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    ActiveLeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastSyncAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalState", x => x.DatabaseId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalOutbox_Queue",
                schema: "sync",
                table: "LocalOutbox",
                columns: new[] { "DatabaseId", "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BootstrapManifest",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "LocalOutbox",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "LocalState",
                schema: "sync");
        }
    }
}
