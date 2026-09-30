using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncidentInProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "in_progress_by_user_id",
                table: "incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "snoozed_until",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "in_progress_by_user_id",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "snoozed_until",
                table: "incidents");
        }
    }
}
