using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharacterConceptAI.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterAdvanced09C : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetCharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRelationships_Characters_SourceCharacterId",
                        column: x => x.SourceCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CharacterRelationships_Characters_TargetCharacterId",
                        column: x => x.TargetCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CharacterRelationships_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CharacterRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangeSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRevisions_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterTemplates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VariantAttributeValues",
                columns: table => new
                {
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariantAttributeValues", x => new { x.VariantId, x.DefinitionId });
                    table.ForeignKey(
                        name: "FK_VariantAttributeValues_CharacterAttributeDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "CharacterAttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VariantAttributeValues_CharacterVariants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "CharacterVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TemplateAttributeDefinitions",
                columns: table => new
                {
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefaultValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateAttributeDefinitions", x => new { x.TemplateId, x.DefinitionId });
                    table.ForeignKey(
                        name: "FK_TemplateAttributeDefinitions_CharacterAttributeDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "CharacterAttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateAttributeDefinitions_CharacterTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "CharacterTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationships_ProjectId_SourceCharacterId_TargetCharacterId_Type",
                table: "CharacterRelationships",
                columns: new[] { "ProjectId", "SourceCharacterId", "TargetCharacterId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationships_SourceCharacterId",
                table: "CharacterRelationships",
                column: "SourceCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRelationships_TargetCharacterId",
                table: "CharacterRelationships",
                column: "TargetCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRevisions_CharacterId_RevisionNumber",
                table: "CharacterRevisions",
                columns: new[] { "CharacterId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterTemplates_ProjectId",
                table: "CharacterTemplates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateAttributeDefinitions_DefinitionId",
                table: "TemplateAttributeDefinitions",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_VariantAttributeValues_DefinitionId",
                table: "VariantAttributeValues",
                column: "DefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterRelationships");

            migrationBuilder.DropTable(
                name: "CharacterRevisions");

            migrationBuilder.DropTable(
                name: "TemplateAttributeDefinitions");

            migrationBuilder.DropTable(
                name: "VariantAttributeValues");

            migrationBuilder.DropTable(
                name: "CharacterTemplates");
        }
    }
}
