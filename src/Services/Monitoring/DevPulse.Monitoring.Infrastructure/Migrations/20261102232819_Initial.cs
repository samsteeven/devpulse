using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPulse.Monitoring.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    response_time_ms = table.Column<int>(type: "integer", nullable: false),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    failure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_checks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "monitored_targets",
                columns: table => new
                {
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_response_time_ms = table.Column<int>(type: "integer", nullable: true),
                    last_http_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_failure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitored_targets", x => x.service_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_checks_checked_at",
                table: "health_checks",
                column: "checked_at");

            migrationBuilder.CreateIndex(
                name: "ix_health_checks_organization_id_service_id_checked_at",
                table: "health_checks",
                columns: new[] { "organization_id", "service_id", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_next_check_at",
                table: "monitored_targets",
                column: "next_check_at",
                filter: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_monitored_targets_organization_id",
                table: "monitored_targets",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_checks");

            migrationBuilder.DropTable(
                name: "monitored_targets");
        }
    }
}
