using System.Windows;
using System.Windows.Threading;
using CharacterConceptAI.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public partial class MainViewModel
{
    private VoiceInputService? _voiceService;
    private DispatcherTimer? _voiceTimer;
    private DateTimeOffset _voiceStartedAt;
    private CancellationTokenSource? _voiceTranscribeCts;

    public event Action? PromptFocusRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVoiceStatusBar))]
    [NotifyPropertyChangedFor(nameof(VoiceStatusHeadline))]
    [NotifyPropertyChangedFor(nameof(VoiceButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(IsVoiceIdle))]
    private bool _isVoiceRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVoiceStatusBar))]
    [NotifyPropertyChangedFor(nameof(VoiceStatusHeadline))]
    [NotifyPropertyChangedFor(nameof(VoiceButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(IsVoiceIdle))]
    [NotifyCanExecuteChangedFor(nameof(ToggleVoiceInputCommand))]
    private bool _isVoiceTranscribing;

    [ObservableProperty]
    private double _voiceAudioLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceStatusHeadline))]
    private string _voiceElapsedLabel = "0:00";

    public bool IsVoiceIdle => !IsVoiceRecording && !IsVoiceTranscribing;
    public bool ShowVoiceStatusBar => IsVoiceRecording || IsVoiceTranscribing;
    public string VoiceModelLabel => VoiceInputService.DescribeActiveModel();

    public string VoiceStatusHeadline => IsVoiceTranscribing
        ? $"Đang chuyển giọng nói thành prompt ({VoiceModelLabel})…"
        : $"Đang nghe ({VoiceElapsedLabel}) · Nhấn lại nút sóng âm để dừng";

    public string VoiceButtonToolTip => IsVoiceRecording
        ? "Dừng ghi âm và chuyển thành prompt (nhấn Esc để hủy)"
        : IsVoiceTranscribing
            ? "Đang nhận diện giọng nói…"
            : $"Nhập prompt bằng giọng nói ({VoiceModelLabel})";

    private bool CanToggleVoiceInput => !IsVoiceTranscribing;

    private VoiceInputService EnsureVoiceService()
    {
        if (_voiceService != null) return _voiceService;
        _voiceService = new VoiceInputService();
        _voiceService.AudioLevelChanged += level =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                VoiceAudioLevel = Math.Round(Math.Clamp(level * 100.0, 0.0, 100.0), 1);
            });
        };
        _voiceService.MaxDurationReached += () =>
        {
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (IsVoiceRecording)
                        await StopAndTranscribeVoiceAsync();
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Lỗi nhận diện giọng nói: {ex.Message}";
                }
            });
        };
        return _voiceService;
    }

    [RelayCommand(CanExecute = nameof(CanToggleVoiceInput))]
    private async Task ToggleVoiceInputAsync()
    {
        if (IsVoiceRecording)
        {
            await StopAndTranscribeVoiceAsync();
            return;
        }

        try
        {
            var svc = EnsureVoiceService();
            if (string.IsNullOrWhiteSpace(svc.ActiveModelPath))
            {
                StatusMessage = "Chưa tìm thấy mô hình Whisper STT trên máy.";
                return;
            }

            svc.StartRecording();
            // Warm up Whisper in background while user speaks (when GPU is free) for near-instant transcription!
            if (!IsGenerating)
                _ = Task.Run(() => svc.WarmupModelAsync(preferSecondaryGpu: false));
            _voiceStartedAt = DateTimeOffset.UtcNow;
            VoiceElapsedLabel = "0:00";
            VoiceAudioLevel = 6;
            IsVoiceRecording = true;

            _voiceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
            {
                if (!IsVoiceRecording) return;
                var elapsed = DateTimeOffset.UtcNow - _voiceStartedAt;
                VoiceElapsedLabel = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            }, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
            _voiceTimer.Start();

            StatusMessage = "Đang ghi âm giọng nói… Hãy đọc ý tưởng ảnh của bạn rồi nhấn lại nút sóng âm.";
        }
        catch (Exception ex)
        {
            IsVoiceRecording = false;
            VoiceAudioLevel = 0;
            StatusMessage = $"Không thể mở micro: {ex.Message}";
        }
    }

    private async Task StopAndTranscribeVoiceAsync()
    {
        _voiceTimer?.Stop();
        var pcm = _voiceService?.StopRecording() ?? [];
        IsVoiceRecording = false;
        VoiceAudioLevel = 0;

        if (pcm.Length == 0)
        {
            StatusMessage = "Không ghi nhận được âm thanh nào.";
            return;
        }

        _voiceTranscribeCts?.Cancel();
        _voiceTranscribeCts?.Dispose();
        _voiceTranscribeCts = new CancellationTokenSource();
        var ct = _voiceTranscribeCts.Token;

        IsVoiceTranscribing = true;
        StatusMessage = $"Đang chuyển giọng nói thành chữ bằng {VoiceModelLabel}…";
        try
        {
            var svc = EnsureVoiceService();
            var started = System.Diagnostics.Stopwatch.StartNew();
            var preferSecondary = IsGenerating;
            var transcript = await Task.Run(() => svc.TranscribePcm16Async(pcm, preferSecondaryGpu: preferSecondary, ct), ct);
            started.Stop();

            if (ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(transcript))
            {
                StatusMessage = "Không nghe rõ nội dung — hãy nói gần micro hơn rồi thử lại.";
                return;
            }

            var existing = RawPromptInput?.Trim() ?? string.Empty;
            RawPromptInput = string.IsNullOrWhiteSpace(existing)
                ? transcript
                : $"{existing} {transcript}";
            PromptFocusRequested?.Invoke();
            StatusMessage = $"Đã nhận diện giọng nói ({started.Elapsed.TotalSeconds:0.0}s).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Đã hủy nhận diện giọng nói.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi nhận diện giọng nói: {ex.Message}";
        }
        finally
        {
            IsVoiceTranscribing = false;
        }
    }

    [RelayCommand]
    private void CancelVoiceInput()
    {
        _voiceTimer?.Stop();
        _voiceService?.CancelRecording();
        _voiceTranscribeCts?.Cancel();
        IsVoiceRecording = false;
        IsVoiceTranscribing = false;
        VoiceAudioLevel = 0;
        VoiceElapsedLabel = "0:00";
        StatusMessage = "Đã hủy nhập giọng nói.";
    }

    public void ReleaseVoiceModelMemory()
    {
        try { _voiceService?.ReleaseModelMemory(); } catch { }
    }

    public void DisposeVoiceResources()
    {
        try { _voiceTimer?.Stop(); } catch { }
        try { _voiceTranscribeCts?.Cancel(); _voiceTranscribeCts?.Dispose(); } catch { }
        try { _voiceService?.Dispose(); } catch { }
        _voiceService = null;
        _voiceTranscribeCts = null;
    }
}
