using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DocsViewer.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MySQL requires every foreign key to remain backed by an index. The
            // replacement indexes are created later because they include columns
            // added by this migration, so keep temporary supporting indexes while
            // the original indexes are replaced.
            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_DocumentId_MigrationSupport",
                table: "DocumentComments",
                columns: new[] { "DocumentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_UserId_MigrationSupport",
                table: "DocumentComments",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_DocumentId",
                table: "DocumentComments");

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_UserId",
                table: "DocumentComments");

            migrationBuilder.AddColumn<DateTime>(
                name: "ModeratedAtUtc",
                table: "DocumentComments",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModeratedByUserId",
                table: "DocumentComments",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "DocumentComments",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_DocumentId_Status_CreatedAt",
                table: "DocumentComments",
                columns: new[] { "DocumentId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_Status_CreatedAt",
                table: "DocumentComments",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_UserId_CreatedAt",
                table: "DocumentComments",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_DocumentId_MigrationSupport",
                table: "DocumentComments");

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_UserId_MigrationSupport",
                table: "DocumentComments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve indexes for the two foreign keys while replacing the
            // composite indexes with their original single-column indexes.
            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_DocumentId_MigrationSupport",
                table: "DocumentComments",
                columns: new[] { "DocumentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_UserId_MigrationSupport",
                table: "DocumentComments",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_DocumentId_Status_CreatedAt",
                table: "DocumentComments");

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_Status_CreatedAt",
                table: "DocumentComments");

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_UserId_CreatedAt",
                table: "DocumentComments");

            migrationBuilder.DropColumn(
                name: "ModeratedAtUtc",
                table: "DocumentComments");

            migrationBuilder.DropColumn(
                name: "ModeratedByUserId",
                table: "DocumentComments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "DocumentComments");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_DocumentId",
                table: "DocumentComments",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentComments_UserId",
                table: "DocumentComments",
                column: "UserId");

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_DocumentId_MigrationSupport",
                table: "DocumentComments");

            migrationBuilder.DropIndex(
                name: "IX_DocumentComments_UserId_MigrationSupport",
                table: "DocumentComments");
        }
    }
}
