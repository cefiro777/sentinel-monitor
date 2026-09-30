using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CheckIgnoredKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "ignored_keys",
                table: "checks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");   // у существующих проверок — пустой список
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ignored_keys",
                table: "checks");
        }
    }
}
