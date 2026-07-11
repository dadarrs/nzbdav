using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddRepairEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepairEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    DavItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    ArrKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ArrHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ArrItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ArrTitleSlug = table.Column<string>(type: "TEXT", nullable: true),
                    ArrTitle = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepairEvents_DavItemId",
                table: "RepairEvents",
                column: "DavItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepairEvents");
        }
    }
}
