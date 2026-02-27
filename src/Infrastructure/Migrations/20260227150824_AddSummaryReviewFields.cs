using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaryReviewFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSummaryReviewed",
                table: "FormDetails",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IsSummaryReviewedBy",
                table: "FormDetails",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SummaryReviewedAt",
                table: "FormDetails",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSummaryReviewed",
                table: "FormDetails");

            migrationBuilder.DropColumn(
                name: "IsSummaryReviewedBy",
                table: "FormDetails");

            migrationBuilder.DropColumn(
                name: "SummaryReviewedAt",
                table: "FormDetails");
        }
    }
}
