using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeHotPathIndexesSprint4B : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FormDetails_FormId",
                table: "FormDetails");

            migrationBuilder.DropIndex(
                name: "IX_Form_DailyId",
                table: "Form");

            migrationBuilder.CreateIndex(
                name: "IX_FormDetails_FormId_IsActive_EmployeeId",
                table: "FormDetails",
                columns: new[] { "FormId", "IsActive", "EmployeeId" })
                .Annotation("SqlServer:Include", new[] { "Amount", "OrderNum" });

            migrationBuilder.CreateIndex(
                name: "IX_Form_DailyId_IsActive_Index",
                table: "Form",
                columns: new[] { "DailyId", "IsActive", "Index" });

            migrationBuilder.CreateIndex(
                name: "IX_Form_IsActive_CreatedAt",
                table: "Form",
                columns: new[] { "IsActive", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FormDetails_FormId_IsActive_EmployeeId",
                table: "FormDetails");

            migrationBuilder.DropIndex(
                name: "IX_Form_DailyId_IsActive_Index",
                table: "Form");

            migrationBuilder.DropIndex(
                name: "IX_Form_IsActive_CreatedAt",
                table: "Form");

            migrationBuilder.CreateIndex(
                name: "IX_FormDetails_FormId",
                table: "FormDetails",
                column: "FormId");

            migrationBuilder.CreateIndex(
                name: "IX_Form_DailyId",
                table: "Form",
                column: "DailyId");
        }
    }
}
