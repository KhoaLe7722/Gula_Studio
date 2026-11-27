using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Tests;

public class VerticalSliceIntegrationTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public VerticalSliceIntegrationTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthy()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VerticalSlice_Login_CreateProject_CreateCharacter_GenerateJob_StreamImage()
    {
        var client = _factory.CreateClient();

        // 1. Login
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "admin@conceptai.local",
            Password: "Admin@Secured2026"
        ));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(loginResult);
        var accessToken = loginResult["accessToken"].ToString();
        Assert.False(string.IsNullOrEmpty(accessToken));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        // 2. Create Project
        var createProjectResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "Test Fantasy Chronicles",
            ProjectType: "Game",
            Description: "A dark fantasy RPG project"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjectResp.StatusCode);
        var project = await createProjectResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);

        // 3. Create Character
        var createCharResp = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest(
            Name: "Luna Silverblade",
            Role: "Protagonist",
            Description: "Knight of the Moon Order",
            InitialAttributes: null
        ));
        Assert.Equal(HttpStatusCode.Created, createCharResp.StatusCode);
        var character = await createCharResp.Content.ReadFromJsonAsync<CharacterDto>();
        Assert.NotNull(character);
        Assert.NotEmpty(character.Variants);

        // 4. Submit Generation Job (Fake AI)
        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: character.Variants[0].Id,
            PromptMode: PromptMode.Hybrid,
            RawPrompt: "Standing on top of castle tower at dusk",
            FinalPrompt: "Full body anime concept art of Luna Silverblade, standing on top of castle tower at dusk",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 424242,
            IdempotencyKey: Guid.NewGuid().ToString()
        ));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);

        var jobLocation = genResp.Headers.Location;
        Assert.NotNull(jobLocation);

        // 5. Poll Job until Completed by BackgroundWorker
        GenerationJobDto? completedJob = null;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var pollResp = await client.GetAsync(jobLocation);
            if (pollResp.IsSuccessStatusCode)
            {
                var job = await pollResp.Content.ReadFromJsonAsync<GenerationJobDto>();
                if (job?.Status == GenerationStatus.Completed)
                {
                    completedJob = job;
                    break;
                }
            }
        }

        Assert.NotNull(completedJob);
        Assert.Equal(GenerationStatus.Completed, completedJob.Status);
        Assert.True(completedJob.IsSimulated);
        Assert.NotEmpty(completedJob.OutputAssetIds);

        // 6. Download / Stream generated image
        var assetId = completedJob.OutputAssetIds[0];
        var mediaResp = await client.GetAsync($"/api/v1/projects/{project.Id}/media/{assetId}");
        Assert.Equal(HttpStatusCode.OK, mediaResp.StatusCode);
        Assert.Equal("image/png", mediaResp.Content.Headers.ContentType?.MediaType);

        var imageBytes = await mediaResp.Content.ReadAsByteArrayAsync();
        Assert.True(imageBytes.Length > 0);
        // Verify PNG magic bytes
        Assert.Equal(0x89, imageBytes[0]);
        Assert.Equal(0x50, imageBytes[1]);
        Assert.Equal(0x4E, imageBytes[2]);
        Assert.Equal(0x47, imageBytes[3]);
    }
}
