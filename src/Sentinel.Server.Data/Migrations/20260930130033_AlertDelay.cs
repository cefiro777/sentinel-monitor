using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertDelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "alert_delay_minutes",
                table: "checks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "problem_since",
                table: "check_states",
                type: "timestamp with time zone",
                nullable: true);

            // Кратковременные пики CPU/памяти — норма: у существующих «Систем» оповещение после 30 мин непрерывного превышения.
            migrationBuilder.Sql("UPDATE checks SET alert_delay_minutes = 30 WHERE module_id = 'system'");
            migrationBuilder.Sql("UPDATE check_states SET problem_since = last_change_at WHERE status <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "alert_delay_minutes",
                table: "checks");

            migrationBuilder.DropColumn(
                name: "problem_since",
                table: "check_states");
        }
    }
}
