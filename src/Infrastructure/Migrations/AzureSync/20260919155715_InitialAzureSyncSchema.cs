using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations.AzureSync
{
    /// <inheritdoc />
    public partial class InitialAzureSyncSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sync");

            migrationBuilder.CreateTable(
                name: "ProcessedOperations",
                schema: "sync",
                columns: table => new
                {
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntitySyncId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResultStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedOperations", x => new { x.DatabaseId, x.ClientOperationId });
                });

            migrationBuilder.CreateTable(
                name: "ServerChangeFeed",
                schema: "sync",
                columns: table => new
                {
                    FeedId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntitySyncId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OriginDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerChangeFeed", x => x.FeedId);
                });

            migrationBuilder.CreateTable(
                name: "ServerState",
                schema: "sync",
                columns: table => new
                {
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CurrentVersion = table.Column<long>(type: "bigint", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerState", x => x.DatabaseId);
                });

            migrationBuilder.CreateTable(
                name: "Tombstones",
                schema: "sync",
                columns: table => new
                {
                    DatabaseId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntitySyncId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NaturalKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ServerVersion = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tombstones", x => new { x.DatabaseId, x.EntityType, x.EntitySyncId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerChangeFeed_Pull",
                schema: "sync",
                table: "ServerChangeFeed",
                columns: new[] { "DatabaseId", "ServerVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_Tombstones_Pull",
                schema: "sync",
                table: "Tombstones",
                columns: new[] { "DatabaseId", "ServerVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedOperations",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "ServerChangeFeed",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "ServerState",
                schema: "sync");

            migrationBuilder.DropTable(
                name: "Tombstones",
                schema: "sync");
        }
    }
}
