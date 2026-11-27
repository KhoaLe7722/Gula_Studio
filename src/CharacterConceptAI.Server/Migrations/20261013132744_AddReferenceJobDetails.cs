using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharacterConceptAI.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceJobDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputAssetSha256",
                table: "GenerationJobs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_ReferenceConceptVersionId",
                table: "GenerationJobs",
                column: "ReferenceConceptVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_GenerationJobs_ConceptVersions_ReferenceConceptVersionId",
                table: "GenerationJobs",
                column: "ReferenceConceptVersionId",
                principalTable: "ConceptVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GenerationJobs_ConceptVersions_ReferenceConceptVersionId",
                table: "GenerationJobs");

            migrationBuilder.DropIndex(
                name: "IX_GenerationJobs_ReferenceConceptVersionId",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "InputAssetSha256",
                table: "GenerationJobs");
        }
    }
}
