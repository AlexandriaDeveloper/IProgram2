using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations.LocalSync
{
    /// <inheritdoc />
    public partial class AlignBootstrapManifestStatusLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail-closed precondition: abort if any existing Status value exceeds 20 characters
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sync.BootstrapManifest WHERE LEN(Status) > 20)
BEGIN
    RAISERROR('Precondition failed: sync.BootstrapManifest contains Status values exceeding 20 characters.', 16, 1);
    RETURN;
END
");

            // Safe column narrowing from nvarchar(50) to nvarchar(20) NOT NULL
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "sync",
                table: "BootstrapManifest",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "sync",
                table: "BootstrapManifest",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);
        }
    }
}
