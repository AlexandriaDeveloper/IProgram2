using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form");

            migrationBuilder.AlterColumn<int>(
                name: "DailyId",
                table: "Form",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form",
                column: "DailyId",
                principalTable: "Daily",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form");

            migrationBuilder.AlterColumn<int>(
                name: "DailyId",
                table: "Form",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Form_Daily_DailyId",
                table: "Form",
                column: "DailyId",
                principalTable: "Daily",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
