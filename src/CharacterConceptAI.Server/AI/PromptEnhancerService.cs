using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

/// <summary>
/// Process-wide coordinator tracking whether external local llama-server has loaded weights into VRAM.
/// Eliminates both VRAM collisions (when user runs Enhance then Generate &lt;8s later) and 2s TCP-refused stalls
/// when llama-server was never used or is stopped.
/// </summary>
public static class PromptEnhancerVramCoordinator
{
    private static int _externalLlmLoaded;

    public static void MarkExternalLlmLoaded() => Volatile.Write(ref _externalLlmLoaded, 1);

    public static bool TryConsumeExternalLlmLoaded() => Interlocked.Exchange(ref _externalLlmLoaded, 0) == 1;
}

public interface IPromptEnhancerService
{
    Task<EnhancePromptResponse> EnhanceAsync(EnhancePromptRequest request, CancellationToken ct = default);
}

/// <summary>
/// Implements Tongyi-MAI/Z-Image-Turbo Prompt Enhancer (pe.py) rules across 3 clean tiers:
/// 1) Optional external OpenAI-compatible / Ollama LLM (PromptEnhancer:EndpointUrl).
/// 2) Native local Qwen3-4B inside ComfyUI (TextGenerate node on qwen_3_4b_fp8_mixed.safetensors, zero extra downloads!).
/// 3) Instant deterministic Z-Captioner fallback (subject-first, zero camera/lens/metal leakage, exact "..." text quoting).
/// </summary>
public sealed class PromptEnhancerService : IPromptEnhancerService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<PromptEnhancerService> _logger;
    private static string? _cachedComfyTemplate;

    public const string OfficialZImagePeSystemPrompt =
        """
        You are the official Z-Image-Turbo Prompt Enhancer (Tongyi-MAI pe.py) — a vision artist bound by strict logic who translates the user's idea into ONE rich, objective, concrete visual paragraph strictly in fluent English (70 to 110 words) ready for text-to-image generation.
        Follow the official 4-step Z-Image PE logical sequence:
        1. Core Lock: Preserve every immutable element from the user's input (subject, count, exact action, clothing, colors, IP names, and authentic Vietnamese rural/cultural details when the input is in Vietnamese). Put the main subject and exact action in the very first sentence.
        2. Generative Reasoning: If the prompt asks for world knowledge, coordinates, a design, steps, or a concept rather than a plain scene, resolve it into a concrete visualizable scene (e.g., geolocation coordinates -> actual real-world landmark scene; step-by-step guide -> concrete illustrated panels).
        3. Objective Visual Aesthetics: Specify framing, natural directional lighting, shadow interplay, tactile clothing/environment textures, cohesive colors, and layered foreground/midground/background depth.
        4. Exact Text Quoting: If any text must appear in the image, transcribe it verbatim inside English double quotes ("...") and describe its typography, material, and placement.
        Strict bans:
        - Output ONLY pure fluent English (never mix Vietnamese or Chinese outside quoted text "...").
        - NEVER write camera hardware/brand words (camera, lens, DSLR, 85mm, 50mm, 35mm, full-frame, photographer, tripod) so no stray camera is drawn.
        - NEVER write buzzwords ("8K", "4K", "UHD", "masterpiece", "best quality", "photorealistic style") or metaphors.
        - Output ONLY the final enhanced prompt paragraph with no title, preamble, or markdown.
        """;

    // Buzzword meta-tags & hardware nouns that cause literal object leakage in Z-Image.
    private static readonly Regex BuzzwordPattern = new(
        @"(?:,?\s*\b(?:8k|4k|uhd|hdr|masterpiece|best quality|highest quality|ultra[- ]?detailed|hyper[- ]?detailed|super detailed|trending on artstation|award[- ]?winning|insanely detailed|photorealistic style)\b)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex MultiCommaPattern = new(@"\s*,\s*(?:,\s*)+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MultiSpacePattern = new(@"\s{2,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ThinkTagPattern = new(@"<think>[\s\S]*?(?:</think>|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRunPattern = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QuotedSegmentSplitPattern = new("(\"[^\"]*\")", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly object LlmCacheLock = new();
    private static readonly LinkedList<(string Key, EnhancePromptResponse Value)> LlmCacheOrder = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, EnhancePromptResponse Value)>> LlmCacheMap = new(StringComparer.Ordinal);
    private const int MaxLlmCacheEntries = 64;

    private static bool TryGetCachedEnhance(string key, out EnhancePromptResponse response)
    {
        lock (LlmCacheLock)
        {
            if (LlmCacheMap.TryGetValue(key, out var node))
            {
                LlmCacheOrder.Remove(node);
                LlmCacheOrder.AddFirst(node);
                response = node.Value.Value;
                return true;
            }
        }
        response = default!;
        return false;
    }

    private static void StoreCachedEnhance(string key, EnhancePromptResponse response)
    {
        lock (LlmCacheLock)
        {
            if (LlmCacheMap.TryGetValue(key, out var existing))
            {
                LlmCacheOrder.Remove(existing);
                LlmCacheMap.Remove(key);
            }
            var node = LlmCacheOrder.AddFirst((key, response));
            LlmCacheMap[key] = node;
            while (LlmCacheMap.Count > MaxLlmCacheEntries && LlmCacheOrder.Last is { } oldest)
            {
                LlmCacheMap.Remove(oldest.Value.Key);
                LlmCacheOrder.RemoveLast();
            }
        }
    }

    public PromptEnhancerService(HttpClient http, IConfiguration config, ILogger<PromptEnhancerService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<EnhancePromptResponse> EnhanceAsync(EnhancePromptRequest request, CancellationToken ct = default)
    {
        var raw = request.Prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new EnhancePromptResponse(string.Empty, "Z-Captioner", []);
        }

        // Tier 1: External OpenAI-compatible / Ollama LLM if configured
        var llmEndpoint = _config["PromptEnhancer:EndpointUrl"];
        var llmModel = _config["PromptEnhancer:Model"] ?? "qwen3:4b";
        var cacheKey = $"{llmEndpoint}|{llmModel}|{raw}|{request.StylePreset}|{request.OverlayText}|{request.OverlayTextStyle}|{request.OverlayTextPosition}|{request.EnableReasoning}";
        if (!string.IsNullOrWhiteSpace(llmEndpoint))
        {
            if (TryGetCachedEnhance(cacheKey, out var cachedResp))
                return cachedResp;

            var llmResult = await TryEnhanceWithExternalLlmAsync(llmEndpoint.Trim(), llmModel, request, ct);
            if (!string.IsNullOrWhiteSpace(llmResult))
            {
                var result = new EnhancePromptResponse(
                    llmResult.Trim(),
                    $"LLM ({llmModel}) · Tongyi-MAI pe.py",
                    ["Đã nâng cấp bằng LLM cục bộ theo chuẩn Tongyi-MAI Z-Image pe.py."]);
                StoreCachedEnhance(cacheKey, result);
                return result;
            }
        }

        // Tier 2: Real Qwen3-4B TextGenerate inside ComfyUI (already on disk, zero extra downloads!)
        var providerMode = _config["ComfyUI:Provider"] ?? "Real";
        var useComfyQwen = _config.GetValue("PromptEnhancer:UseComfyUiQwen", false)
            && !providerMode.Equals("Fake", StringComparison.OrdinalIgnoreCase)
            && !providerMode.Equals("Simulated", StringComparison.OrdinalIgnoreCase);

        if (useComfyQwen)
        {
            var comfyResult = await TryEnhanceWithComfyUiQwenAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(comfyResult))
            {
                return new EnhancePromptResponse(
                    comfyResult,
                    "Qwen3-4B AI (ComfyUI · Tongyi-MAI pe.py)",
                    ["Đã dùng mô hình Qwen3-4B trong ComfyUI viết lại ngữ cảnh chi tiết, loại bỏ lỗi vẽ nhầm vật thể."]);
            }
        }

        // Tier 3: Instant subject-first Z-Captioner fallback (zero camera/lens/metal object leakage!)
        return EnhanceWithZCaptionerRules(request);
    }

    private async Task<string?> TryEnhanceWithComfyUiQwenAsync(EnhancePromptRequest request, CancellationToken ct)
    {
        try
        {
            var comfyBase = (_config["ComfyUI:BaseUrl"] ?? "http://127.0.0.1:8188").TrimEnd('/');
            var templateJson = await LoadComfyEnhanceTemplateAsync(ct);
            var graph = JsonNode.Parse(templateJson)?.AsObject();
            if (graph is null) return null;

            var styleName = GetEnglishStyleDescription((request.StylePreset ?? "none").Trim().ToLowerInvariant());
            var sb = new StringBuilder();
            sb.AppendLine(OfficialZImagePeSystemPrompt.Trim());
            sb.AppendLine();
            sb.AppendLine($"Visual style: {styleName}");
            if (!string.IsNullOrWhiteSpace(request.OverlayText))
            {
                var cleanText = request.OverlayText.Trim().Trim('"');
                sb.AppendLine($"Exact text to appear in the image (keep verbatim in double quotes): \"{cleanText}\" (style: {request.OverlayTextStyle ?? "modern"}, placement: {request.OverlayTextPosition ?? "auto"})");
            }
            sb.Append($"User idea: {request.Prompt.Trim()} /no_think");

            graph["40"]!["inputs"]!["prompt"] = sb.ToString();
            graph["40"]!["inputs"]!["sampling_mode.seed"] = Random.Shared.Next(1, 999999);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(85));

            var payload = new JsonObject
            {
                ["prompt"] = graph,
                ["client_id"] = $"cca_pe_{Guid.NewGuid():N}"
            };
            using var postContent = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var submitResp = await _http.PostAsync($"{comfyBase}/prompt", postContent, timeoutCts.Token);
            if (!submitResp.IsSuccessStatusCode) return null;

            var submitObj = await submitResp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: timeoutCts.Token);
            var promptId = submitObj?["prompt_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(promptId)) return null;

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, timeoutCts.Token);
                using var histResp = await _http.GetAsync($"{comfyBase}/history/{promptId}", timeoutCts.Token);
                if (!histResp.IsSuccessStatusCode) continue;

                var histObj = await histResp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: timeoutCts.Token);
                if (histObj?[promptId] is not JsonObject item) continue;

                if (item["status"]?["status_str"]?.GetValue<string>() != "success")
                    return null;

                var rawOut = item["outputs"]?["50"]?["text"]?[0]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(rawOut)) return null;

                var cleaned = CleanLlmOutput(rawOut, request.OverlayText, request.OverlayTextStyle, request.OverlayTextPosition);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComfyUI Qwen3-4B prompt enhancer timed out or failed; falling back to clean Z-Captioner rules.");
            return null;
        }
    }

    private static string CleanLlmOutput(string rawOut, string? overlayText, string? overlayStyle, string? overlayPos)
    {
        // Strip <think>...</think> blocks cleanly
        var text = ThinkTagPattern.Replace(rawOut, string.Empty).Trim();
        text = text.Trim('"', '“', '”', ' ', '\r', '\n');
        text = WhitespaceRunPattern.Replace(text, " ").Trim();
        text = StripBuzzwordsOutsideQuotes(text);

        // Ensure exact quoted OverlayText was not dropped by the LLM
        if (!string.IsNullOrWhiteSpace(overlayText))
        {
            var cleanOverlay = overlayText.Trim().Trim('"');
            if (cleanOverlay.Length > 0 && !text.Contains($"\"{cleanOverlay}\"", StringComparison.Ordinal))
            {
                var directive = BuildTextOverlayDirective(cleanOverlay, overlayStyle, overlayPos, isVietnamese: false);
                text = text.TrimEnd('.', ',') + ", " + directive + ".";
            }
        }
        return text;
    }

    private static async Task<string> LoadComfyEnhanceTemplateAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedComfyTemplate)) return _cachedComfyTemplate;
        var path = Path.Combine(AppContext.BaseDirectory, "AI", "WorkflowTemplates", "qwen3-prompt-enhance.api.json");
        if (File.Exists(path))
        {
            _cachedComfyTemplate = await File.ReadAllTextAsync(path, ct);
            return _cachedComfyTemplate;
        }
        var alt = Path.Combine(Directory.GetCurrentDirectory(), "AI", "WorkflowTemplates", "qwen3-prompt-enhance.api.json");
        if (File.Exists(alt))
        {
            _cachedComfyTemplate = await File.ReadAllTextAsync(alt, ct);
            return _cachedComfyTemplate;
        }
        throw new FileNotFoundException("Missing qwen3-prompt-enhance.api.json template.");
    }

    private async Task<string?> TryEnhanceWithExternalLlmAsync(string endpointUrl, string model, EnhancePromptRequest request, CancellationToken ct)
    {
        try
        {
            var useReasoning = request.EnableReasoning || NeedsReasoningChain(request.Prompt);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(useReasoning ? 35 : 18));

            var userContent = new StringBuilder();
            userContent.Append("Style: ").Append(GetEnglishStyleDescription((request.StylePreset ?? "none").Trim().ToLowerInvariant()));
            if (!string.IsNullOrWhiteSpace(request.OverlayText))
                userContent.Append($"\nExact text to render verbatim in image: \"{request.OverlayText.Trim().Trim('"')}\" (style: {request.OverlayTextStyle ?? "modern"}, position: {request.OverlayTextPosition ?? "auto"})");
            userContent.Append("\nUser idea: ").Append(request.Prompt.Trim());

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["temperature"] = 0.65,
                ["top_p"] = 0.85,
                ["max_tokens"] = useReasoning ? 768 : 190,
                ["reasoning_budget"] = useReasoning ? 220 : 0,
                ["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = useReasoning },
                ["messages"] = new[]
                {
                    new { role = "system", content = OfficialZImagePeSystemPrompt },
                    new { role = "user", content = userContent.ToString() }
                }
            };

            // Mark VRAM active before contacting llama-server so ComfyUiClient always unloads it even <8s later.
            PromptEnhancerVramCoordinator.MarkExternalLlmLoaded();

            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.PostAsJsonAsync(endpointUrl, payload, timeoutCts.Token);
                if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    // llama-server may briefly return 503 while waking model from --sleep-idle-seconds.
                    resp.Dispose();
                    await Task.Delay(600, timeoutCts.Token);
                    resp = await _http.PostAsJsonAsync(endpointUrl, payload, timeoutCts.Token);
                }
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: timeoutCts.Token);
                var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                    ?? json?["message"]?["content"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(content)) return null;

                return CleanLlmOutput(content, request.OverlayText, request.OverlayTextStyle, request.OverlayTextPosition);
            }
            finally
            {
                resp?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local LLM prompt enhancer unavailable at {Endpoint}.", endpointUrl);
            return null;
        }
    }

    private static bool NeedsReasoningChain(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var p = prompt.ToLowerInvariant();
        return p.Contains('°')
            || p.Contains("tọa độ")
            || p.Contains("coordinate")
            || p.Contains("các bước")
            || p.Contains("hướng dẫn")
            || p.Contains("step by step")
            || p.Contains("infographic")
            || p.Contains("giải bài")
            || p.Contains("sơ đồ");
    }

    /// <summary>
    /// Subject-first deterministic Z-Captioner enhancer.
    /// IMPORTANT: Never injects camera/lens/metal nouns so Z-Image never draws stray cameras or metal objects.
    /// </summary>
    public static EnhancePromptResponse EnhanceWithZCaptionerRules(EnhancePromptRequest request)
    {
        var notes = new List<string>();
        var core = request.Prompt.Trim();

        var cleaned = StripBuzzwordsOutsideQuotes(core);
        if (!string.Equals(cleaned, core, StringComparison.Ordinal))
        {
            notes.Add("Đã lược bỏ các từ khóa ảo (8K/masterpiece) theo khuyến nghị Z-Image pe.py.");
        }
        core = string.IsNullOrWhiteSpace(cleaned) ? core : cleaned;

        var isVietnamese = ContainsVietnameseDiacritics(core);
        var styleKey = (request.StylePreset ?? "none").Trim().ToLowerInvariant();
        var lower = core.ToLowerInvariant();

        var parts = new List<string>
        {
            // 1. ALWAYS put the user's core subject & action FIRST for maximum attention weight!
            core.TrimEnd('.', ',')
        };

        // 2. Purely visual style descriptor AFTER the subject (zero camera/lens/DSLR nouns!)
        var styleCue = GetSafeStyleSuffix(styleKey, isVietnamese);
        if (!string.IsNullOrWhiteSpace(styleCue) && !AlreadyContainsMedium(lower, styleKey))
        {
            parts.Add(styleCue);
            notes.Add($"Áp dụng phong cách {styleKey}.");
        }

        // 3. Framing & depth (purely compositional, no camera hardware!)
        if (!HasFramingCue(lower))
        {
            var aspect = request.Height > 0 ? (double)request.Width / request.Height : 1.0;
            parts.Add(aspect switch
            {
                > 1.35 => isVietnamese
                    ? "góc nhìn toàn cảnh ngang rộng có chiều sâu không gian rõ ràng, hậu cảnh mở thoáng"
                    : "wide horizontal framing with layered foreground-to-background spatial depth",
                < 0.82 => isVietnamese
                    ? "khung hình dọc cân đối, chủ thể rõ nét nổi bật trên hậu cảnh xóa phông nhẹ tự nhiên"
                    : "balanced vertical framing with crisp subject focus and soft natural background blur",
                _ => isVietnamese
                    ? "bố cục tự nhiên cân đối, chủ thể rõ nét với hậu cảnh có chiều sâu mềm mại"
                    : "natural balanced composition with clear subject focus and gentle background depth"
            });
            notes.Add("Bổ sung bố cục cân đối theo tỷ lệ khung hình.");
        }

        // 4. Natural directional lighting
        if (!HasLightingCue(lower))
        {
            parts.Add(GetLightingCue(styleKey, isVietnamese));
            notes.Add("Bổ sung ánh sáng tự nhiên và đổ bóng hài hòa.");
        }

        // 5. Organic surface & color authenticity (NO "kim loại" / "metal" leakage!)
        if (!HasTextureCue(lower))
        {
            parts.Add(GetTextureCue(styleKey, isVietnamese));
            notes.Add("Bổ sung độ chi tiết bề mặt tự nhiên.");
        }

        // 6. Exact double-quoted text overlay (Z-Image Paper Sec. 3.1 / pe.py rule #4)
        if (!string.IsNullOrWhiteSpace(request.OverlayText))
        {
            var textClean = request.OverlayText.Trim().Trim('"');
            if (textClean.Length > 0 && !core.Contains($"\"{textClean}\"", StringComparison.Ordinal))
            {
                parts.Add(BuildTextOverlayDirective(textClean, request.OverlayTextStyle, request.OverlayTextPosition, isVietnamese));
                notes.Add($"Đã chèn chữ chuẩn ngoặc kép \"{textClean}\" cho Z-Image.");
            }
        }

        var enhanced = string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        enhanced = MultiCommaPattern.Replace(enhanced, ", ");
        enhanced = MultiSpacePattern.Replace(enhanced, " ").Trim().TrimEnd(',') + ".";

        return new EnhancePromptResponse(enhanced, "Z-Captioner Built-in (Subject-First Safe)", notes);
    }

    public static string BuildTextOverlayDirective(string text, string? textStyle, string? position, bool isVietnamese)
    {
        var clean = text.Trim().Trim('"');
        var styleDesc = (textStyle ?? "modern").Trim().ToLowerInvariant() switch
        {
            "neon" => isVietnamese ? "kiểu chữ đèn neon phát sáng sắc nét có viền sáng nhẹ" : "crisp glowing neon tube lettering with subtle light halo",
            "gold3d" => isVietnamese ? "kiểu chữ nổi 3D màu vàng ánh kim bóng bẩy" : "embossed 3D golden lettering with crisp specular highlights",
            "calligraphy" => isVietnamese ? "kiểu chữ thư pháp nghệ thuật uyển chuyển thanh đậm rõ nét" : "expressive brush calligraphy typography with clean ink strokes",
            "serif" => isVietnamese ? "kiểu chữ serif tạp chí cổ điển sang trọng rõ nét" : "refined editorial serif magazine typography with crisp legibility",
            "signboard" => isVietnamese ? "in rõ nét trên bảng hiệu cửa hàng thực tế" : "printed sharply on a realistic storefront signboard",
            _ => isVietnamese ? "kiểu chữ không chân hiện đại, đậm, rõ nét và dễ đọc" : "clean bold modern sans-serif typography with sharp legibility"
        };

        var posDesc = (position ?? "auto").Trim().ToLowerInvariant() switch
        {
            "top" => isVietnamese ? "đặt nổi bật ở phần trên cùng" : "positioned prominently across the upper section",
            "center" => isVietnamese ? "đặt cân đối ở vị trí trung tâm" : "centered cleanly as the focal headline",
            "bottom" => isVietnamese ? "đặt gọn gàng ở phần dưới cùng" : "aligned cleanly along the lower third",
            _ => isVietnamese ? "bố trí hài hòa trong khung hình" : "integrated cleanly into the composition"
        };

        return isVietnamese
            ? $"hiển thị dòng chữ chính xác \"{clean}\" bằng {styleDesc}, {posDesc}"
            : $"featuring exact crisp text reading \"{clean}\" in {styleDesc}, {posDesc}";
    }

    private static string GetEnglishStyleDescription(string styleKey) => styleKey switch
    {
        "photoreal" => "Authentic real-life candid photograph with natural textures and realistic daylight",
        "anime" => "High-craft cinematic anime key visual illustration with clean expressive linework and luminous colors",
        "3d" => "Stylized 3D animated feature-film render with soft subsurface scattering and rich lighting",
        "cyberpunk" => "Nocturnal cyberpunk sci-fi atmosphere with cyan and magenta neon reflections",
        "oilpaint" => "Painterly fine-art oil concept painting with visible textured brushwork and rich tonal depth",
        "poster" => "Modern commercial graphic design poster with clean balanced layout and studio clarity",
        "film35mm" => "Warm vintage analog film look with subtle fine grain and soft nostalgic color tones",
        _ => "Natural, vivid, high-craft visual style matching the subject"
    };

    public static string GetSafeStyleSuffix(string styleKey, bool isVietnamese) => styleKey switch
    {
        "photoreal" => isVietnamese
            ? "phong cách ảnh đời thực chân thực sống động, màu sắc trung thực tự nhiên"
            : "candid real-life photographic realism, true-to-life natural colors",
        "anime" => isVietnamese
            ? "phong cách minh họa anime điện ảnh với đường nét thanh mảnh sắc sảo và màu sắc trong trẻo"
            : "cinematic anime key visual illustration with crisp expressive linework and luminous shading",
        "3d" => isVietnamese
            ? "phong cách dựng hình 3D hoạt hình điện ảnh sắc nét với ánh sáng mềm mượt"
            : "stylized 3D animated film render with soft subsurface scattering and global illumination",
        "cyberpunk" => isVietnamese
            ? "không khí khoa học viễn tưởng cyberpunk về đêm rực rỡ ánh đèn neon xanh lam và tím hồng"
            : "nocturnal cyberpunk sci-fi atmosphere with cyan and magenta neon reflections",
        "oilpaint" => isVietnamese
            ? "phong cách tranh sơn dầu nghệ thuật với nét cọ đắp nổi giàu chiều sâu và hòa sắc cổ điển"
            : "painterly fine-art oil painting with visible expressive brushwork and rich tonal harmony",
        "poster" => isVietnamese
            ? "bố cục thiết kế áp phích đồ họa hiện đại, mảng sáng tối rõ ràng tách nền sắc nét"
            : "modern commercial graphic poster layout with crisp subject separation and clean visual hierarchy",
        "film35mm" => isVietnamese
            ? "chất màu phim nhựa cổ điển ấm áp, hạt phim mịn tự nhiên và độ tương phản dịu mắt"
            : "warm vintage analog film tone with fine natural grain and soft nostalgic contrast",
        _ => string.Empty
    };

    private static string GetLightingCue(string styleKey, bool isVietnamese) => styleKey switch
    {
        "cyberpunk" => isVietnamese
            ? "ánh sáng đèn neon tương phản mạnh, viền sáng xanh ngọc và tím hắt lên chủ thể"
            : "high-contrast neon rim lighting in cyan and magenta with subtle atmospheric haze",
        "anime" => isVietnamese
            ? "ánh sáng ngược ấm áp tạo viền sáng dịu nổi bật hình khối chủ thể"
            : "warm directional rim light highlighting contours and atmospheric depth",
        "oilpaint" => isVietnamese
            ? "ánh sáng chiaroscuro định hướng mềm mại làm nổi khối chủ thể và chuyển sắc bóng đổ"
            : "soft directional chiaroscuro lighting sculpting form and dramatic shadow gradients",
        _ => isVietnamese
            ? "ánh sáng tự nhiên dịu ấm, chuyển sắc sáng tối tinh tế và đổ bóng chân thực"
            : "soft natural directional daylight with gentle shadow transitions and realistic highlights"
    };

    private static string GetTextureCue(string styleKey, bool isVietnamese) => styleKey switch
    {
        "anime" => isVietnamese
            ? "chi tiết bề mặt tách lớp sắc nét, bảng màu hài hòa sống động"
            : "finely layered illustration details, crisp cel shading, and harmonious color grading",
        "3d" => isVietnamese
            ? "chất liệu bề mặt tinh xảo, chi tiết sắc nét và độ sâu không gian tự nhiên"
            : "finely crafted material textures, tactile surface details, and soft natural depth",
        "oilpaint" => isVietnamese
            ? "kết cấu sơn dầu trên nền toan rõ nét, hòa sắc trầm ấm giàu cảm giác hội họa"
            : "visible canvas weave, confident layered brushstrokes, and rich tonal depth",
        _ => isVietnamese
            ? "chi tiết tự nhiên, kết cấu bề mặt và cảnh vật xung quanh sắc nét sống động"
            : "authentic natural textures across subject and environment with crisp clarity"
    };

    private static string StripBuzzwordsOutsideQuotes(string text)
    {
        var segments = QuotedSegmentSplitPattern.Split(text);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('"') && segments[i].EndsWith('"')) continue;
            segments[i] = BuzzwordPattern.Replace(segments[i], string.Empty);
        }
        var joined = string.Concat(segments);
        joined = MultiCommaPattern.Replace(joined, ", ");
        joined = MultiSpacePattern.Replace(joined, " ").Trim().Trim(',', ' ');
        return joined;
    }

    private static bool ContainsVietnameseDiacritics(string text)
        => text.Any(ch => ch is >= '\u00C0' and <= '\u024F' or >= '\u1E00' and <= '\u1EFF');

    private static bool AlreadyContainsMedium(string lower, string styleKey) => styleKey switch
    {
        "photoreal" => lower.Contains("photograph") || lower.Contains("photo of") || lower.Contains("đời thực") || lower.Contains("chân thực"),
        "anime" => lower.Contains("anime") || lower.Contains("manga") || lower.Contains("cel-shaded"),
        "3d" => lower.Contains("3d") || lower.Contains("pixar") || lower.Contains("unreal"),
        "cyberpunk" => lower.Contains("cyberpunk"),
        "oilpaint" => lower.Contains("oil paint") || lower.Contains("sơn dầu") || lower.Contains("brushstroke"),
        "poster" => lower.Contains("poster") || lower.Contains("áp phích"),
        "film35mm" => lower.Contains("phim nhựa") || lower.Contains("kodak") || lower.Contains("analog film"),
        _ => true
    };

    private static bool HasFramingCue(string lower)
        => lower.Contains("shot") || lower.Contains("portrait") || lower.Contains("close-up") || lower.Contains("closeup")
        || lower.Contains("full body") || lower.Contains("framing") || lower.Contains("composition") || lower.Contains("angle")
        || lower.Contains("bố cục") || lower.Contains("chân dung") || lower.Contains("toàn thân") || lower.Contains("cận cảnh") || lower.Contains("trung cảnh");

    private static bool HasLightingCue(string lower)
        => lower.Contains("light") || lower.Contains("sunlight") || lower.Contains("daylight") || lower.Contains("neon")
        || lower.Contains("sunset") || lower.Contains("golden hour") || lower.Contains("shadow") || lower.Contains("rim light")
        || lower.Contains("ánh sáng") || lower.Contains("hoàng hôn") || lower.Contains("đèn") || lower.Contains("nắng");

    private static bool HasTextureCue(string lower)
        => lower.Contains("texture") || lower.Contains("fabric") || lower.Contains("pores") || lower.Contains("grain")
        || lower.Contains("brushstroke") || lower.Contains("chất liệu") || lower.Contains("kết cấu") || lower.Contains("nếp vải");
}
