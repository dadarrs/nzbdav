using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddHealthCheckBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "HealthBytesBackground",
                table: "ProviderMinutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "HealthBytesOnAdd",
                table: "ProviderMinutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "HealthBytesBackground",
                table: "ProviderHourly",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "HealthBytesOnAdd",
                table: "ProviderHourly",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HealthBytesBackground",
                table: "ProviderMinutes");

            migrationBuilder.DropColumn(
                name: "HealthBytesOnAdd",
                table: "ProviderMinutes");

            migrationBuilder.DropColumn(
                name: "HealthBytesBackground",
                table: "ProviderHourly");

            migrationBuilder.DropColumn(
                name: "HealthBytesOnAdd",
                table: "ProviderHourly");
        }
    }
}
