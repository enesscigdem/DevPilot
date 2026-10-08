using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConflictMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConflictMode",
                table: "AutomationPolicies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Balanced");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConflictMode",
                table: "AutomationPolicies");
        }
    }
}
