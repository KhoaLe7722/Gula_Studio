using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharacterConceptAI.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptAndQueueEnhancements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GenerationJobs_RequestedByUserId_IdempotencyKey",
                table: "GenerationJobs");

            migrationBuilder.AddColumn<double>(
                name: "Denoise",
                table: "GenerationJobs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptTemplateVersion",
                table: "GenerationJobs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceConceptVersionId",
                table: "GenerationJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowKey",
                table: "GenerationJobs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowVersion",
                table: "GenerationJobs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_RequestedByUserId_IdempotencyKey",
                table: "GenerationJobs",
                columns: new[] { "RequestedByUserId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GenerationJobs_RequestedByUserId_IdempotencyKey",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "Denoise",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "PromptTemplateVersion",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "ReferenceConceptVersionId",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "WorkflowKey",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "WorkflowVersion",
                table: "GenerationJobs");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_RequestedByUserId_IdempotencyKey",
                table: "GenerationJobs",
                columns: new[] { "RequestedByUserId", "IdempotencyKey" },
                filter: "[IdempotencyKey] IS NOT NULL");
        }
    }
}
