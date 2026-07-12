using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DocsViewer.MySql.Migrations
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
                type: "varchar(10)",
                maxLength: 10,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
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
