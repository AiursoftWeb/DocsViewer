using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DocsViewer.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceCulture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceCulture",
                table: "Documents",
                type: "TEXT",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceCulture",
                table: "Documents");
        }
    }
}
