using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Speech2Text.Services;

public class SpeechRecognitionService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpeechRecognitionService> _logger;
    private ConversationTranscriber? _conversationTranscriber;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private readonly ConcurrentQueue<byte[]> _audioBuffer = new();
    private bool _isRecognizing;

    public event Action<string, string>? OnTranscriptionReceived;
    public event Action<string, string>? OnTranscribing;

    public SpeechRecognitionService(IConfiguration configuration, ILogger<SpeechRecognitionService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartRecognitionAsync()
    {
        if (_isRecognizing) return;

        // 録音バッファをクリア
        while (_audioBuffer.TryDequeue(out _)) { }

        var endpoint = _configuration["AzureSpeech:Endpoint"];
        var resourceId = _configuration["AzureSpeech:ResourceId"];
        var language = _configuration["Recognition:Language"] ?? "ja-JP";

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(resourceId))
        {
            throw new InvalidOperationException("Speech configuration is missing (Endpoint and ResourceId required)");
        }

        // Azure AD認証トークンを取得
        var credential = new DefaultAzureCredential();
        var tokenContext = new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" });
        var accessToken = await credential.GetTokenAsync(tokenContext);
        _logger.LogInformation("Azure AD token acquired for Speech service");

        var speechConfig = SpeechConfig.FromEndpoint(new Uri(endpoint));
        speechConfig.AuthorizationToken = $"aad#{resourceId}#{accessToken.Token}";
        speechConfig.SpeechRecognitionLanguage = language;
        
        // 話者ダイアライゼーションの設定を強化
        speechConfig.SetProperty(PropertyId.SpeechServiceResponse_DiarizeIntermediateResults, "true");
        speechConfig.SetProperty("DiarizationMode", "Identity");
        speechConfig.SetProperty(PropertyId.SpeechServiceConnection_EnableAudioLogging, "true");
        
        // 話者の最小・最大人数を設定（3人想定）
        speechConfig.SetProperty("MinSpeakers", "2");
        speechConfig.SetProperty("MaxSpeakers", "5");

        // Create push stream for audio data from browser
        _pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);

        _conversationTranscriber = new ConversationTranscriber(speechConfig, _audioConfig);

        // Transcribing: リアルタイムの途中経過
        _conversationTranscriber.Transcribing += (s, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizingSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                OnTranscribing?.Invoke(e.Result.Text, e.Result.SpeakerId ?? "Unknown");
                _logger.LogDebug("[Transcribing] Speaker: {SpeakerId}, Text: {Text}", e.Result.SpeakerId, e.Result.Text);
            }
        };

        // Transcribed: 確定した結果
        _conversationTranscriber.Transcribed += (s, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                OnTranscriptionReceived?.Invoke(e.Result.Text, e.Result.SpeakerId ?? "Unknown");
                _logger.LogInformation("[Transcribed] Speaker: {SpeakerId}, Text: {Text}", e.Result.SpeakerId, e.Result.Text);
            }
        };

        _conversationTranscriber.Canceled += (s, e) =>
        {
            _logger.LogWarning("Recognition canceled: {Reason}", e.Reason);
            if (e.Reason == CancellationReason.Error)
            {
                _logger.LogError("Speech recognition error: {ErrorDetails}", e.ErrorDetails);
            }
        };

        await _conversationTranscriber.StartTranscribingAsync();
        _isRecognizing = true;
    }

    public async Task StopRecognitionAsync()
    {
        if (!_isRecognizing) return;

        if (_conversationTranscriber != null)
        {
            await _conversationTranscriber.StopTranscribingAsync();
            _conversationTranscriber.Dispose();
            _conversationTranscriber = null;
        }

        _pushStream?.Close();
        _audioConfig?.Dispose();
        
        _isRecognizing = false;
    }

    public async Task<string?> SaveRecordingToFileAsync()
    {
        if (_audioBuffer.IsEmpty)
        {
            _logger.LogWarning("No audio data in buffer");
            return null;
        }

        try
        {
            // バッファから全音声データを取得
            var allAudioData = new List<byte>();
            while (_audioBuffer.TryDequeue(out var chunk))
            {
                allAudioData.AddRange(chunk);
            }

            _logger.LogInformation("Total audio data: {ByteCount} bytes", allAudioData.Count);

            // WAVファイルとして保存
            var tempDir = Path.GetTempPath();
            var fileName = $"recording_{DateTime.Now:yyyyMMddHHmmss}.wav";
            var filePath = Path.Combine(tempDir, fileName);

            await CreateWavFileAsync(filePath, allAudioData.ToArray(), 16000, 1, 16);
            _logger.LogInformation("Recording saved to: {FilePath}", filePath);

            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving recording");
            return null;
        }
    }

    private async Task CreateWavFileAsync(string filePath, byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fileStream);

        // WAVヘッダーを書き込み
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        writer.Write(new char[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + pcmData.Length);
        writer.Write(new char[] { 'W', 'A', 'V', 'E' });
        writer.Write(new char[] { 'f', 'm', 't', ' ' });
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(new char[] { 'd', 'a', 't', 'a' });
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        await fileStream.FlushAsync();
    }

    public async Task ProcessAudioDataAsync(byte[] audioData)
    {
        if (_pushStream != null && audioData != null && audioData.Length > 0)
        {
            try
            {
                _pushStream.Write(audioData);
                _audioBuffer.Enqueue(audioData);
                _logger.LogDebug("Audio data received: {ByteCount} bytes", audioData.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing audio data");
            }
        }
    }

    public async Task<List<(string Text, string SpeakerId, TimeSpan Offset)>> ProcessAudioFileWithDiarizationAsync(string audioFilePath)
    {
        var results = new List<(string Text, string SpeakerId, TimeSpan Offset)>();
        
        var endpoint = _configuration["AzureSpeech:Endpoint"];
        var language = _configuration["Recognition:Language"] ?? "ja-JP";

        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("Speech configuration is missing");
        }

        // Azure AD認証トークンを取得
        var credential = new DefaultAzureCredential();
        var tokenContext = new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" });
        var accessToken = await credential.GetTokenAsync(tokenContext);
        _logger.LogInformation("Azure AD token acquired for Fast Transcription API");

        try
        {
            _logger.LogInformation("Starting fast transcription with diarization for: {FilePath}", audioFilePath);
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            
            // Azure AD認証ではカスタムドメインエンドポイントを使用する必要がある
            var uri = new Uri(endpoint);
            var baseUrl = $"{uri.Scheme}://{uri.Host}";
            var transcriptionUrl = $"{baseUrl}/speechtotext/transcriptions:transcribe?api-version=2024-11-15";
            
            _logger.LogInformation("Using transcription URL: {Url}", transcriptionUrl);
            
            using var multipartContent = new MultipartFormDataContent();
            
            // オーディオファイルを追加
            var audioBytes = await File.ReadAllBytesAsync(audioFilePath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            multipartContent.Add(audioContent, "audio", Path.GetFileName(audioFilePath));
            
            // 定義を追加（ダイアライゼーション有効化）
            var definition = new
            {
                locales = new[] { language },
                diarization = new
                {
                    enabled = true,
                    minSpeakers = 2,
                    maxSpeakers = 5
                },
                profanityFilterMode = "Masked"
            };
            
            var definitionJson = JsonSerializer.Serialize(definition);
            var definitionContent = new StringContent(definitionJson, System.Text.Encoding.UTF8, "application/json");
            multipartContent.Add(definitionContent, "definition");
            
            // リクエスト送信（Azure AD Bearer トークン認証）
            var request = new HttpRequestMessage(HttpMethod.Post, transcriptionUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Content = multipartContent;
            
            _logger.LogInformation("Sending transcription request...");
            var response = await httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Transcription API error: {ErrorContent}", errorContent);
                response.EnsureSuccessStatusCode();
            }
            
            var responseJson = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Transcription completed, parsing results...");
            _logger.LogDebug("Response JSON (first 500 chars): {Json}", responseJson[..Math.Min(500, responseJson.Length)]);
            
            // 結果を解析
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("phrases", out var phrases))
            {
                _logger.LogInformation("Found {PhraseCount} phrases", phrases.GetArrayLength());
                
                foreach (var phrase in phrases.EnumerateArray())
                {
                    var text = phrase.GetProperty("text").GetString() ?? "";
                    var offsetMs = phrase.GetProperty("offsetMilliseconds").GetInt64();
                    var offset = TimeSpan.FromMilliseconds(offsetMs);
                    
                    // 話者情報を取得
                    var speakerId = "Unknown";
                    if (phrase.TryGetProperty("speaker", out var speakerElement))
                    {
                        var speakerNum = speakerElement.GetInt32();
                        speakerId = $"Guest-{speakerNum + 1}";
                        _logger.LogDebug("Phrase: Speaker={SpeakerId}, Text={Text}", speakerId, text[..Math.Min(50, text.Length)]);
                    }
                    else
                    {
                        _logger.LogWarning("No speaker property found for phrase: {Text}", text[..Math.Min(50, text.Length)]);
                        if (phrase.TryGetProperty("channel", out var channelElement))
                        {
                            var channelNum = channelElement.GetInt32();
                            speakerId = $"Guest-{channelNum + 1}";
                            _logger.LogDebug("Using channel instead: {SpeakerId}", speakerId);
                        }
                    }
                    
                    results.Add((text, speakerId, offset));
                }
            }
            else
            {
                _logger.LogWarning("No 'phrases' property in response");
            }
            
            _logger.LogInformation("Parsed {SegmentCount} transcription segments", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fast transcription error");
            throw;
        }

        return results;
    }
}
