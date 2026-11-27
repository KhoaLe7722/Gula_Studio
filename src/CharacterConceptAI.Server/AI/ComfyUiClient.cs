using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CharacterConceptAI.Server.AI;

public partial class ComfyUiClient : IImageGenerationClient
{
    private static readonly ConcurrentDictionary<string, string> WorkflowTemplateCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> UploadedImageCache = new(StringComparer.Ordinal);
    private static long _lastComfyHealthyTicks;
    private static int _liveComfyServerFlag;
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ComfyUiClient> _logger;
    private readonly string _templatePathT2I;
    private readonly string _templatePathI2I;

    public bool IsSimulated => false;

    public ComfyUiClient(HttpClient http, IConfiguration config, ILogger<ComfyUiClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;

        var baseUrl = config["ComfyUI:BaseUrl"] ?? "http://127.0.0.1:8188";
        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri(baseUrl);
        }

        _templatePathT2I = Path.Combine(AppContext.BaseDirectory, "AI", "WorkflowTemplates", "z-image-turbo-int8-t2i.api.json");
        _templatePathI2I = Path.Combine(AppContext.BaseDirectory, "AI", "WorkflowTemplates", "z-image-turbo-int8-i2i.api.json");
    }

    public async Task<GenerationExecutionResult> GenerateAsync(GenerationExecutionRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? promptId = null;

        try
        {
            await EnsureComfyUiReadyAsync(ct);
            await ReleaseLocalPromptEnhancerVramAsync(ct);

            bool isImg2Img = (request.ReferenceImageBytes != null && request.ReferenceImageBytes.Length > 0) ||
                             !string.IsNullOrWhiteSpace(request.ReferenceImagePath);
            bool isDepthPose = request.DepthPoseImageBytes is { Length: > 0 };
            if (isDepthPose && isImg2Img)
                throw new InvalidOperationException("Tư thế Depth không thể kết hợp với Img2Img.");

            var hasFaceIdentity = request.FaceReferenceImageBytes is { Length: > 0 } && (request.FaceSwapStrength ?? 0.90) > 0.01;
            var hasSecondaryFace = hasFaceIdentity && request.SecondaryFaceReferenceImageBytes is { Length: > 0 } && (request.SecondaryFaceSwapStrength ?? 0.90) > 0.01;

            async Task<(string? RefName, string? DepthName, string? FaceName, List<string> ExtraFaceNames, string? SecondaryFaceName, List<string> SecondaryExtraFaceNames)> UploadAllInputsAsync(bool bypassCache)
            {
                Task<string?> refTask = Task.FromResult<string?>(null);
                if (isImg2Img)
                {
                    if (request.ReferenceImageBytes is { Length: > 0 })
                    {
                        var fileName = $"cca_ref_{ComputeShortHash(request.ReferenceImageBytes)}.png";
                        refTask = UploadReferenceImageAsync(request.ReferenceImageBytes, fileName, ct, bypassCache: bypassCache);
                    }
                    else if (!string.IsNullOrWhiteSpace(request.ReferenceImagePath))
                    {
                        refTask = Task.FromResult<string?>(Path.GetFileName(request.ReferenceImagePath));
                    }
                }

                Task<string?> depthTask = isDepthPose
                    ? UploadReferenceImageAsync(request.DepthPoseImageBytes!, $"cca_depth_{ComputeShortHash(request.DepthPoseImageBytes!)}.webp", ct, "image/webp", bypassCache: bypassCache)
                    : Task.FromResult<string?>(null);

                Task<string?> faceTask = hasFaceIdentity
                    ? UploadReferenceImageAsync(request.FaceReferenceImageBytes!, $"cca_face_{ComputeShortHash(request.FaceReferenceImageBytes!)}.png", ct, bypassCache: bypassCache)
                    : Task.FromResult<string?>(null);

                var extraTasks = new List<Task<string?>>();
                if (hasFaceIdentity && request.AdditionalFaceReferenceImageBytes is { Count: > 0 })
                {
                    foreach (var extraBytes in request.AdditionalFaceReferenceImageBytes.Where(b => b is { Length: > 0 }).Take(4))
                    {
                        var extraFileName = $"cca_face_{ComputeShortHash(extraBytes)}.png";
                        extraTasks.Add(UploadReferenceImageAsync(extraBytes, extraFileName, ct, bypassCache: bypassCache));
                    }
                }

                Task<string?> secondaryFaceTask = hasSecondaryFace
                    ? UploadReferenceImageAsync(request.SecondaryFaceReferenceImageBytes!, $"cca_face_{ComputeShortHash(request.SecondaryFaceReferenceImageBytes!)}.png", ct, bypassCache: bypassCache)
                    : Task.FromResult<string?>(null);

                var secondaryExtraTasks = new List<Task<string?>>();
                if (hasSecondaryFace && request.SecondaryAdditionalFaceReferenceImageBytes is { Count: > 0 })
                {
                    foreach (var extraBytes in request.SecondaryAdditionalFaceReferenceImageBytes.Where(b => b is { Length: > 0 }).Take(2))
                    {
                        var extraFileName = $"cca_face_{ComputeShortHash(extraBytes)}.png";
                        secondaryExtraTasks.Add(UploadReferenceImageAsync(extraBytes, extraFileName, ct, bypassCache: bypassCache));
                    }
                }

                await Task.WhenAll(new Task[] { refTask, depthTask, faceTask, secondaryFaceTask }.Concat(extraTasks).Concat(secondaryExtraTasks));
                var extraNames = extraTasks.Select(t => t.Result).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
                var secExtraNames = secondaryExtraTasks.Select(t => t.Result).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
                return (refTask.Result, depthTask.Result, faceTask.Result, extraNames, secondaryFaceTask.Result, secExtraNames);
            }

            async Task<JsonNode> BuildGraphAsync((string? RefName, string? DepthName, string? FaceName, List<string> ExtraFaceNames, string? SecondaryFaceName, List<string> SecondaryExtraFaceNames) uploads)
            {
                var templateJson = isDepthPose
                    ? await LoadAuxiliaryWorkflowAsync("z-image-turbo-int8-depth.api.json", ct)
                    : await LoadTemplateJsonAsync(isImg2Img, ct);
                var graph = JsonNode.Parse(templateJson)
                    ?? throw new InvalidOperationException("Không thể đọc cấu trúc đồ thị workflow mẫu ComfyUI.");

                InjectWorkflowParameters(graph, request, isImg2Img, uploads.RefName);
                var activeModelNodeId = InjectLoraChain(graph.AsObject(), request.Loras);
                if (isDepthPose)
                {
                    if (graph["50"]?["inputs"] is JsonObject depthLoad && !string.IsNullOrWhiteSpace(uploads.DepthName))
                        depthLoad["image"] = uploads.DepthName;
                    if (graph["51"]?["inputs"] is JsonObject depthScale)
                    {
                        depthScale["width"] = request.Width > 0 ? request.Width : 896;
                        depthScale["height"] = request.Height > 0 ? request.Height : 1152;
                    }
                    if (graph["43"]?["inputs"] is JsonObject cnetInputs)
                    {
                        var clampedStrength = Math.Clamp(request.DepthStrength ?? 0.92, 0.35, 1.20);
                        cnetInputs["strength"] = clampedStrength;
                        cnetInputs["end_percent"] = Math.Round(Math.Clamp(0.79 - 0.10 * clampedStrength, 0.64, 0.74), 2);
                    }
                }

                string currentImageSourceNode = "8";
                if (hasSecondaryFace && !string.IsNullOrWhiteSpace(uploads.FaceName) && !string.IsNullOrWhiteSpace(uploads.SecondaryFaceName))
                {
                    var graphObj = graph.AsObject();
                    graphObj["80"] = new JsonObject
                    {
                        ["class_type"] = "LoadImage",
                        ["inputs"] = new JsonObject { ["image"] = uploads.FaceName }
                    };
                    graphObj["85"] = new JsonObject
                    {
                        ["class_type"] = "LoadImage",
                        ["inputs"] = new JsonObject { ["image"] = uploads.SecondaryFaceName }
                    };
                    var dualInputs = new JsonObject
                    {
                        ["target_image"] = new JsonArray(currentImageSourceNode, 0),
                        ["left_face"] = new JsonArray("80", 0),
                        ["right_face"] = new JsonArray("85", 0),
                        ["left_strength"] = Math.Clamp(request.FaceSwapStrength ?? 0.90, 0.0, 1.0),
                        ["left_restore"] = Math.Clamp(request.FaceRestoreStrength ?? 0.40, 0.0, 1.0),
                        ["right_strength"] = Math.Clamp(request.SecondaryFaceSwapStrength ?? 0.90, 0.0, 1.0),
                        ["right_restore"] = Math.Clamp(request.SecondaryFaceRestoreStrength ?? 0.40, 0.0, 1.0),
                        ["yaw_weight_strength"] = 0.65
                    };

                    int lSlot = 2;
                    foreach (var extra in uploads.ExtraFaceNames.Take(2))
                    {
                        var nid = (80 + lSlot).ToString();
                        graphObj[nid] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = extra } };
                        dualInputs[$"left_face_{lSlot}"] = new JsonArray(nid, 0);
                        lSlot++;
                    }
                    int rSlot = 2;
                    foreach (var extra in uploads.SecondaryExtraFaceNames.Take(2))
                    {
                        var nid = (85 + rSlot).ToString();
                        graphObj[nid] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = extra } };
                        dualInputs[$"right_face_{rSlot}"] = new JsonArray(nid, 0);
                        rSlot++;
                    }

                    graphObj["81"] = new JsonObject
                    {
                        ["class_type"] = "CcaDualFaceIdentitySwap",
                        ["inputs"] = dualInputs
                    };
                    graphObj["9"]!["inputs"]!["images"] = new JsonArray("81", 0);
                    currentImageSourceNode = "81";
                }
                else if (hasFaceIdentity && !string.IsNullOrWhiteSpace(uploads.FaceName))
                {
                    var graphObj = graph.AsObject();
                    graphObj["80"] = new JsonObject
                    {
                        ["class_type"] = "LoadImage",
                        ["inputs"] = new JsonObject { ["image"] = uploads.FaceName }
                    };
                    var swapInputs = new JsonObject
                    {
                        ["target_image"] = new JsonArray(currentImageSourceNode, 0),
                        ["source_face"] = new JsonArray("80", 0),
                        ["strength"] = Math.Clamp(request.FaceSwapStrength ?? 0.90, 0.0, 1.0),
                        ["restore_strength"] = Math.Clamp(request.FaceRestoreStrength ?? 0.40, 0.0, 1.0),
                        ["yaw_weight_strength"] = 0.65
                    };

                    int slot = 2;
                    foreach (var uploadedExtra in uploads.ExtraFaceNames)
                    {
                        var nodeId = (80 + slot).ToString();
                        graphObj[nodeId] = new JsonObject
                        {
                            ["class_type"] = "LoadImage",
                            ["inputs"] = new JsonObject { ["image"] = uploadedExtra }
                        };
                        swapInputs[$"source_face_{slot}"] = new JsonArray(nodeId, 0);
                        slot++;
                    }

                    graphObj["81"] = new JsonObject
                    {
                        ["class_type"] = "CcaFaceIdentitySwap",
                        ["inputs"] = swapInputs
                    };
                    graphObj["9"]!["inputs"]!["images"] = new JsonArray("81", 0);
                    currentImageSourceNode = "81";
                }

                if (request.FullQuality)
                {
                    await AddFinishingNodesAsync(graph.AsObject(), currentImageSourceNode, "9", ct);
                    if (request.RefineFace && !hasFaceIdentity)
                    {
                        await AddFaceRefinementAsync(graph.AsObject(), request, ct);
                        if (activeModelNodeId != "28" && graph["75"]?["inputs"] is JsonObject refineModelInputs)
                            refineModelInputs["model"] = new JsonArray(activeModelNodeId, 0);
                    }
                }

                return graph;
            }

            var uploads = await UploadAllInputsAsync(bypassCache: false);
            var rootNode = await BuildGraphAsync(uploads);

            // 3. Open WebSocket before submitting /prompt (on live ComfyUI) for <5ms completion wakeup
            var clientId = Guid.NewGuid().ToString("N");
            using var ws = await TryConnectWebSocketAsync(clientId, ct);

            _logger.LogInformation("Gửi tác vụ sinh ảnh (Job {JobId}, Seed {Seed}, {Width}x{Height}) đến ComfyUI...",
                request.JobId, request.Seed, request.Width, request.Height);

            async Task<(bool Ok, string? PromptId, JsonNode? NodeErrors, string? ErrorMessage)> SubmitGraphAsync(JsonNode graph)
            {
                var promptPayload = new JsonObject
                {
                    ["prompt"] = graph,
                    ["client_id"] = clientId
                };
                using var postContent = new StringContent(promptPayload.ToJsonString(), Encoding.UTF8, "application/json");
                using var submitResponse = await _http.PostAsync("/prompt", postContent, ct);
                if (!submitResponse.IsSuccessStatusCode)
                {
                    var errorBody = await submitResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogError("ComfyUI từ chối yêu cầu /prompt: HTTP {Status} - {Body}", submitResponse.StatusCode, errorBody);
                    return (false, null, null, $"ComfyUI trả về lỗi HTTP {submitResponse.StatusCode}: {errorBody}");
                }
                var submitResultJson = await submitResponse.Content.ReadAsStringAsync(ct);
                var submitResultObj = JsonNode.Parse(submitResultJson);
                var pid = submitResultObj?["prompt_id"]?.ToString();
                var ne = submitResultObj?["node_errors"];
                return (true, pid, ne, null);
            }

            var submitRes = await SubmitGraphAsync(rootNode);
            if (!submitRes.Ok)
            {
                return new GenerationExecutionResult(false, [], null, submitRes.ErrorMessage, sw.Elapsed);
            }

            // If ComfyUI was restarted and its input/ folder cleared, retry once with fresh uploads
            if (submitRes.NodeErrors is JsonObject neRetry && neRetry.Count > 0 &&
                (isImg2Img || isDepthPose || hasFaceIdentity) &&
                neRetry.ToJsonString().Contains("Invalid image file", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Phát hiện ảnh cache trên ComfyUI bị xóa sau khi khởi động lại — tự động tải lại và gửi lại tác vụ.");
                UploadedImageCache.Clear();
                uploads = await UploadAllInputsAsync(bypassCache: true);
                rootNode = await BuildGraphAsync(uploads);
                submitRes = await SubmitGraphAsync(rootNode);
                if (!submitRes.Ok)
                    return new GenerationExecutionResult(false, [], null, submitRes.ErrorMessage, sw.Elapsed);
            }

            promptId = submitRes.PromptId;
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return new GenerationExecutionResult(false, [], null, "ComfyUI không trả về prompt_id hợp lệ.", sw.Elapsed);
            }

            if (submitRes.NodeErrors is JsonObject neObj && neObj.Count > 0)
            {
                var errStr = neObj.ToJsonString();
                _logger.LogError("ComfyUI phát hiện lỗi cấu hình node: {Errors}", errStr);
                return new GenerationExecutionResult(false, [], promptId, $"ComfyUI node validation errors: {errStr}", sw.Elapsed);
            }

            _logger.LogInformation("ComfyUI đã tiếp nhận tác vụ với prompt_id: {PromptId}. Đang theo dõi tiến độ...", promptId);

            // 4. Wait for completion via WebSocket (<5ms wakeup) with tight adaptive /history fallback
            var timeoutSeconds = _config.GetValue("ComfyUI:TimeoutSeconds", 300);
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pollCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            JsonObject? historyItem = null;
            try
            {
                historyItem = await WaitForPromptHistoryAsync(promptId, ws, sw, pollCts.Token);
            }
            catch (OperationCanceledException) when (pollCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Distinct from user cancellation: report accurate timeout message instead of "Tác vụ đã bị hủy."
                historyItem = null;
            }

            if (historyItem is null)
            {
                ct.ThrowIfCancellationRequested();
                return new GenerationExecutionResult(
                    Success: false,
                    Outputs: [],
                    ProviderPromptId: promptId,
                    ErrorMessage: $"Hết thời gian chờ ({timeoutSeconds}s) khi xử lý tác vụ trên ComfyUI.",
                    Elapsed: sw.Elapsed
                );
            }

            // 5. Inspect status messages for execution errors
            var statusNode = historyItem["status"] as JsonObject;
            var statusStr = statusNode?["status_str"]?.ToString();
            var messagesNode = statusNode?["messages"] as JsonArray;

            if (messagesNode != null)
            {
                foreach (var msg in messagesNode)
                {
                    if (msg is JsonArray arr && arr.Count >= 2)
                    {
                        var msgType = arr[0]?.ToString();
                        if (msgType == "execution_error" || msgType == "execution_interrupted")
                        {
                            var errDetail = arr[1]?.ToJsonString() ?? "Lỗi thực thi trong đồ thị ComfyUI";
                            _logger.LogError("Lỗi thực thi ComfyUI ({Type}): {Detail}", msgType, errDetail);
                            return new GenerationExecutionResult(
                                Success: false,
                                Outputs: [],
                                ProviderPromptId: promptId,
                                ErrorMessage: $"ComfyUI {msgType}: {errDetail}",
                                Elapsed: sw.Elapsed
                            );
                        }
                    }
                }
            }
            if (string.Equals(statusStr, "error", StringComparison.OrdinalIgnoreCase))
            {
                return new GenerationExecutionResult(false, [], promptId, "ComfyUI báo lỗi thực thi (status_str = error).", sw.Elapsed);
            }

            // 6. Download output images from SaveImage node (Node "9")
            var outputsNode = historyItem["outputs"] as JsonObject;
            if (request.RefineFace && !hasFaceIdentity)
            {
                var faceStatus = outputsNode?["78"]?["cca_face_status"]?[0]?.GetValue<string>();
                if (faceStatus is not ("refined" or "no_face" or "kept_source"))
                    throw new InvalidOperationException("Workflow Full không trả trạng thái kiểm tra khuôn mặt.");
                _logger.LogInformation("Face refinement for job {JobId}: {Status}", request.JobId, faceStatus);
            }
            if (hasFaceIdentity)
            {
                var idStatus = outputsNode?["81"]?["cca_face_identity_status"]?[0]?.GetValue<string>() ?? "unknown";
                _logger.LogInformation("Face identity lock for job {JobId}: {Status}", request.JobId, idStatus);
            }
            var saveNodeOutputs = outputsNode?["9"]?["images"] as JsonArray;

            if (saveNodeOutputs is null || saveNodeOutputs.Count == 0)
            {
                return new GenerationExecutionResult(
                    Success: false,
                    Outputs: [],
                    ProviderPromptId: promptId,
                    ErrorMessage: "ComfyUI hoàn thành nhưng không tìm thấy ảnh xuất từ Node SaveImage (Node 9).",
                    Elapsed: sw.Elapsed
                );
            }

            var outputs = new List<GeneratedImageOutput>();
            foreach (var imgNode in saveNodeOutputs)
            {
                if (imgNode is not JsonObject imgObj) continue;
                var filename = imgObj["filename"]?.ToString();
                var subfolder = imgObj["subfolder"]?.ToString() ?? string.Empty;
                var type = imgObj["type"]?.ToString() ?? "output";

                if (string.IsNullOrWhiteSpace(filename)) continue;

                var viewQuery = $"/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
                using var viewResp = await _http.GetAsync(viewQuery, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!viewResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Không thể tải ảnh kết quả {Filename} từ ComfyUI view: {Status}", filename, viewResp.StatusCode);
                    continue;
                }

                var imageBytes = await ReadBoundedBytesAsync(viewResp.Content, 36_000_000, ct);

                // Full quality requires a valid 2× output before the worker can save it.
                var (width, height) = request.FullQuality
                    ? ParsePngDimensions(imageBytes, 0, 0)
                    : ParsePngDimensions(imageBytes, request.Width, request.Height);
                if (request.FullQuality && (width != request.Width * 2 || height != request.Height * 2))
                    throw new InvalidOperationException("Workflow Full không trả ảnh có kích thước 2×.");

                outputs.Add(new GeneratedImageOutput(
                    ImageBytes: imageBytes,
                    Extension: "png",
                    Width: width,
                    Height: height
                ));
            }

            Volatile.Write(ref _lastComfyHealthyTicks, Environment.TickCount64);
            sw.Stop();
            _logger.LogInformation("Tác vụ sinh ảnh ComfyUI thành công: {Count} ảnh trong {Elapsed:F1}s", outputs.Count, sw.Elapsed.TotalSeconds);

            return new GenerationExecutionResult(
                Success: outputs.Count > 0,
                Outputs: outputs,
                ProviderPromptId: promptId,
                ErrorMessage: outputs.Count > 0 ? null : "Không có ảnh nào được tải về thành công.",
                Elapsed: sw.Elapsed
            );
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Lỗi kết nối đến dịch vụ ComfyUI.");
            return new GenerationExecutionResult(
                Success: false,
                Outputs: [],
                ProviderPromptId: promptId,
                ErrorMessage: $"Không thể kết nối đến ComfyUI tại {_http.BaseAddress}: {ex.Message}. Vui lòng đảm bảo ComfyUI đang chạy.",
                Elapsed: sw.Elapsed
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Tác vụ sinh ảnh bị hủy bởi người dùng hoặc hệ thống.");
            // /interrupt is global to ComfyUI, so it could cancel another
            // application's workflow. Keep bounded local cancellation instead.

            return new GenerationExecutionResult(
                Success: false,
                Outputs: [],
                ProviderPromptId: promptId,
                ErrorMessage: "Tác vụ đã bị hủy.",
                Elapsed: sw.Elapsed
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi không xác định khi gọi ComfyUI.");
            return new GenerationExecutionResult(
                Success: false,
                Outputs: [],
                ProviderPromptId: promptId,
                ErrorMessage: $"Lỗi thực thi: {ex.Message}",
                Elapsed: sw.Elapsed
            );
        }
    }

    private static readonly object ComfyLaunchLock = new();
    private static long _lastComfyLaunchTicks;

    private async Task<ClientWebSocket?> TryConnectWebSocketAsync(string clientId, CancellationToken ct)
    {
        if (Volatile.Read(ref _liveComfyServerFlag) != 1 || _http.BaseAddress is not { IsLoopback: true } baseUri)
            return null;

        var wsScheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var wsUri = new Uri($"{wsScheme}://{baseUri.Authority}/ws?clientId={Uri.EscapeDataString(clientId)}");
        var ws = new ClientWebSocket();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(400));
            await ws.ConnectAsync(wsUri, connectCts.Token);
            return ws;
        }
        catch
        {
            ws.Dispose();
            return null;
        }
    }

    private async Task<JsonObject?> WaitForPromptHistoryAsync(string promptId, ClientWebSocket? ws, Stopwatch sw, CancellationToken ct)
    {
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                var buffer = new byte[4096];
                var sb = new StringBuilder(1024);
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    var msgText = sb.ToString();
                    sb.Clear();

                    try
                    {
                        var msgObj = JsonNode.Parse(msgText) as JsonObject;
                        var msgType = msgObj?["type"]?.GetValue<string>();
                        var data = msgObj?["data"] as JsonObject;
                        var msgPromptId = data?["prompt_id"]?.GetValue<string>();
                        if (!string.Equals(msgPromptId, promptId, StringComparison.Ordinal))
                            continue;

                        var isFinished = (msgType == "executing" && data?["node"] is null)
                            || msgType is "execution_error" or "execution_interrupted" or "execution_success";
                        if (isFinished)
                        {
                            var immediate = await TryFetchHistoryItemAsync(promptId, ct);
                            if (immediate is not null) return immediate;
                            break; // Fall through to tight /history poll for the final few ms
                        }
                    }
                    catch
                    {
                        // Ignore non-JSON/malformed WS frames
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket ComfyUI ngắt sớm — chuyển sang truy vấn /history.");
            }
        }

        while (!ct.IsCancellationRequested)
        {
            var delayMs = sw.Elapsed.TotalSeconds < 8 ? 120 : sw.Elapsed.TotalSeconds < 20 ? 250 : 400;
            await Task.Delay(delayMs, ct);

            var item = await TryFetchHistoryItemAsync(promptId, ct);
            if (item is not null) return item;
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }

    private async Task<JsonObject?> TryFetchHistoryItemAsync(string promptId, CancellationToken ct)
    {
        try
        {
            using var historyResponse = await _http.GetAsync($"/history/{promptId}", ct);
            if (historyResponse.IsSuccessStatusCode)
            {
                var historyJson = await historyResponse.Content.ReadAsStringAsync(ct);
                var historyRoot = JsonNode.Parse(historyJson) as JsonObject;
                if (historyRoot != null && historyRoot.TryGetPropertyValue(promptId, out var itemNode) && itemNode is JsonObject itemObj)
                    return itemObj;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Tạm thời không truy vấn được lịch sử ComfyUI ({Message}). Tiếp tục thử...", ex.Message);
        }
        return null;
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new InvalidOperationException($"Ảnh trả về vượt quá giới hạn dung lượng ({maxBytes / (1024 * 1024)} MB).");

        var initialCap = contentLength is > 0 and <= 36_000_000 ? (int)contentLength.Value : 65536;
        using var ms = new MemoryStream(initialCap);
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new InvalidOperationException($"Ảnh trả về vượt quá giới hạn dung lượng ({maxBytes / (1024 * 1024)} MB).");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private async Task EnsureComfyUiReadyAsync(CancellationToken ct)
    {
        if (_http.BaseAddress is not { IsLoopback: true } baseUri) return;

        // Skip redundant /system_stats round-trips during back-to-back images (20s health cache)
        var lastHealthy = Volatile.Read(ref _lastComfyHealthyTicks);
        if (Volatile.Read(ref _liveComfyServerFlag) == 1 && lastHealthy != 0 && Environment.TickCount64 - lastHealthy < 20_000)
            return;

        if (await IsComfyUiReachableAsync(ct)) return;

        // Only auto-start local run_comfyui.bat for the standard local ComfyUI port (8188), never test mock ports
        if (baseUri.Port != 8188) return;

        TryStartLocalComfyUi();

        var deadline = Environment.TickCount64 + 45_000;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(400, ct);
            if (await IsComfyUiReachableAsync(ct))
            {
                _logger.LogInformation("ComfyUI cục bộ đã sẵn sàng sau khi khởi động.");
                return;
            }
        }
    }

    private async Task<bool> IsComfyUiReachableAsync(CancellationToken ct)
    {
        try
        {
            using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quickCts.CancelAfter(TimeSpan.FromMilliseconds(800));
            using var resp = await _http.GetAsync("/system_stats", quickCts.Token);
            if (resp.IsSuccessStatusCode)
            {
                Volatile.Write(ref _liveComfyServerFlag, 1);
                Volatile.Write(ref _lastComfyHealthyTicks, Environment.TickCount64);
                return true;
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Volatile.Write(ref _liveComfyServerFlag, 0);
                return true;
            }
            return false;
        }
        catch (HttpRequestException)
        {
            Volatile.Write(ref _lastComfyHealthyTicks, 0);
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void TryStartLocalComfyUi()
    {
        lock (ComfyLaunchLock)
        {
            var now = Environment.TickCount64;
            if (_lastComfyLaunchTicks != 0 && now - _lastComfyLaunchTicks < 35_000)
                return;

            // Cross-process guard so Desktop & Server never spawn duplicate ComfyUI instances on cold boot
            using var mutex = new Mutex(false, @"Local\CharacterConceptAI.ComfyLaunch");
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(200));
                if (!acquired) return;

                var stampPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CharacterConceptAI", "comfy-launch.stamp");
                if (File.Exists(stampPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(stampPath)).TotalSeconds < 35)
                    return;

                var batPath = ResolveComfyBatchPath();
                if (batPath is null) return;

                _lastComfyLaunchTicks = now;
                Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
                File.WriteAllText(stampPath, DateTimeOffset.UtcNow.ToString("O"));
                _logger.LogInformation("ComfyUI chưa phản hồi trên cổng 8188 — tự động khởi động {BatPath}...", batPath);
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{batPath}\" --no-pause\"",
                    WorkingDirectory = Path.GetDirectoryName(batPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể tự khởi động ComfyUI.");
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
    }

    private string? ResolveComfyBatchPath()
    {
        var configured = _config["ComfyUI:LaunchScript"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var envHome = Environment.GetEnvironmentVariable("GULA_STUDIO_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            var envBat = Path.Combine(envHome.Trim(), "run_comfyui.bat");
            if (File.Exists(envBat)) return envBat;
        }

        var localAppConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CharacterConceptAI", "workspace-root.txt");
        try
        {
            if (File.Exists(localAppConfig))
            {
                var savedRoot = File.ReadAllText(localAppConfig).Trim();
                var savedBat = Path.Combine(savedRoot, "run_comfyui.bat");
                if (File.Exists(savedBat)) return savedBat;
            }
        }
        catch { }

        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var candidate = Path.Combine(current, "run_comfyui.bat");
            if (File.Exists(candidate)) return candidate;
            current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        }

        var defaultBat = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GulaStudio", "run_comfyui.bat");
        return File.Exists(defaultBat) ? defaultBat : null;
    }

    private static string ComputeShortHash(byte[] bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private async Task ReleaseLocalPromptEnhancerVramAsync(CancellationToken ct, bool force = false)
    {
        var endpoint = _config["PromptEnhancer:EndpointUrl"];
        var model = _config["PromptEnhancer:Model"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model)) return;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback) return;

        // Only contact llama-server when PromptEnhancer actually loaded Qwen3 into VRAM (or forced):
        // eliminates 2s dead-port TCP stalls when unused AND guarantees unload even <8s after Enhance!
        var wasLoaded = PromptEnhancerVramCoordinator.TryConsumeExternalLlmLoaded();
        if (!force && !wasLoaded) return;

        try
        {
            using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quickCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
            var unloadUrl = $"{uri.Scheme}://{uri.Authority}/models/unload";
            using var content = new StringContent(JsonSerializer.Serialize(new { model }), Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(unloadUrl, content, quickCts.Token);
            if (force || wasLoaded) await Task.Delay(150, ct);
        }
        catch
        {
            // Best-effort VRAM handoff; llama-server also auto-unloads via --sleep-idle-seconds.
        }
    }

    private async Task<string?> UploadReferenceImageAsync(byte[] imageBytes, string filename, CancellationToken ct, string mediaType = "image/png", bool bypassCache = false)
    {
        var useCache = !bypassCache && Volatile.Read(ref _liveComfyServerFlag) == 1;
        var cacheKey = $"{_http.BaseAddress?.Authority ?? "default"}|root|{filename}";
        if (useCache && UploadedImageCache.TryGetValue(cacheKey, out var cachedName))
            return cachedName;

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        form.Add(fileContent, "image", filename);
        form.Add(new StringContent("true"), "overwrite");

        using var response = await _http.PostAsync("/upload/image", form, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Lỗi khi tải ảnh tham chiếu lên ComfyUI: HTTP {Status} - {Error}", response.StatusCode, err);
            throw new InvalidOperationException($"Không thể tải ảnh tham chiếu lên ComfyUI (HTTP {response.StatusCode}).");
        }

        var respJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonNode.Parse(respJson);
        var uploadedName = doc?["name"]?.ToString() ?? filename;
        if (Volatile.Read(ref _liveComfyServerFlag) == 1)
        {
            if (UploadedImageCache.Count >= 256) UploadedImageCache.Clear();
            UploadedImageCache[cacheKey] = uploadedName;
        }
        return uploadedName;
    }

    private static void InjectWorkflowParameters(JsonNode rootNode, GenerationExecutionRequest request, bool isImg2Img, string? uploadedImageName)
    {
        // 1. Text Prompt -> Node "27" (CLIPTextEncode)
        if (rootNode["27"]?["inputs"] is JsonObject promptInputs)
        {
            promptInputs["text"] = request.Prompt;
        }

        if (isImg2Img)
        {
            var width = request.Width > 0 ? request.Width : 1024;
            var height = request.Height > 0 ? request.Height : 1024;

            // 2. Reference Image -> Node "10" (LoadImage)
            if (rootNode["10"]?["inputs"] is JsonObject loadImageInputs && !string.IsNullOrWhiteSpace(uploadedImageName))
            {
                loadImageInputs["image"] = uploadedImageName;
            }

            // 2a. Bounded Resize -> Node "14" (ImageScale) prevents VRAM OOM on huge user uploads
            if (rootNode["14"]?["inputs"] is JsonObject scaleInputs)
            {
                scaleInputs["width"] = width;
                scaleInputs["height"] = height;
            }

            // 2b. Dynamic Time Shifting -> Node "11" (ModelSamplingAuraFlow)
            if (rootNode["11"]?["inputs"] is JsonObject samplingInputs)
            {
                samplingInputs["shift"] = ComputeDynamicShift(width, height);
            }

            // 3. Seed & Denoise -> Node "3" (KSampler)
            if (rootNode["3"]?["inputs"] is JsonObject samplerInputs)
            {
                samplerInputs["seed"] = request.Seed;
                samplerInputs["denoise"] = Math.Clamp(request.Denoise ?? 0.6, 0.05, 1.0);
            }
        }
        else
        {
            // 2. Width, Height -> Node "13" (EmptySD3LatentImage)
            var width = request.Width > 0 ? request.Width : 512;
            var height = request.Height > 0 ? request.Height : 512;
            if (rootNode["13"]?["inputs"] is JsonObject latentInputs)
            {
                latentInputs["width"] = width;
                latentInputs["height"] = height;
            }

            // 2b. Dynamic Time Shifting (Z-Image Paper Sec. 4.3) -> Node "11" (ModelSamplingAuraFlow)
            if (rootNode["11"]?["inputs"] is JsonObject samplingInputs)
            {
                samplingInputs["shift"] = ComputeDynamicShift(width, height);
            }

            // 3. Seed -> Node "3" (KSampler)
            if (rootNode["3"]?["inputs"] is JsonObject samplerInputs)
            {
                samplerInputs["seed"] = request.Seed;
                samplerInputs["denoise"] = 1.0;
            }
        }
    }

    // Resolution-aware Flow Matching timestep shift (Z-Image Sec. 4.3):
    // calibrated so 512x512 -> 2.10, 512x768 -> 2.36, 1024x1024 -> 3.00, 1024x1536 -> 3.26.
    public static double ComputeDynamicShift(int width, int height)
    {
        var pixels = Math.Max((double)width * height, 262_144.0);
        var shift = 1.65 + 1.35 * Math.Log2(pixels / 131_072.0) / 3.0;
        return Math.Round(Math.Clamp(shift, 1.8, 3.4), 2);
    }

    private async Task<string> LoadTemplateJsonAsync(bool isImg2Img, CancellationToken ct)
    {
        var targetPath = isImg2Img ? _templatePathI2I : _templatePathT2I;
        if (WorkflowTemplateCache.TryGetValue(targetPath, out var cached))
            return cached;

        if (File.Exists(targetPath))
        {
            var text = await File.ReadAllTextAsync(targetPath, ct);
            WorkflowTemplateCache[targetPath] = text;
            return text;
        }

        var altFileName = isImg2Img ? "z-image-turbo-int8-i2i.api.json" : "z-image-turbo-int8-t2i.api.json";
        var altPath = Path.Combine(Directory.GetCurrentDirectory(), "AI", "WorkflowTemplates", altFileName);
        if (WorkflowTemplateCache.TryGetValue(altPath, out var altCached))
            return altCached;

        if (File.Exists(altPath))
        {
            var text = await File.ReadAllTextAsync(altPath, ct);
            WorkflowTemplateCache[altPath] = text;
            return text;
        }

        if (isImg2Img)
        {
            return """
            {
              "28": { "class_type": "UNETLoader", "inputs": { "unet_name": "z_image_turbo_int8_convrot.safetensors", "weight_dtype": "default" } },
              "30": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_3_4b_fp8_mixed.safetensors", "type": "lumina2", "device": "default" } },
              "29": { "class_type": "VAELoader", "inputs": { "vae_name": "ae.safetensors" } },
              "11": { "class_type": "ModelSamplingAuraFlow", "inputs": { "model": [ "28", 0 ], "shift": 3.0 } },
              "27": { "class_type": "CLIPTextEncode", "inputs": { "text": "__PROMPT__", "clip": [ "30", 0 ] } },
              "33": { "class_type": "ConditioningZeroOut", "inputs": { "conditioning": [ "27", 0 ] } },
              "10": { "class_type": "LoadImage", "inputs": { "image": "__REFERENCE_IMAGE__" } },
              "14": { "class_type": "ImageScale", "inputs": { "image": [ "10", 0 ], "upscale_method": "lanczos", "width": 1024, "height": 1024, "crop": "center" } },
              "12": { "class_type": "VAEEncode", "inputs": { "pixels": [ "14", 0 ], "vae": [ "29", 0 ] } },
              "3": { "class_type": "KSampler", "inputs": { "model": [ "11", 0 ], "positive": [ "27", 0 ], "negative": [ "33", 0 ], "latent_image": [ "12", 0 ], "seed": 123456, "steps": 9, "cfg": 1.0, "sampler_name": "res_multistep", "scheduler": "simple", "denoise": 0.6 } },
              "8": { "class_type": "VAEDecode", "inputs": { "samples": [ "3", 0 ], "vae": [ "29", 0 ] } },
              "9": { "class_type": "PreviewImage", "inputs": { "images": [ "8", 0 ] } }
            }
            """;
        }

        // Hardcoded verified fallback template
        return """
        {
          "28": { "class_type": "UNETLoader", "inputs": { "unet_name": "z_image_turbo_int8_convrot.safetensors", "weight_dtype": "default" } },
          "30": { "class_type": "CLIPLoader", "inputs": { "clip_name": "qwen_3_4b_fp8_mixed.safetensors", "type": "lumina2", "device": "default" } },
          "29": { "class_type": "VAELoader", "inputs": { "vae_name": "ae.safetensors" } },
          "11": { "class_type": "ModelSamplingAuraFlow", "inputs": { "model": [ "28", 0 ], "shift": 3 } },
          "27": { "class_type": "CLIPTextEncode", "inputs": { "text": "__PROMPT__", "clip": [ "30", 0 ] } },
          "33": { "class_type": "ConditioningZeroOut", "inputs": { "conditioning": [ "27", 0 ] } },
          "13": { "class_type": "EmptySD3LatentImage", "inputs": { "width": 512, "height": 512, "batch_size": 1 } },
          "3": { "class_type": "KSampler", "inputs": { "model": [ "11", 0 ], "positive": [ "27", 0 ], "negative": [ "33", 0 ], "latent_image": [ "13", 0 ], "seed": 123456, "steps": 9, "cfg": 1.0, "sampler_name": "res_multistep", "scheduler": "simple", "denoise": 1.0 } },
          "8": { "class_type": "VAEDecode", "inputs": { "samples": [ "3", 0 ], "vae": [ "29", 0 ] } },
          "9": { "class_type": "PreviewImage", "inputs": { "images": [ "8", 0 ] } }
        }
        """;
    }

    private static (int width, int height) ParsePngDimensions(byte[] pngBytes, int fallbackW, int fallbackH)
    {
        if (pngBytes.Length >= 24 &&
            pngBytes[0] == 0x89 && pngBytes[1] == 0x50 && pngBytes[2] == 0x4E && pngBytes[3] == 0x47 &&
            pngBytes[4] == 0x0D && pngBytes[5] == 0x0A && pngBytes[6] == 0x1A && pngBytes[7] == 0x0A)
        {
            int w = (pngBytes[16] << 24) | (pngBytes[17] << 16) | (pngBytes[18] << 8) | pngBytes[19];
            int h = (pngBytes[20] << 24) | (pngBytes[21] << 16) | (pngBytes[22] << 8) | pngBytes[23];
            return (w, h);
        }

        return (fallbackW, fallbackH);
    }
}
