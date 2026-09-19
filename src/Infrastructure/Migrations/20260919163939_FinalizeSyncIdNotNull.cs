using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FinalizeSyncIdNotNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FormRefernce_SyncId",
                table: "FormRefernce");

            migrationBuilder.DropIndex(
                name: "IX_FormDetails_SyncId",
                table: "FormDetails");

            migrationBuilder.DropIndex(
                name: "IX_Form_SyncId",
                table: "Form");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeWatchLists_SyncId",
                table: "EmployeeWatchLists");

            migrationBuilder.DropIndex(
                name: "IX_Employees_SyncId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeRefernce_SyncId",
                table: "EmployeeRefernce");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeNetPays_SyncId",
                table: "EmployeeNetPays");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeBank_SyncId",
                table: "EmployeeBank");

            migrationBuilder.DropIndex(
                name: "IX_Departments_SyncId",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_DailyReference_SyncId",
                table: "DailyReference");

            migrationBuilder.DropIndex(
                name: "IX_Daily_SyncId",
                table: "Daily");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "FormRefernce",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "FormDetails",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Form",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeWatchLists",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Employees",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeRefernce",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeNetPays",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeBank",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Departments",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "DailyReference",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Daily",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormRefernce_SyncId",
                table: "FormRefernce",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormDetails_SyncId",
                table: "FormDetails",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Form_SyncId",
                table: "Form",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeWatchLists_SyncId",
                table: "EmployeeWatchLists",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_SyncId",
                table: "Employees",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeRefernce_SyncId",
                table: "EmployeeRefernce",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeNetPays_SyncId",
                table: "EmployeeNetPays",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeBank_SyncId",
                table: "EmployeeBank",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Departments_SyncId",
                table: "Departments",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyReference_SyncId",
                table: "DailyReference",
                column: "SyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Daily_SyncId",
                table: "Daily",
                column: "SyncId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FormRefernce_SyncId",
                table: "FormRefernce");

            migrationBuilder.DropIndex(
                name: "IX_FormDetails_SyncId",
                table: "FormDetails");

            migrationBuilder.DropIndex(
                name: "IX_Form_SyncId",
                table: "Form");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeWatchLists_SyncId",
                table: "EmployeeWatchLists");

            migrationBuilder.DropIndex(
                name: "IX_Employees_SyncId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeRefernce_SyncId",
                table: "EmployeeRefernce");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeNetPays_SyncId",
                table: "EmployeeNetPays");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeBank_SyncId",
                table: "EmployeeBank");

            migrationBuilder.DropIndex(
                name: "IX_Departments_SyncId",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_DailyReference_SyncId",
                table: "DailyReference");

            migrationBuilder.DropIndex(
                name: "IX_Daily_SyncId",
                table: "Daily");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "FormRefernce",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "FormDetails",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Form",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeWatchLists",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Employees",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeRefernce",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeNetPays",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "EmployeeBank",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Departments",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "DailyReference",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "SyncId",
                table: "Daily",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_FormRefernce_SyncId",
                table: "FormRefernce",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FormDetails_SyncId",
                table: "FormDetails",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Form_SyncId",
                table: "Form",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeWatchLists_SyncId",
                table: "EmployeeWatchLists",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_SyncId",
                table: "Employees",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeRefernce_SyncId",
                table: "EmployeeRefernce",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeNetPays_SyncId",
                table: "EmployeeNetPays",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeBank_SyncId",
                table: "EmployeeBank",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_SyncId",
                table: "Departments",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DailyReference_SyncId",
                table: "DailyReference",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Daily_SyncId",
                table: "Daily",
                column: "SyncId",
                unique: true,
                filter: "[SyncId] IS NOT NULL");
        }
    }
}
