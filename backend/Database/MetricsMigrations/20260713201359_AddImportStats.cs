using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddImportStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadMs = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProviderBytesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportStats_CompletedAt",
                table: "ImportStats",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportStats");
        }
    }
}
