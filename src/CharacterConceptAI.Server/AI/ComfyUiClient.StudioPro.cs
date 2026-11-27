using System.Text.Json.Nodes;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

public partial class ComfyUiClient
{
    private static string InjectLoraChain(JsonObject graph, IReadOnlyList<LoraSelectionDto>? loras, string baseModelNodeId = "28")
    {
        if (loras is null || loras.Count == 0)
            return baseModelNodeId;

        var prevNode = baseModelNodeId;
        int nodeNum = 90;
        foreach (var lora in loras.Take(3))
        {
            if (string.IsNullOrWhiteSpace(lora.FileName)) continue;
            var strength = Math.Clamp(lora.Strength, 0.05, 1.50);
            var id = (nodeNum++).ToString();
            graph[id] = new JsonObject
            {
                ["class_type"] = "LoraLoaderModelOnly",
                ["inputs"] = new JsonObject
                {
                    ["model"] = new JsonArray(prevNode, 0),
                    ["lora_name"] = lora.FileName.Trim(),
                    ["strength_model"] = Math.Round(strength, 2)
                }
            };
            prevNode = id;
        }

        if (prevNode != baseModelNodeId && graph["11"]?["inputs"] is JsonObject samplingInputs)
            samplingInputs["model"] = new JsonArray(prevNode, 0);

        return prevNode;
    }

    private async Task<byte[]> DownloadAuxiliaryOutputImageAsync(JsonObject historyResult, string outputNodeId, CancellationToken ct)
    {
        var outputs = historyResult["outputs"]?[outputNodeId]?["images"] as JsonArray;
        var output = outputs?.FirstOrDefault();
        var filename = output?["filename"]?.GetValue<string>();
        var subfolder = output?["subfolder"]?.GetValue<string>() ?? string.Empty;
        var type = output?["type"]?.GetValue<string>() ?? "temp";
        if (string.IsNullOrWhiteSpace(filename) || filename != Path.GetFileName(filename))
            throw new InvalidOperationException("ComfyUI không trả ảnh kết quả hợp lệ.");

        var query = $"/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        using var response = await _http.GetAsync(query, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        Volatile.Write(ref _lastComfyHealthyTicks, Environment.TickCount64);
        return await ReadBoundedBytesAsync(response.Content, 36_000_000, ct);
    }

    public async Task<byte[]> RemoveBackgroundAsync(
        byte[] image,
        double edgeSmooth = 0.6,
        double foregroundBoost = 0.0,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var token = timeout.Token;

        await EnsureComfyUiReadyAsync(token);
        var graph = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("remove-background.api.json", token))!.AsObject();
        graph["10"]!["inputs"]!["image"] = await UploadAuxiliarySourceAsync(image, "cca_rmbg_sources", token);
        graph["20"]!["inputs"]!["edge_smooth"] = Math.Clamp(edgeSmooth, 0.0, 3.0);
        graph["20"]!["inputs"]!["foreground_boost"] = Math.Clamp(foregroundBoost, -0.25, 0.25);

        var result = await RunAuxiliaryWorkflowAsync(graph, token);
        return await DownloadAuxiliaryOutputImageAsync(result, "40", token);
    }

    public async Task<byte[]> HiresUpscale2KAsync(
        byte[] image,
        string? prompt,
        bool useTiledHires = true,
        double denoise = 0.24,
        string? upscaler = null,
        IReadOnlyList<LoraSelectionDto>? loras = null,
        long? seed = null,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.GetValue("ComfyUI:TimeoutSeconds", 360)));
        var token = timeout.Token;

        await EnsureComfyUiReadyAsync(token);
        await ReleaseLocalPromptEnhancerVramAsync(token);

        var effectivePrompt = string.IsNullOrWhiteSpace(prompt)
            ? "ultra-detailed high-resolution photograph, crisp natural skin texture, fine hair strands, delicate fabric weave, true-to-life lighting"
            : $"{prompt.Trim()}, ultra-detailed 2K crisp natural texture, fine skin pores, sharp hair strands, realistic fabric weave";

        var chosenUpscaler = ResolveSmartUpscaler(upscaler, effectivePrompt);
        var uploaded = await UploadAuxiliarySourceAsync(image, "cca_hires_sources", token);

        if (!useTiledHires)
        {
            // Fast supersampled 4x -> 2x pass (~3-4s, ultra crisp!)
            var fastGraph = new JsonObject
            {
                ["10"] = new JsonObject
                {
                    ["class_type"] = "LoadImage",
                    ["inputs"] = new JsonObject { ["image"] = uploaded }
                },
                ["61"] = new JsonObject
                {
                    ["class_type"] = "UpscaleModelLoader",
                    ["inputs"] = new JsonObject { ["model_name"] = chosenUpscaler }
                },
                ["62"] = new JsonObject
                {
                    ["class_type"] = "CcaSmartUpscale2x",
                    ["inputs"] = new JsonObject
                    {
                        ["image"] = new JsonArray("10", 0),
                        ["upscale_model"] = new JsonArray("61", 0),
                        ["detail_mix"] = 0.84
                    }
                },
                ["40"] = new JsonObject
                {
                    ["class_type"] = "PreviewImage",
                    ["inputs"] = new JsonObject { ["images"] = new JsonArray("62", 0) }
                }
            };
            var fastRes = await RunAuxiliaryWorkflowAsync(fastGraph, token);
            return await DownloadAuxiliaryOutputImageAsync(fastRes, "40", token);
        }

        var graph = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("hires-fix-2k.api.json", token))!.AsObject();
        graph["10"]!["inputs"]!["image"] = uploaded;
        graph["61"]!["inputs"]!["model_name"] = chosenUpscaler;
        graph["27"]!["inputs"]!["text"] = effectivePrompt;
        graph["70"]!["inputs"]!["seed"] = seed ?? Random.Shared.Next(10000, 999999);
        graph["70"]!["inputs"]!["denoise"] = Math.Round(Math.Clamp(denoise, 0.12, 0.42), 2);
        InjectLoraChain(graph, loras);

        var result = await RunAuxiliaryWorkflowAsync(graph, token);
        return await DownloadAuxiliaryOutputImageAsync(result, "40", token);
    }

    public async Task<byte[]> InpaintAsync(
        byte[] sourceImage,
        byte[] maskImage,
        string prompt,
        double controlStrength = 0.90,
        int maskDilate = 6,
        int featherPx = 14,
        long? seed = null,
        IReadOnlyList<LoraSelectionDto>? loras = null,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.GetValue("ComfyUI:TimeoutSeconds", 300)));
        var token = timeout.Token;

        await EnsureComfyUiReadyAsync(token);
        await ReleaseLocalPromptEnhancerVramAsync(token);

        var uploadSrc = UploadAuxiliarySourceAsync(sourceImage, "cca_inpaint_sources", token);
        var uploadMask = UploadAuxiliarySourceAsync(maskImage, "cca_inpaint_masks", token);
        await Task.WhenAll(uploadSrc, uploadMask);

        var graph = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("inpaint-union21.api.json", token))!.AsObject();
        graph["10"]!["inputs"]!["image"] = uploadSrc.Result;
        graph["12"]!["inputs"]!["image"] = uploadMask.Result;
        graph["15"]!["inputs"]!["mask_dilate"] = Math.Clamp(maskDilate, 0, 48);
        graph["15"]!["inputs"]!["feather_px"] = Math.Clamp(featherPx, 0, 64);
        graph["43"]!["inputs"]!["strength"] = Math.Round(Math.Clamp(controlStrength, 0.35, 1.25), 2);
        graph["27"]!["inputs"]!["text"] = string.IsNullOrWhiteSpace(prompt)
            ? "seamless natural realistic detail matching surrounding lighting, skin texture, and style"
            : prompt.Trim();
        graph["3"]!["inputs"]!["seed"] = seed ?? Random.Shared.Next(10000, 999999);
        InjectLoraChain(graph, loras);

        var result = await RunAuxiliaryWorkflowAsync(graph, token);
        return await DownloadAuxiliaryOutputImageAsync(result, "40", token);
    }

    public async Task<byte[]> OutpaintAsync(
        byte[] sourceImage,
        string prompt,
        string targetPreset = "16:9",
        int padLeft = 0,
        int padRight = 0,
        int padTop = 0,
        int padBottom = 0,
        double controlStrength = 0.90,
        long? seed = null,
        IReadOnlyList<LoraSelectionDto>? loras = null,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.GetValue("ComfyUI:TimeoutSeconds", 300)));
        var token = timeout.Token;

        await EnsureComfyUiReadyAsync(token);
        await ReleaseLocalPromptEnhancerVramAsync(token);

        var uploaded = await UploadAuxiliarySourceAsync(sourceImage, "cca_outpaint_sources", token);
        var graph = JsonNode.Parse(await LoadAuxiliaryWorkflowAsync("outpaint-union21.api.json", token))!.AsObject();
        graph["10"]!["inputs"]!["image"] = uploaded;
        graph["15"]!["inputs"]!["target_preset"] = string.IsNullOrWhiteSpace(targetPreset) ? "16:9" : targetPreset.Trim();
        graph["15"]!["inputs"]!["pad_left"] = Math.Clamp(padLeft, 0, 768);
        graph["15"]!["inputs"]!["pad_right"] = Math.Clamp(padRight, 0, 768);
        graph["15"]!["inputs"]!["pad_top"] = Math.Clamp(padTop, 0, 768);
        graph["15"]!["inputs"]!["pad_bottom"] = Math.Clamp(padBottom, 0, 768);
        graph["43"]!["inputs"]!["strength"] = Math.Round(Math.Clamp(controlStrength, 0.35, 1.25), 2);
        graph["27"]!["inputs"]!["text"] = string.IsNullOrWhiteSpace(prompt)
            ? "seamless natural wide background extension matching lighting, depth of field, colors, and environment"
            : prompt.Trim();
        graph["3"]!["inputs"]!["seed"] = seed ?? Random.Shared.Next(10000, 999999);
        InjectLoraChain(graph, loras);

        var result = await RunAuxiliaryWorkflowAsync(graph, token);
        return await DownloadAuxiliaryOutputImageAsync(result, "40", token);
    }

    private static long _lastWarmupTicks;

    /// <summary>
    /// Pre-loads Z-Image-Turbo UNET, Qwen3-4B FP8 CLIP, Flux VAE, and CUDA kernels into ComfyUI's VRAM/RAM cache
    /// via a single 1-step 256x256 pass during the app splash screen so the user's very first generation starts immediately.
    /// </summary>
    public async Task<(bool WarmedUp, string Status, double ElapsedSeconds)> WarmupModelsAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;

        await EnsureComfyUiReadyAsync(token);

        var last = Volatile.Read(ref _lastWarmupTicks);
        if (last != 0 && Environment.TickCount64 - last < 300_000)
            return (true, "already_warm", sw.Elapsed.TotalSeconds);

        await ReleaseLocalPromptEnhancerVramAsync(token);

        var warmupGraph = new JsonObject
        {
            ["28"] = new JsonObject
            {
                ["class_type"] = "UNETLoader",
                ["inputs"] = new JsonObject
                {
                    ["unet_name"] = "z_image_turbo_int8_convrot.safetensors",
                    ["weight_dtype"] = "default"
                }
            },
            ["30"] = new JsonObject
            {
                ["class_type"] = "CLIPLoader",
                ["inputs"] = new JsonObject
                {
                    ["clip_name"] = "qwen_3_4b_fp8_mixed.safetensors",
                    ["type"] = "lumina2",
                    ["device"] = "default"
                }
            },
            ["29"] = new JsonObject
            {
                ["class_type"] = "VAELoader",
                ["inputs"] = new JsonObject { ["vae_name"] = "ae.safetensors" }
            },
            ["11"] = new JsonObject
            {
                ["class_type"] = "ModelSamplingAuraFlow",
                ["inputs"] = new JsonObject { ["model"] = new JsonArray("28", 0), ["shift"] = 2.0 }
            },
            ["27"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject { ["text"] = "gula studio warmup", ["clip"] = new JsonArray("30", 0) }
            },
            ["33"] = new JsonObject
            {
                ["class_type"] = "ConditioningZeroOut",
                ["inputs"] = new JsonObject { ["conditioning"] = new JsonArray("27", 0) }
            },
            ["13"] = new JsonObject
            {
                ["class_type"] = "EmptySD3LatentImage",
                ["inputs"] = new JsonObject { ["width"] = 256, ["height"] = 256, ["batch_size"] = 1 }
            },
            ["3"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["model"] = new JsonArray("11", 0),
                    ["positive"] = new JsonArray("27", 0),
                    ["negative"] = new JsonArray("33", 0),
                    ["latent_image"] = new JsonArray("13", 0),
                    ["seed"] = 1,
                    ["steps"] = 1,
                    ["cfg"] = 1.0,
                    ["sampler_name"] = "res_multistep",
                    ["scheduler"] = "simple",
                    ["denoise"] = 1.0
                }
            },
            ["8"] = new JsonObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JsonObject { ["samples"] = new JsonArray("3", 0), ["vae"] = new JsonArray("29", 0) }
            },
            ["40"] = new JsonObject
            {
                ["class_type"] = "PreviewImage",
                ["inputs"] = new JsonObject { ["images"] = new JsonArray("8", 0) }
            }
        };

        await RunAuxiliaryWorkflowAsync(warmupGraph, token);
        Volatile.Write(ref _lastWarmupTicks, Environment.TickCount64);
        sw.Stop();
        return (true, "warmed", Math.Round(sw.Elapsed.TotalSeconds, 2));
    }

    private string ResolveSmartUpscaler(string? requested, string prompt)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            requested.Trim() == Path.GetFileName(requested.Trim()) &&
            requested.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
        {
            return requested.Trim();
        }

        var upscaleDir = _config["ComfyUI:UpscaleModelsPath"];
        if (string.IsNullOrWhiteSpace(upscaleDir))
        {
            var home = Environment.GetEnvironmentVariable("GULA_STUDIO_HOME");
            if (!string.IsNullOrWhiteSpace(home))
                upscaleDir = Path.Combine(home, "ComfyUI", "models", "upscale_models");
            else
                upscaleDir = Path.Combine(Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? AppContext.BaseDirectory, "ComfyUI", "models", "upscale_models");
        }
        var explicitPhoto = FaceRefinePhotoRegex.IsMatch(prompt);
        var isAnime = FaceRefineAnimeRegex.IsMatch(prompt) && !explicitPhoto;

        if (isAnime && File.Exists(Path.Combine(upscaleDir, "4x-UltraSharp.pth")))
            return "4x-UltraSharp.pth";
        if (File.Exists(Path.Combine(upscaleDir, "4x_NMKD-Siax_200k.pth")))
            return "4x_NMKD-Siax_200k.pth";
        if (File.Exists(Path.Combine(upscaleDir, "4x-UltraSharp.pth")))
            return "4x-UltraSharp.pth";
        return "RealESRGAN_x2plus.pth";
    }
}
