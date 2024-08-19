using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DailyRelationfix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form");

            migrationBuilder.AddForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form",
                column: "DailyId",
                principalTable: "Daily",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form");

            migrationBuilder.AddForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form",
                column: "DailyId",
                principalTable: "Daily",
                principalColumn: "Id");
        }
    }
}
