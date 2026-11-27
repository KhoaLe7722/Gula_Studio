using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharacterConceptAI.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterConceptsAndApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterConcepts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RepresentativeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterConcepts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterConcepts_CharacterVariants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "CharacterVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CharacterConcepts_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CharacterConcepts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConceptVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConceptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ParentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImageAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FinalPromptSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConceptVersions_CharacterConcepts_ConceptId",
                        column: x => x.ConceptId,
                        principalTable: "CharacterConcepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConceptVersions_ConceptVersions_ParentVersionId",
                        column: x => x.ParentVersionId,
                        principalTable: "ConceptVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConceptVersions_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConceptVersions_ImageAssets_ImageAssetId",
                        column: x => x.ImageAssetId,
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConceptApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConceptVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConceptApprovals_AspNetUsers_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConceptApprovals_ConceptVersions_ConceptVersionId",
                        column: x => x.ConceptVersionId,
                        principalTable: "ConceptVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterConcepts_CharacterId",
                table: "CharacterConcepts",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterConcepts_ProjectId_CharacterId",
                table: "CharacterConcepts",
                columns: new[] { "ProjectId", "CharacterId" });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterConcepts_RepresentativeVersionId",
                table: "CharacterConcepts",
                column: "RepresentativeVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterConcepts_VariantId",
                table: "CharacterConcepts",
                column: "VariantId");

            migrationBuilder.CreateIndex(
                name: "IX_ConceptApprovals_ConceptVersionId_ReviewedAt",
                table: "ConceptApprovals",
                columns: new[] { "ConceptVersionId", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConceptApprovals_ReviewerUserId",
                table: "ConceptApprovals",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConceptVersions_ConceptId_VersionNumber",
                table: "ConceptVersions",
                columns: new[] { "ConceptId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConceptVersions_GenerationJobId",
                table: "ConceptVersions",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ConceptVersions_ImageAssetId",
                table: "ConceptVersions",
                column: "ImageAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ConceptVersions_ParentVersionId",
                table: "ConceptVersions",
                column: "ParentVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_CharacterConcepts_ConceptVersions_RepresentativeVersionId",
                table: "CharacterConcepts",
                column: "RepresentativeVersionId",
                principalTable: "ConceptVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CharacterConcepts_ConceptVersions_RepresentativeVersionId",
                table: "CharacterConcepts");

            migrationBuilder.DropTable(
                name: "ConceptApprovals");

            migrationBuilder.DropTable(
                name: "ConceptVersions");

            migrationBuilder.DropTable(
                name: "CharacterConcepts");
        }
    }
}
