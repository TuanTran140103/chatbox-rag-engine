using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarkdownGenQAs.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadIdToConversationThread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThreadId",
                table: "Threads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Threads_ThreadId",
                table: "Threads",
                column: "ThreadId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Threads_ThreadId",
                table: "Threads");

            migrationBuilder.DropColumn(
                name: "ThreadId",
                table: "Threads");
        }
    }
}
