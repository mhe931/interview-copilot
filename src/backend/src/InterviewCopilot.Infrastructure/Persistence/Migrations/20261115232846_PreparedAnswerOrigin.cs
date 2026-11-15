using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewCopilot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreparedAnswerOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "SuggestedAnswers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocument",
                table: "SuggestedAnswers",
                type: "TEXT",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "SuggestedAnswers");

            migrationBuilder.DropColumn(
                name: "SourceDocument",
                table: "SuggestedAnswers");
        }
    }
}
