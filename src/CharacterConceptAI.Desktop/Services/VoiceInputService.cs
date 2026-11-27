using System.IO;
using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CharacterConceptAI.Desktop.Services;

/// <summary>
/// Local offline Speech-to-Text service powered by NAudio 16kHz PCM mic capture
/// and Whisper.net (Whisper Large-v3 Turbo) with Vulkan GPU acceleration (~0.8s latency)
/// and automatic VRAM release when idle or when ComfyUI starts image generation.
/// </summary>
public sealed class VoiceInputService : IDisposable
{
    private const int SampleRate = 16000;
    private const int MaxRecordingSeconds = 60;
    private static readonly TimeSpan IdleUnloadDelay = TimeSpan.FromSeconds(25);

    private static readonly string[] PreferredModelFileNames =
    [
        "ggml-large-v3-turbo-q5_0.bin",
        "ggml-small-q5_1.bin",
        "ggml-small.bin",
        "ggml-medium-q5_0.bin"
    ];

    private static readonly string[] HallucinationPhrases =
    [
        "[BLANK_AUDIO]",
        "(BLANK_AUDIO)",
        "[Music]",
        "(Music)",
        "(nhạc)",
        "[nhạc]",
        "Hãy subscribe cho kênh Ghiền Mì Gõ Để không bỏ lỡ những video hấp dẫn",
        "Cảm ơn các bạn đã theo dõi và hẹn gặp lại",
        "Cảm ơn các bạn đã xem video",
        "Đăng ký kênh để ủng hộ"
    ];

    private static int _runtimeConfigured;
    private static readonly object ModelResolveLock = new();
    private static string? _cachedModelPath;
    private static string? _cachedModelLabel;
    private static bool _modelPathResolved;

    private readonly object _recordLock = new();
    private readonly object _idleLock = new();
    private readonly SemaphoreSlim _factoryLock = new(1, 1);

    private WaveIn? _waveIn;
    private MemoryStream? _pcmStream;
    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    private int _loadedGpuDevice = int.MinValue;
    private CancellationTokenSource? _idleUnloadCts;
    private volatile bool _releaseRequested;
    private volatile bool _disposed;

    public bool IsRecording { get; private set; }
    public string? ActiveModelPath => ResolveModelPath();

    public event Action<double>? AudioLevelChanged;
    public event Action? MaxDurationReached;

    public static string? ResolveModelPath()
    {
        lock (ModelResolveLock)
        {
            if (_modelPathResolved && (_cachedModelPath is null || File.Exists(_cachedModelPath)))
                return _cachedModelPath;

            var resolved = ResolveModelPathCore();
            _cachedModelPath = resolved;
            _cachedModelLabel = null;
            _modelPathResolved = true;
            return resolved;
        }
    }

    public static void InvalidateModelCache()
    {
        lock (ModelResolveLock)
        {
            _cachedModelPath = null;
            _cachedModelLabel = null;
            _modelPathResolved = false;
        }
    }

    private static string? ResolveModelPathCore()
    {
        var envPath = Environment.GetEnvironmentVariable("DREAMINA_STT_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var workspaceDir = GulaWorkspaceLocator.GetWorkspaceRoot();
        var modelDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(workspaceDir, "SpeechToText", "models"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SpeechToText", "models")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SpeechToText", "models"))
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in modelDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var fileName in PreferredModelFileNames)
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            var anyBin = Directory.GetFiles(dir, "ggml-*.bin").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (anyBin != null) return anyBin;
        }

        return null;
    }

    public static string DescribeActiveModel()
    {
        lock (ModelResolveLock)
        {
            if (_cachedModelLabel != null) return _cachedModelLabel;
            var path = ResolveModelPath();
            if (string.IsNullOrWhiteSpace(path))
                return _cachedModelLabel = "Chưa tìm thấy model Whisper";
            var name = Path.GetFileNameWithoutExtension(path);
            return _cachedModelLabel = name.Contains("large-v3-turbo", StringComparison.OrdinalIgnoreCase)
                ? "Whisper Large-v3 Turbo"
                : name.Contains("small", StringComparison.OrdinalIgnoreCase)
                    ? "Whisper Small"
                    : name;
        }
    }

    public void StartRecording()
    {
        lock (_recordLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording) return;

            if (WaveIn.DeviceCount <= 0)
                throw new InvalidOperationException("Không tìm thấy micro trên máy tính. Hãy kết nối micro và thử lại.");

            _pcmStream?.Dispose();
            _pcmStream = new MemoryStream(SampleRate * 2 * 10);

            var waveIn = new WaveIn
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 80
            };

            var maxBytes = SampleRate * 2 * MaxRecordingSeconds;
            waveIn.DataAvailable += (_, args) =>
            {
                double rms;
                var hitLimit = false;
                lock (_recordLock)
                {
                    if (!IsRecording || _pcmStream == null) return;
                    var toWrite = Math.Min(args.BytesRecorded, Math.Max(0, maxBytes - (int)_pcmStream.Length));
                    if (toWrite > 0)
                        _pcmStream.Write(args.Buffer, 0, toWrite);
                    if (_pcmStream.Length >= maxBytes)
                        hitLimit = true;
                    rms = ComputeRms(args.Buffer, args.BytesRecorded);
                }
                AudioLevelChanged?.Invoke(Math.Clamp(rms * 4.5, 0.0, 1.0));
                if (hitLimit)
                    MaxDurationReached?.Invoke();
            };

            _waveIn = waveIn;
            IsRecording = true;
            try
            {
                waveIn.StartRecording();
            }
            catch
            {
                IsRecording = false;
                _waveIn = null;
                waveIn.Dispose();
                _pcmStream.Dispose();
                _pcmStream = null;
                throw;
            }
        }
    }

    public byte[] StopRecording()
    {
        WaveIn? waveIn;
        lock (_recordLock)
        {
            if (!IsRecording) return [];
            waveIn = _waveIn;
            _waveIn = null;
        }

        // Stop device BEFORE snapshotting _pcmStream so final buffered audio tail (~80-160ms) is flushed!
        if (waveIn != null)
        {
            try { waveIn.StopRecording(); } catch { }
            try { waveIn.Dispose(); } catch { }
        }

        byte[] pcmBytes;
        lock (_recordLock)
        {
            IsRecording = false;
            pcmBytes = _pcmStream?.ToArray() ?? [];
            _pcmStream?.Dispose();
            _pcmStream = null;
        }
        AudioLevelChanged?.Invoke(0);
        return pcmBytes;
    }

    public void CancelRecording() => _ = StopRecording();

    /// <summary>
    /// Pre-loads Whisper weights in the background while the user is speaking so post-speech transcription is near-instant.
    /// </summary>
    public async Task WarmupModelAsync(bool preferSecondaryGpu = false, CancellationToken ct = default)
    {
        if (_disposed || _releaseRequested) return;
        var modelPath = ResolveModelPath();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return;

        CancelIdleUnload();
        if (!await _factoryLock.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if (_disposed || _releaseRequested) return;
            EnsureFactoryLocked(modelPath, preferSecondaryGpu);
            if (_releaseRequested || _disposed)
            {
                DisposeFactoryLocked();
            }
            else
            {
                ScheduleIdleUnload();
            }
        }
        catch { }
        finally
        {
            _factoryLock.Release();
        }
    }

    public async Task<string> TranscribePcm16Async(byte[] pcmBytes, bool preferSecondaryGpu = false, CancellationToken ct = default)
    {
        if (pcmBytes.Length < SampleRate * 2 / 4) // < 0.25s audio
            return string.Empty;

        var samples = ConvertAndTrimPcm16(pcmBytes, out var peakAmplitude);
        if (samples.Length < SampleRate / 5 || peakAmplitude < 0.006f)
            return string.Empty;

        var modelPath = ResolveModelPath();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("Không tìm thấy mô hình nhận diện giọng nói Whisper trên máy.");

        _releaseRequested = false;
        CancelIdleUnload();

        await _factoryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return string.Empty;
            var factory = EnsureFactoryLocked(modelPath, preferSecondaryGpu);
            var audioSeconds = samples.Length / (double)SampleRate;
            var audioCtx = Math.Clamp((int)Math.Ceiling(audioSeconds * 50.0) + 96, 256, 1500);
            var threadCount = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);

            using var processor = factory.CreateBuilder()
                .WithLanguage("vi")
                .WithThreads(threadCount)
                .WithNoContext()
                .WithAudioContextSize(audioCtx)
                .WithPrompt("Mô tả ý tưởng tạo ảnh AI bằng tiếng Việt và tiếng Anh: cô gái, chàng trai, chân dung điện ảnh, anime, cyberpunk, 3D Pixar, áo dài, vest, trench coat, ánh sáng studio.")
                .Build();

            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                var chunk = segment.Text?.Trim();
                if (string.IsNullOrWhiteSpace(chunk)) continue;
                if (IsHallucination(chunk)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(chunk);
            }

            return CleanupTranscript(sb.ToString());
        }
        finally
        {
            if (_disposed || _releaseRequested)
            {
                // Guaranteed immediate VRAM release if ComfyUI started while Whisper was finishing!
                _releaseRequested = false;
                DisposeFactoryLocked();
            }
            else
            {
                ScheduleIdleUnload();
            }
            _factoryLock.Release();
        }
    }

    /// <summary>
    /// Immediately frees Whisper Vulkan VRAM / RAM (or queues immediate release right after an in-flight transcription).
    /// </summary>
    public void ReleaseModelMemory()
    {
        _releaseRequested = true;
        CancelIdleUnload();
        if (!_factoryLock.Wait(0)) return;
        try
        {
            _releaseRequested = false;
            DisposeFactoryLocked();
        }
        finally
        {
            _factoryLock.Release();
        }
    }

    private void DisposeFactoryLocked()
    {
        try { _factory?.Dispose(); } catch { }
        _factory = null;
        _loadedModelPath = null;
        _loadedGpuDevice = int.MinValue;
    }

    private WhisperFactory EnsureFactoryLocked(string modelPath, bool preferSecondaryGpu)
    {
        var preferredDevice = preferSecondaryGpu ? 1 : 0;
        if (_factory != null &&
            string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            (_loadedGpuDevice == preferredDevice || _loadedGpuDevice == -1))
            return _factory;

        if (Interlocked.Exchange(ref _runtimeConfigured, 1) == 0)
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

        DisposeFactoryLocked();

        var deviceOrder = preferSecondaryGpu ? new[] { 1, 0 } : new[] { 0, 1 };
        foreach (var gpuIndex in deviceOrder)
        {
            try
            {
                _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
                {
                    UseGpu = true,
                    UseFlashAttention = true,
                    GpuDevice = gpuIndex
                });
                _loadedModelPath = modelPath;
                _loadedGpuDevice = gpuIndex;
                return _factory;
            }
            catch
            {
                // Try next Vulkan device or fall back to CPU below.
            }
        }

        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
        _loadedModelPath = modelPath;
        _loadedGpuDevice = -1;
        return _factory;
    }

    private void CancelIdleUnload()
    {
        CancellationTokenSource? old;
        lock (_idleLock)
        {
            old = _idleUnloadCts;
            _idleUnloadCts = null;
        }
        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            try { old.Dispose(); } catch { }
        }
    }

    private void ScheduleIdleUnload()
    {
        CancelIdleUnload();
        if (_disposed || _releaseRequested) return;
        var cts = new CancellationTokenSource();
        lock (_idleLock)
        {
            if (_disposed || _releaseRequested)
            {
                cts.Dispose();
                return;
            }
            _idleUnloadCts = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleUnloadDelay, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested && !_disposed && !IsRecording)
                    ReleaseModelMemory();
            }
            catch (OperationCanceledException) { }
        });
    }

    private static float[] ConvertAndTrimPcm16(byte[] pcmBytes, out float peak)
    {
        // Single-allocation PCM scan: finds peak & trim boundaries directly on pcmBytes first
        var count = pcmBytes.Length / 2;
        int peakInt = 0;
        for (var i = 0; i < count; i++)
        {
            short sample = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
            var abs = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
            if (abs > peakInt) peakInt = abs;
        }
        peak = peakInt / 32768f;
        if (peak < 0.006f) return [];

        var thresholdInt = (int)(MathF.Max(0.008f, peak * 0.04f) * 32768f);
        var first = 0;
        while (first < count)
        {
            short s = (short)(pcmBytes[first * 2] | (pcmBytes[first * 2 + 1] << 8));
            if ((s == short.MinValue ? 32768 : Math.Abs((int)s)) >= thresholdInt) break;
            first++;
        }
        var last = count - 1;
        while (last > first)
        {
            short s = (short)(pcmBytes[last * 2] | (pcmBytes[last * 2 + 1] << 8));
            if ((s == short.MinValue ? 32768 : Math.Abs((int)s)) >= thresholdInt) break;
            last--;
        }

        var pad = SampleRate / 5; // 200ms
        var start = Math.Max(0, first - pad);
        var end = Math.Min(count - 1, last + pad);
        var length = Math.Max(0, end - start + 1);
        if (length == 0) return [];

        var gain = peak < 0.25f ? MathF.Min(3.5f, 0.70f / peak) : 1.0f;
        var scale = gain / 32768f;
        var trimmed = new float[length];
        for (var i = 0; i < length; i++)
        {
            var idx = (start + i) * 2;
            short sample = (short)(pcmBytes[idx] | (pcmBytes[idx + 1] << 8));
            trimmed[i] = Math.Clamp(sample * scale, -1f, 1f);
        }
        return trimmed;
    }

    private static double ComputeRms(byte[] buffer, int bytesRecorded)
    {
        var count = bytesRecorded / 2;
        if (count == 0) return 0;
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            short sample = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
            var norm = sample / 32768.0;
            sum += norm * norm;
        }
        return Math.Sqrt(sum / count);
    }

    private static bool IsHallucination(string text)
    {
        var clean = text.Trim();
        if (clean.Length == 0) return true;
        foreach (var phrase in HallucinationPhrases)
        {
            if (clean.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string CleanupTranscript(string text)
    {
        var clean = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        while (clean.Contains("  ", StringComparison.Ordinal))
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        return clean;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _releaseRequested = true;
        CancelIdleUnload();
        CancelRecording();
        // Wait briefly for any active transcription to exit cleanly; do NOT dispose _factoryLock while held
        // (avoids ObjectDisposedException crash on window close while guaranteeing _factory cleanup).
        if (_factoryLock.Wait(250))
        {
            try { DisposeFactoryLocked(); }
            finally { _factoryLock.Release(); }
        }
    }
}
