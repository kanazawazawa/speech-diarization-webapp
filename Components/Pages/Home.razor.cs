using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Speech2Text.Models;
using Speech2Text.Services;

namespace Speech2Text.Components.Pages;

public partial class Home : IDisposable
{
    [Inject] private SpeechRecognitionService SpeechService { get; set; } = default!;
    [Inject] private SummarizationService SummarizationService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    // リアルタイム文字起こしで話者を表示するかどうか（falseで非表示）
    private const bool ShowRealtimeSpeaker = false;

    private static readonly string[] SpeakerBadgeColors =
        ["badge bg-secondary", "badge bg-primary", "badge bg-success", "badge bg-info", "badge bg-warning", "badge bg-danger"];

    private static readonly string[] SpeakerBgClasses =
        ["bg-light", "bg-primary bg-opacity-10", "bg-success bg-opacity-10", "bg-info bg-opacity-10", "bg-warning bg-opacity-10", "bg-danger bg-opacity-10"];

    private bool isRecording;
    private bool isProcessing;
    private bool showPrivacyNotice = true;
    private bool isSummarizing;
    private List<TranscriptionItem> realtimeTranscriptions = new();
    private List<TranscriptionItem> diarizedTranscriptions = new();
    private DotNetObjectReference<Home>? objRef;
    private string? currentRecordingPath;
    private string currentNote = string.Empty;
    private string summaryText = string.Empty;
    private string? summaryError;
    private string? lastProcessedAudioPath;
    private Timer? fileCleanupTimer;

    // 話者IDからUI番号へのマッピング（リアルタイム用 / 話者分離用）
    private readonly SpeakerMapper realtimeSpeakerMapper = new();
    private readonly SpeakerMapper diarizedSpeakerMapper = new();

    protected override void OnInitialized()
    {
        SpeechService.OnTranscriptionReceived += HandleTranscription;
        SpeechService.OnTranscribing += HandleTranscribing;

        // AI要約サービスの初期化をバックグラウンドで開始
        _ = Task.Run(async () =>
        {
            try { await SummarizationService.SummarizeTranscriptionAsync("", "", null); }
            catch { /* 初期化エラーは無視 */ }
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            objRef = DotNetObjectReference.Create(this);
        }
    }

    private void AcceptPrivacyNotice() => showPrivacyNotice = false;
    private void ShowPrivacyNotice() => showPrivacyNotice = true;

    private async Task ToggleRecording()
    {
        isRecording = !isRecording;

        if (isRecording)
        {
            realtimeTranscriptions.Clear();
            diarizedTranscriptions.Clear();
            realtimeSpeakerMapper.Reset();
            diarizedSpeakerMapper.Reset();
            currentRecordingPath = await JS.InvokeAsync<string>("startRecording", objRef);
            await SpeechService.StartRecognitionAsync();
        }
        else
        {
            await JS.InvokeVoidAsync("stopRecording");
            await SpeechService.StopRecognitionAsync();

            var savedFilePath = await SpeechService.SaveRecordingToFileAsync();
            if (!string.IsNullOrEmpty(savedFilePath))
            {
                await ProcessDiarization(savedFilePath);
            }
        }

        StateHasChanged();
    }

    private async Task ProcessDiarization(string audioFilePath)
    {
        isProcessing = true;
        lastProcessedAudioPath = audioFilePath;

        // 30分後に自動削除
        fileCleanupTimer?.Dispose();
        fileCleanupTimer = new Timer(_ => DeleteAudioFile(audioFilePath), null, TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);
        StateHasChanged();

        try
        {
            if (!File.Exists(audioFilePath)) return;

            var results = await SpeechService.ProcessAudioFileWithDiarizationAsync(audioFilePath);

            diarizedSpeakerMapper.Reset();
            diarizedTranscriptions.Clear();
            var baseTime = DateTime.Now;

            foreach (var (text, speakerId, offset) in results)
            {
                diarizedSpeakerMapper.GetOrAssign(speakerId);
                diarizedTranscriptions.Add(new TranscriptionItem
                {
                    Text = text,
                    SpeakerId = speakerId,
                    Timestamp = baseTime.Add(offset)
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Diarization error: {ex.Message}");
        }
        finally
        {
            isProcessing = false;
            StateHasChanged();
        }
    }

    private async Task RetryDiarization()
    {
        if (!string.IsNullOrEmpty(lastProcessedAudioPath))
            await ProcessDiarization(lastProcessedAudioPath);
    }

    [JSInvokable]
    public async Task ReceiveAudioData(int[] audioData)
    {
        if (audioData is { Length: > 0 })
        {
            var byteArray = new byte[audioData.Length];
            for (int i = 0; i < audioData.Length; i++)
                byteArray[i] = (byte)audioData[i];

            await SpeechService.ProcessAudioDataAsync(byteArray);
        }
    }

    // --- 文字起こしイベントハンドラ ---

    private void HandleTranscribing(string text, string speakerId)
    {
        // 最後の未確定アイテムを更新
        for (int i = realtimeTranscriptions.Count - 1; i >= 0; i--)
        {
            var item = realtimeTranscriptions[i];
            if (!item.IsFinalized && !item.IsNote)
            {
                item.Text = text;
                item.Timestamp = DateTime.Now;
                InvokeAsync(StateHasChanged);
                return;
            }
        }

        realtimeTranscriptions.Add(new TranscriptionItem
        {
            Text = text,
            SpeakerId = "Unknown",
            Timestamp = DateTime.Now,
            IsFinalized = false
        });
        InvokeAsync(StateHasChanged);
    }

    private void HandleTranscription(string text, string speakerId)
    {
        for (int i = realtimeTranscriptions.Count - 1; i >= 0; i--)
        {
            var item = realtimeTranscriptions[i];
            if (!item.IsFinalized && !item.IsNote)
            {
                item.Text = text;
                item.SpeakerId = speakerId;
                item.IsFinalized = true;
                InvokeAsync(StateHasChanged);
                return;
            }
        }

        realtimeTranscriptions.Add(new TranscriptionItem
        {
            Text = text,
            SpeakerId = "Unknown",
            Timestamp = DateTime.Now,
            IsFinalized = true
        });
        InvokeAsync(StateHasChanged);
    }

    // --- 速記メモ ---

    private async Task HandleNoteKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(currentNote))
        {
            AddNote();
            await Task.CompletedTask;
        }
    }

    private void AddNote()
    {
        if (string.IsNullOrWhiteSpace(currentNote)) return;

        realtimeTranscriptions.Add(new TranscriptionItem
        {
            Text = currentNote.Trim(),
            Timestamp = DateTime.Now,
            IsNote = true,
            IsFinalized = true
        });
        currentNote = string.Empty;
        StateHasChanged();
    }

    // --- 話者表示ヘルパー（共通マッパーで統一） ---

    private string GetSpeakerName(string speakerId)
        => FormatSpeakerName(speakerId, realtimeSpeakerMapper);

    private string GetSpeakerColor(string speakerId)
        => GetBadgeColor(realtimeSpeakerMapper.GetOrAssign(speakerId));

    private string GetDiarizedSpeakerName(string speakerId)
        => FormatSpeakerName(speakerId, diarizedSpeakerMapper);

    private string GetDiarizedSpeakerColor(string speakerId)
        => GetBadgeColor(diarizedSpeakerMapper.GetOrAssign(speakerId));

    private string GetDiarizedSpeakerClass(string speakerId)
        => GetBgClass(diarizedSpeakerMapper.GetOrAssign(speakerId));

    private static string FormatSpeakerName(string speakerId, SpeakerMapper mapper)
    {
        if (string.IsNullOrEmpty(speakerId) || speakerId == "Unknown")
            return "識別中...";
        return $"話者{mapper.GetOrAssign(speakerId)}";
    }

    private static string GetBadgeColor(int number)
        => SpeakerBadgeColors[Math.Min(number, SpeakerBadgeColors.Length - 1)];

    private static string GetBgClass(int number)
        => SpeakerBgClasses[Math.Min(number, SpeakerBgClasses.Length - 1)];

    // --- AI要約 ---

    private async Task GenerateSummary()
    {
        isSummarizing = true;
        summaryError = null;
        summaryText = string.Empty;
        StateHasChanged();

        try
        {
            var realtimeText = string.Join("\n",
                realtimeTranscriptions.OrderBy(t => t.Timestamp).Select(t =>
                    t.IsNote
                        ? $"[{t.Timestamp:HH:mm:ss}] 【速記メモ】{t.Text}"
                        : $"[{t.Timestamp:HH:mm:ss}] {GetSpeakerName(t.SpeakerId)}: {t.Text}"));

            var diarizedText = string.Join("\n",
                diarizedTranscriptions.OrderBy(t => t.Timestamp).Select(t =>
                    $"[{t.Timestamp:HH:mm:ss}] {GetDiarizedSpeakerName(t.SpeakerId)}: {t.Text}"));

            await SummarizationService.SummarizeTranscriptionAsync(realtimeText, diarizedText, partial =>
            {
                summaryText = partial;
                InvokeAsync(StateHasChanged);
            });
        }
        catch (Exception ex)
        {
            summaryError = $"要約生成に失敗しました: {ex.Message}";
        }
        finally
        {
            isSummarizing = false;
            StateHasChanged();
        }
    }

    // --- クリップボード ---

    private async Task CopyRealtimeTranscription()
    {
        var text = string.Join("\n",
            realtimeTranscriptions.OrderBy(t => t.Timestamp).Select(t =>
            {
                if (t.IsNote) return $"[{t.Timestamp:HH:mm:ss}] 【速記メモ】{t.Text}";
                return ShowRealtimeSpeaker
                    ? $"[{t.Timestamp:HH:mm:ss}] {GetSpeakerName(t.SpeakerId)}: {t.Text}"
                    : $"[{t.Timestamp:HH:mm:ss}] {t.Text}";
            }));
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    private async Task CopyDiarizedTranscription()
    {
        var text = string.Join("\n",
            diarizedTranscriptions.OrderBy(t => t.Timestamp).Select(t =>
                $"[{t.Timestamp:HH:mm:ss}] {GetDiarizedSpeakerName(t.SpeakerId)}: {t.Text}"));
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    private async Task CopySummary()
        => await JS.InvokeVoidAsync("navigator.clipboard.writeText", summaryText);

    // --- クリーンアップ ---

    public void Dispose()
    {
        SpeechService.OnTranscriptionReceived -= HandleTranscription;
        SpeechService.OnTranscribing -= HandleTranscribing;
        objRef?.Dispose();
        fileCleanupTimer?.Dispose();

        if (!string.IsNullOrEmpty(lastProcessedAudioPath))
            DeleteAudioFile(lastProcessedAudioPath);
    }

    private static void DeleteAudioFile(string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// 話者IDを連番にマッピングするヘルパー
    /// </summary>
    private class SpeakerMapper
    {
        private readonly Dictionary<string, int> _map = new();
        private int _next = 1;

        public int GetOrAssign(string speakerId)
        {
            if (string.IsNullOrEmpty(speakerId) || speakerId == "Unknown")
                return 0;
            if (!_map.TryGetValue(speakerId, out var number))
            {
                number = _next++;
                _map[speakerId] = number;
            }
            return number;
        }

        public void Reset()
        {
            _map.Clear();
            _next = 1;
        }
    }
}
