using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CharacterConceptAI.Server.AI;

namespace CharacterConceptAI.Tests;

public class ComfyUiClientTests
{
    [Fact]
    public void UNIT_ComfyUiClient_IsSimulated_ReturnsFalse()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:8188"
        }).Build();

        using var http = new HttpClient();
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        Assert.False(client.IsSimulated);
    }

    [Fact]
    public async Task UNIT_ComfyUiClient_UnreachableServer_ReturnsHandledFailure_NoCrash()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:59999" // Unreachable port
        }).Build();

        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:59999") };
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        var result = await client.GenerateAsync(new GenerationExecutionRequest(
            JobId: Guid.NewGuid(),
            Prompt: "Test unreachable",
            Width: 512,
            Height: 512,
            Seed: 12345
        ));

        Assert.False(result.Success);
        Assert.Empty(result.Outputs);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Không thể kết nối", result.ErrorMessage);
    }

    [Fact]
    public async Task UNIT_ComfyUiClient_ExecutionError_ReportsErrorSafely()
    {
        var handler = new MockHttpMessageHandler((req) =>
        {
            if (req.RequestUri?.AbsolutePath == "/prompt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        prompt_id = "test-error-prompt-id"
                    }))
                };
            }
            if (req.RequestUri?.AbsolutePath.StartsWith("/history/test-error-prompt-id") == true)
            {
                var historyData = new Dictionary<string, object>
                {
                    ["test-error-prompt-id"] = new
                    {
                        status = new
                        {
                            status_str = "error",
                            completed = false,
                            messages = new object[]
                            {
                                new object[] { "execution_error", new { exception_type = "OutOfMemoryError", node_id = "3" } }
                            }
                        },
                        outputs = new Dictionary<string, object>()
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(historyData))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:8188",
            ["ComfyUI:TimeoutSeconds"] = "5"
        }).Build();

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8188") };
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        var result = await client.GenerateAsync(new GenerationExecutionRequest(
            JobId: Guid.NewGuid(),
            Prompt: "Test error reporting",
            Width: 512,
            Height: 512,
            Seed: 999
        ));

        Assert.False(result.Success);
        Assert.Empty(result.Outputs);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("execution_error", result.ErrorMessage);
        Assert.Equal("test-error-prompt-id", result.ProviderPromptId);
    }

    [Fact]
    public async Task UNIT_ComfyUiClient_SuccessfulGeneration_InjectsParameters_DownloadsAndParsesPng()
    {
        string? capturedPromptJson = null;

        // Create a minimal 24-byte PNG byte header for 512x768
        var mockPng = new byte[32];
        mockPng[0] = 0x89; mockPng[1] = 0x50; mockPng[2] = 0x4E; mockPng[3] = 0x47; // PNG
        mockPng[4] = 0x0D; mockPng[5] = 0x0A; mockPng[6] = 0x1A; mockPng[7] = 0x0A;
        mockPng[16] = 0x00; mockPng[17] = 0x00; mockPng[18] = 0x02; mockPng[19] = 0x00; // w = 512
        mockPng[20] = 0x00; mockPng[21] = 0x00; mockPng[22] = 0x03; mockPng[23] = 0x00; // h = 768

        var handler = new MockHttpMessageHandler((req) =>
        {
            if (req.RequestUri?.AbsolutePath == "/prompt")
            {
                capturedPromptJson = req.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        prompt_id = "test-success-prompt-id"
                    }))
                };
            }
            if (req.RequestUri?.AbsolutePath.StartsWith("/history/test-success-prompt-id") == true)
            {
                var historyData = new Dictionary<string, object>
                {
                    ["test-success-prompt-id"] = new
                    {
                        status = new { status_str = "success", completed = true },
                        outputs = new Dictionary<string, object>
                        {
                            ["9"] = new
                            {
                                images = new[]
                                {
                                    new { filename = "output_001.png", subfolder = "", type = "output" }
                                }
                            }
                        }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(historyData))
                };
            }
            if (req.RequestUri?.AbsolutePath == "/view")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(mockPng)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:8188",
            ["ComfyUI:TimeoutSeconds"] = "10"
        }).Build();

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8188") };
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        var result = await client.GenerateAsync(new GenerationExecutionRequest(
            JobId: Guid.NewGuid(),
            Prompt: "Anime heroine in silver armor",
            Width: 512,
            Height: 768,
            Seed: 888999
        ));

        Assert.True(result.Success);
        Assert.Single(result.Outputs);
        Assert.Equal("test-success-prompt-id", result.ProviderPromptId);
        Assert.Equal(512, result.Outputs[0].Width);
        Assert.Equal(768, result.Outputs[0].Height);
        Assert.Equal("png", result.Outputs[0].Extension);

        // Verify injected parameters into graph nodes
        Assert.NotNull(capturedPromptJson);
        var parsedGraph = JsonNode.Parse(capturedPromptJson)?["prompt"];
        Assert.NotNull(parsedGraph);
        Assert.Equal("Anime heroine in silver armor", parsedGraph["27"]?["inputs"]?["text"]?.ToString());
        Assert.Equal(512, parsedGraph["13"]?["inputs"]?["width"]?.GetValue<int>());
        Assert.Equal(768, parsedGraph["13"]?["inputs"]?["height"]?.GetValue<int>());
        Assert.Equal(888999, parsedGraph["3"]?["inputs"]?["seed"]?.GetValue<long>());
        Assert.Equal(ComfyUiClient.ComputeDynamicShift(512, 768), parsedGraph["11"]?["inputs"]?["shift"]?.GetValue<double>());
    }

    [Fact]
    public void UNIT_ComfyUiClient_DynamicShift_ScalesMonotonicallyWithResolution()
    {
        var s512 = ComfyUiClient.ComputeDynamicShift(512, 512);
        var s512x768 = ComfyUiClient.ComputeDynamicShift(512, 768);
        var s1024 = ComfyUiClient.ComputeDynamicShift(1024, 1024);
        var s1024x1536 = ComfyUiClient.ComputeDynamicShift(1024, 1536);

        Assert.InRange(s512, 1.8, 2.5);
        Assert.True(s512 < s512x768 && s512x768 < s1024 && s1024 < s1024x1536);
        Assert.InRange(s1024x1536, 3.2, 3.6);
    }

    [Fact]
    public async Task UNIT_ComfyUiClient_UserCancellation_DoesNotInterruptUnrelatedWorkflows()
    {
        bool interruptCalled = false;
        var handler = new MockHttpMessageHandler((req) =>
        {
            if (req.RequestUri?.AbsolutePath == "/prompt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        prompt_id = "test-cancel-prompt-id"
                    }))
                };
            }
            if (req.RequestUri?.AbsolutePath == "/interrupt")
            {
                interruptCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:8188",
            ["ComfyUI:TimeoutSeconds"] = "30"
        }).Build();

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8188") };
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(500); // Cancel while polling

        var result = await client.GenerateAsync(new GenerationExecutionRequest(
            JobId: Guid.NewGuid(),
            Prompt: "Prompt to cancel",
            Width: 512,
            Height: 512,
            Seed: 123
        ), cts.Token);

        Assert.False(result.Success);
        Assert.Empty(result.Outputs);
        Assert.Contains("hủy", result.ErrorMessage);
        Assert.False(interruptCalled);
    }

    private class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
