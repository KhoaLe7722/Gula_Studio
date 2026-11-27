using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Projects;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CharacterConceptAI.Tests;

public class PagedWorkspaceEndpointsTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;
    public PagedWorkspaceEndpointsTests(TestApplicationFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string UserId)> CreateActorAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"paging_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Paging", IsActive = true };
        Assert.True((await manager.CreateAsync(user, "Paging@Secured2026")).Succeeded);
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Paging@Secured2026"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<BearerTokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return (client, user.Id);
    }

    private static Project AddProject(AppDbContext db, string userId, string name)
    {
        var project = new Project { Name = name, CreatedByUserId = userId };
        db.Projects.Add(project);
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = userId, Role = "Owner" });
        return project;
    }

    [Fact]
    public async Task UpscaleRequiresProjectDesignerAndProjectOwnedAsset()
    {
        var (owner, ownerId) = await CreateActorAsync();
        var (viewer, viewerId) = await CreateActorAsync();
        Guid projectId, unrelatedProjectId, assetId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = AddProject(db, ownerId, "Upscale");
            projectId = project.Id;
            unrelatedProjectId = AddProject(db, ownerId, "Unrelated").Id;
            db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = viewerId, Role = "Viewer" });
            var asset = new ImageAsset { ProjectId = projectId, StorageKey = "fixture.png", Width = 512, Height = 768, SizeBytes = 3000 };
            db.ImageAssets.Add(asset);
            assetId = asset.Id;
            await db.SaveChangesAsync();
        }

        var route = $"/api/v1/projects/{projectId}/media/{assetId}/upscale";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/v1/projects/{unrelatedProjectId}/media/{assetId}/upscale", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync(route, null)).StatusCode);
    }

    [Fact]
    public async Task Projects_SortBeforePaging_AllowlistedAndStable()
    {
        var (client, userId) = await CreateActorAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AddProject(db, userId, "Charlie").UpdatedAt = DateTimeOffset.UtcNow.AddDays(-3);
            AddProject(db, userId, "Alpha").UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
            AddProject(db, userId, "Bravo").UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var first = await client.GetFromJsonAsync<PagedResult<ProjectDto>>("/api/v1/projects?sort=name&pageSize=1");
        var second = await client.GetFromJsonAsync<PagedResult<ProjectDto>>("/api/v1/projects?sort=name&pageSize=1&page=2");
        Assert.Equal("Alpha", Assert.Single(first!.Items).Name);
        Assert.Equal("Bravo", Assert.Single(second!.Items).Name);
        Assert.Equal(3, first.TotalCount);
        var updated = await client.GetFromJsonAsync<PagedResult<ProjectDto>>("/api/v1/projects?sort=updated&pageSize=1");
        Assert.Equal("Bravo", Assert.Single(updated!.Items).Name);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/projects?sort=arbitrarySql")).StatusCode);
        var health = await client.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>("/api/v1/health");
        Assert.Equal("uiux-v1", health!["desktopContractVersion"].GetString());
    }

    [Fact]
    public async Task Characters_PagedFiltersBoundsAndLegacyShape_WithIdorConcealment()
    {
        var (client, userId) = await CreateActorAsync();
        Guid projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = AddProject(db, userId, "Characters");
            projectId = project.Id;
            foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo" })
            {
                var character = new Character { Id = Guid.NewGuid(), ProjectId = projectId, Name = name,
                    Status = name is "Delta" or "Echo" ? "Archived" : "Active", Description = name == "Charlie" ? "harbor" : null };
                character.Variants.Add(new CharacterVariant { Id = Guid.NewGuid(), CharacterId = character.Id, Name = "Default" });
                db.Characters.Add(character);
            }
            await db.SaveChangesAsync();
        }
        var route = $"/api/v1/projects/{projectId}/characters";
        var first = await client.GetFromJsonAsync<PagedResult<CharacterDto>>(route + "/paged?status=Active&pageSize=2");
        Assert.Equal(3, first!.TotalCount);
        Assert.Equal(new[] { "Alpha", "Bravo" }, first.Items.Select(c => c.Name));
        Assert.All(first.Items, c => Assert.Single(c.Variants));
        var second = await client.GetFromJsonAsync<PagedResult<CharacterDto>>(route + "/paged?status=Active&pageSize=2&page=2");
        Assert.Equal("Charlie", Assert.Single(second!.Items).Name);
        var filtered = await client.GetFromJsonAsync<PagedResult<CharacterDto>>(route + "/paged?query=harbor&page=0&pageSize=1000");
        Assert.Single(filtered!.Items);
        Assert.Equal(1, filtered.Page);
        Assert.Equal(100, filtered.PageSize);
        Assert.Equal(5, (await client.GetFromJsonAsync<List<CharacterDto>>(route))!.Count);
        var (outsider, _) = await CreateActorAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(route + "/paged")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(route + "/paged")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "/paged?page=2147483647&pageSize=100")).StatusCode);
    }

    [Fact]
    public async Task ProjectConcepts_ReviewAggregateUsesSubmittedVersions_AcrossCharactersAndProtectsTenant()
    {
        var (client, userId) = await CreateActorAsync();
        Guid projectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            projectId = AddProject(db, userId, "Review").Id;
            foreach (var (name, status, submitted) in new[] {
                ("Alpha", ConceptStatus.UnderReview, false), ("Bravo", ConceptStatus.Approved, true),
                ("Charlie", ConceptStatus.Archived, true), ("Delta", ConceptStatus.Draft, false) })
            {
                var character = new Character { Id = Guid.NewGuid(), ProjectId = projectId, Name = name };
                db.Characters.Add(character);
                var asset = new ImageAsset { ProjectId = projectId, StorageKey = $"{Guid.NewGuid():N}.png" };
                db.ImageAssets.Add(asset);
                var concept = new CharacterConcept { ProjectId = projectId, CharacterId = character.Id, Title = name, Status = status, CreatedByUserId = userId };
                var version = new ConceptVersion { ConceptId = concept.Id, ImageAssetId = asset.Id, Status = submitted ? ConceptVersionStatus.Submitted : ConceptVersionStatus.Draft, CreatedByUserId = userId };
                concept.Versions.Add(version);
                db.CharacterConcepts.Add(concept);
                await db.SaveChangesAsync();
                concept.RepresentativeVersionId = version.Id;
            }
            await db.SaveChangesAsync();
        }
        var route = $"/api/v1/projects/{projectId}/concepts";
        var pending = await client.GetFromJsonAsync<PagedResult<CharacterConceptDto>>(route + "?status=PendingReview&pageSize=1");
        Assert.Equal(2, pending!.TotalCount);
        Assert.Single(pending.Items);
        var second = await client.GetFromJsonAsync<PagedResult<CharacterConceptDto>>(route + "?status=PendingReview&pageSize=1&page=2");
        Assert.NotEqual(pending.Items[0].CharacterId, Assert.Single(second!.Items).CharacterId);
        Assert.All(pending.Items.Concat(second.Items), c => { Assert.False(string.IsNullOrEmpty(c.CharacterName)); Assert.Equal(1, c.VersionCount); Assert.NotNull(c.RepresentativeStreamUrl); });
        var underReview = await client.GetFromJsonAsync<PagedResult<CharacterConceptDto>>(route + "?status=UnderReview");
        Assert.Equal("Alpha", Assert.Single(underReview!.Items).CharacterName);
        Assert.Equal(4, (await client.GetFromJsonAsync<PagedResult<CharacterConceptDto>>(route))!.TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?status=999")).StatusCode);
        var (outsider, _) = await CreateActorAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(route + "?status=PendingReview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(route)).StatusCode);
    }
}
