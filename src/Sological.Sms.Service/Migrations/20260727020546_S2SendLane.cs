using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sological.Sms.Service.Migrations
{
    /// <inheritdoc />
    public partial class S2SendLane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "duplicate_window_seconds",
                table: "channels",
                type: "integer",
                nullable: false,
                defaultValue: 3600);

            migrationBuilder.CreateTable(
                name: "allowed_originators",
                columns: table => new
                {
                    originator = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allowed_originators", x => x.originator);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allowed_originators");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "duplicate_window_seconds",
                table: "channels");
        }
    }
}
