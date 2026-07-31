using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sological.Sms.Service.Migrations
{
    /// <inheritdoc />
    public partial class SecurityContainment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "allowed_recipients",
                table: "channels",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "daily_part_limit",
                table: "channels",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "upstream_breakers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    upstream = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tripped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    unmatched_count = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    alert_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    alert_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    re_armed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upstream_breakers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_events_unmatched",
                table: "delivery_events",
                column: "received_at",
                filter: "message_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_upstream_breakers_active",
                table: "upstream_breakers",
                column: "upstream",
                unique: true,
                filter: "re_armed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "upstream_breakers");

            migrationBuilder.DropIndex(
                name: "ix_delivery_events_unmatched",
                table: "delivery_events");

            migrationBuilder.DropColumn(
                name: "allowed_recipients",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "daily_part_limit",
                table: "channels");
        }
    }
}
