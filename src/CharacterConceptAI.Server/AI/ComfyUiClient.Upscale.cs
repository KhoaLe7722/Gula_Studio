using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CharacterConceptAI.Server.AI;

public partial class ComfyUiClient
{
    internal const string FullQualityWorkflowVersion = "full-1.0";
    internal const string FullFaceWorkflowVersion = "full-2.0";

    private static readonly Regex FaceRefineAnimeRegex = new(
        @"\b(anime|manga|cel[ -]shaded|cartoon|toon)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FaceRefinePhotoRegex = new(
        @"\b(photorealistic|photograph|photography|realistic photo)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsFullQualityWorkflow(string? workflowVersion)
        => workflowVersion is FullQualityWorkflowVersion or FullFaceWorkflowVersion;

    private static async Task<string> LoadAuxiliaryWorkflowAsync(string fileName, CancellationToken ct)
    {
        if (WorkflowTemplateCache.TryGetValue(fileName, out var cached))
            return cached;

        var path = Path.Combine(AppContext.BaseDirectory, "AI", "WorkflowTemplates", fileName);
        if (!File.Exists(path))
            path = Path.Combine(Directory.GetCurrentDirectory(), "AI", "WorkflowTemplates", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Không tìm thấy workflow ComfyUI mẫu '{fileName}'.", path);

        var text = await File.ReadAllTextAsync(path, ct);
        WorkflowTemplateCache[fileName] = text;
        return text;
    }

    private async Task<string> UploadAuxiliarySourceAsync(byte[] image, string subfolder, CancellationToken ct)
    {
        var fileName = $"cca_aux_{ComputeShortHash(image)}.png";
        var cacheKey = $"{_http.BaseAddress?.Authority ?? "default"}|{subfolder}|{fileName}";
        if (Volatile.Read(ref _liveComfyServerFlag) == 1 && UploadedImageCache.TryGetValue(cacheKey, out var cachedPath))
            return cachedPath;

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(image);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "image", fileName);
        if (!string.IsNullOrWhiteSpace(subfolder))
            form.Add(new StringContent(subfolder), "subfolder");
        form.Add(new StringContent("true"), "overwrite");

        using var response = await _http.PostAsync("/upload/image", form, ct);
        response.EnsureSuccessStatusCode();
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var uploadedName = doc?["name"]?.GetValue<string>() ?? fileName;
        var uploadedSub = doc?["subfolder"]?.GetValue<string>() ?? string.Empty;
        var resolved = string.IsNullOrWhiteSpace(uploadedSub) ? uploadedName : $"{uploadedSub}/{uploadedName}";
        if (Volatile.Read(ref _liveComfyServerFlag) == 1)
        {
            if (UploadedImageCache.Count >= 256) UploadedImageCache.Clear();
            UploadedImageCache[cacheKey] = resolved;
        }
        return resolved;
    }

    private async Task<JsonObject> RunAuxiliaryWorkflowAsync(JsonObject graph, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var clientId = Guid.NewGuid().ToString("N");
        using var ws = await TryConnectWebSocketAsync(clientId, ct);

        var payload = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = clientId
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var submit = await _http.PostAsync("/prompt", content, ct);
        submit.EnsureSuccessStatusCode();

        var submitJson = JsonNode.Parse(await submit.Content.ReadAsStringAsync(ct));
        var promptId = submitJson?["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(promptId))
            throw new InvalidOperationException("ComfyUI không trả về prompt_id cho tác vụ phụ.");
        if (submitJson?["node_errors"] is JsonObject neObj && neObj.Count > 0)
            throw new InvalidOperationException($"ComfyUI lỗi cấu hình node cho tác vụ phụ: {neObj.ToJsonString()}");

        var item = await WaitForPromptHistoryAsync(promptId, ws, sw, ct);
        if (item is null)
            throw new OperationCanceledException("Tác vụ phụ trên ComfyUI đã hết thời gian chờ.", ct);

        if (item["status"]?["messages"] is JsonArray messages)
        {
            foreach (var msg in messages)
            {
                if (msg is JsonArray pair && pair.Count >= 2 &&
                    pair[0]?.GetValue<string>() is "execution_error" or "execution_interrupted")
                {
                    throw new InvalidOperationException($"ComfyUI lỗi khi chạy tác vụ phụ: {pair[1]?.ToJsonString()}");
                }
            }
        }
        if (string.Equals(item["status"]?["status_str"]?.GetValue<string>(), "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ComfyUI báo lỗi thực thi cho tác vụ phụ.");

        return item;
    }

    private static async Task AddFaceRefinementAsync(JsonObject graph, GenerationExecutionRequest request, CancellationToken ct)
    {
        var refinement = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("full-face-refinement.api.json", ct))!.AsObject();
        refinement["71"]!["inputs"]!["prompt"] = request.Prompt;
        refinement["76"]!["inputs"]!["seed"] = request.Seed;

        // Decouple Node 72 (CLIPTextEncode) from Node 71 (which depends on per-seed VAEDecode) using a static literal
        // so ComfyUI permanently caches the conditioning across jobs — avoiding a 4 GB Qwen3-4B CLIP reload after RealESRGAN!
        var explicitPhoto = FaceRefinePhotoRegex.IsMatch(request.Prompt);
        var isAnime = FaceRefineAnimeRegex.IsMatch(request.Prompt) && !explicitPhoto;
        var styleCue = isAnime
            ? "Anime illustration with consistent linework"
            : "Preserve the original rendering style and natural facial details";
        refinement["72"]!["inputs"]!["text"] =
            $"{styleCue}. Close-up of the same face. Keep the same expression, eye color, gaze, hair and lighting. Clearly defined eyes, eyelids, nose and lips.";

        foreach (var node in refinement)
            graph.Add(node.Key, node.Value?.DeepClone());
        graph["9"]!["inputs"]!["images"] = new JsonArray("78", 0);
    }

    private static async Task AddFinishingNodesAsync(JsonObject graph, string sourceId, string outputId, CancellationToken ct)
    {
        var finishing = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("full-quality-2x.api.json", ct))!.AsObject();
        finishing["62"]!["inputs"]!["image"] = new JsonArray(sourceId, 0);
        finishing["63"]!["inputs"]!["image"] = new JsonArray(sourceId, 0);
        foreach (var node in finishing)
            graph.Add(node.Key, node.Value?.DeepClone());
        graph[outputId]!["inputs"]!["images"] = new JsonArray("64", 0);
    }

    public async Task<byte[]> UpscaleAsync(byte[] image, int width, int height, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.GetValue("ComfyUI:TimeoutSeconds", 300)));
        var token = timeout.Token;
        await EnsureComfyUiReadyAsync(token);
        await ReleaseLocalPromptEnhancerVramAsync(token);
        var graph = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("upscale-2x.api.json", token))!.AsObject();
        graph["10"]!["inputs"]!["image"] = await UploadAuxiliarySourceAsync(image, "cca_upscale_sources", token);
        await AddFinishingNodesAsync(graph, "10", "40", token);
        var result = await RunAuxiliaryWorkflowAsync(graph, token);
        var outputs = result["outputs"]?["40"]?["images"] as JsonArray;
        var output = outputs?.FirstOrDefault();
        var filename = output?["filename"]?.GetValue<string>();
        var subfolder = output?["subfolder"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(filename) || filename != Path.GetFileName(filename) ||
            subfolder.Length != 0 || output?["type"]?.GetValue<string>() != "temp")
            throw new InvalidOperationException("ComfyUI không trả ảnh làm nét hợp lệ.");

        using var response = await _http.GetAsync($"/view?filename={Uri.EscapeDataString(filename)}&type=temp",
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedBytesAsync(response.Content, 36_000_000, token);
        var dimensions = ParsePngDimensions(bytes, 0, 0);
        if (dimensions != (width * 2, height * 2))
            throw new InvalidOperationException("Kích thước ảnh làm nét không đúng gấp đôi ảnh nguồn.");
        Volatile.Write(ref _lastComfyHealthyTicks, Environment.TickCount64);
        return bytes;
    }
}
