using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddSegmentFetchCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SegmentFetches_At",
                table: "SegmentFetches");

            migrationBuilder.CreateIndex(
                name: "IX_SegmentFetches_At_Status_DurationMs",
                table: "SegmentFetches",
                columns: new[] { "At", "Status", "DurationMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SegmentFetches_At_Status_DurationMs",
                table: "SegmentFetches");

            migrationBuilder.CreateIndex(
                name: "IX_SegmentFetches_At",
                table: "SegmentFetches",
                column: "At");
        }
    }
}
