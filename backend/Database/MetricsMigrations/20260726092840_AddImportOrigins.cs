using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddImportOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportOrigins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UrlHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ArrIndexer = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Resolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportOrigins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportOrigins_Resolved_CreatedAt",
                table: "ImportOrigins",
                columns: new[] { "Resolved", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportOrigins");
        }
    }
}
