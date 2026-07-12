using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DocsViewer.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalizedNavTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalizedNavTitles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceText = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Culture = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LocalizedText = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LastLocalizedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalizedNavTitles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalizedNavTitles_SourceText_Culture",
                table: "LocalizedNavTitles",
                columns: new[] { "SourceText", "Culture" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalizedNavTitles");
        }
    }
}
