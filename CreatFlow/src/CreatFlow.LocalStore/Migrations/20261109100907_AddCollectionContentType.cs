using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatFlow.LocalStore.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionContentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "content_type",
                table: "x_collections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "content_type",
                table: "x_async_task_item",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_type",
                table: "x_collections");

            migrationBuilder.DropColumn(
                name: "content_type",
                table: "x_async_task_item");
        }
    }
}
