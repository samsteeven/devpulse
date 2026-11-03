using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPulse.Monitoring.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Agents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: false),
                    memory_percent = table.Column<double>(type: "double precision", nullable: false),
                    disk_percent = table.Column<double>(type: "double precision", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    operating_system = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    agent_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_percent = table.Column<double>(type: "double precision", nullable: true),
                    disk_percent = table.Column<double>(type: "double precision", nullable: true),
                    uptime_seconds = table.Column<long>(type: "bigint", nullable: true),
                    silent_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_samples_at",
                table: "agent_samples",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_agent_samples_organization_id_agent_id_at",
                table: "agent_samples",
                columns: new[] { "organization_id", "agent_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_agents_organization_id",
                table: "agents",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_samples");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
