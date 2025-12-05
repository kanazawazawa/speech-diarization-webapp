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
    private ConversationTranscriber? _conversationTranscriber;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private readonly ConcurrentQueue<byte[]> _audioBuffer = new();
    private bool _isRecognizing = false;

    public event Action<string, string>? OnTranscriptionReceived;
    public event Action<string, string>? OnTranscribing;

    public SpeechRecognitionService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task StartRecognitionAsync()
    {
        if (_isRecognizing) return;

        // 録音バッファをクリア
        while (_audioBuffer.TryDequeue(out _)) { }

        var speechKey = _configuration["AzureSpeech:Key"];
        var endpoint = _configuration["AzureSpeech:Endpoint"];
        var language = _configuration["Recognition:Language"] ?? "ja-JP";

        if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("Speech configuration is missing");
        }

        var speechConfig = SpeechConfig.FromEndpoint(new Uri(endpoint), speechKey);
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
                Console.WriteLine($"[Transcribing] Speaker: {e.Result.SpeakerId}, Text: {e.Result.Text}");
            }
        };

        // Transcribed: 確定した結果
        _conversationTranscriber.Transcribed += (s, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                OnTranscriptionReceived?.Invoke(e.Result.Text, e.Result.SpeakerId ?? "Unknown");
                Console.WriteLine($"[Transcribed] Speaker: {e.Result.SpeakerId}, Text: {e.Result.Text}");
            }
        };

        _conversationTranscriber.Canceled += (s, e) =>
        {
            Console.WriteLine($"Recognition canceled: {e.Reason}");
            if (e.Reason == CancellationReason.Error)
            {
                Console.WriteLine($"Error: {e.ErrorDetails}");
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
            Console.WriteLine("No audio data in buffer");
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

            Console.WriteLine($"Total audio data: {allAudioData.Count} bytes");

            // WAVファイルとして保存
            var tempDir = Path.GetTempPath();
            var fileName = $"recording_{DateTime.Now:yyyyMMddHHmmss}.wav";
            var filePath = Path.Combine(tempDir, fileName);

            await CreateWavFileAsync(filePath, allAudioData.ToArray(), 16000, 1, 16);
            Console.WriteLine($"Recording saved to: {filePath}");

            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving recording: {ex.Message}");
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
                // バッファに音声データを保存（話者分離用）
                _audioBuffer.Enqueue(audioData);
                Console.WriteLine($"Audio data received: {audioData.Length} bytes");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing audio data: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Push stream is null or audio data is empty. Stream null: {_pushStream == null}, Data length: {audioData?.Length ?? 0}");
        }
    }

    public async Task<List<(string Text, string SpeakerId, TimeSpan Offset)>> ProcessAudioFileWithDiarizationAsync(string audioFilePath)
    {
        var results = new List<(string Text, string SpeakerId, TimeSpan Offset)>();
        
        var speechKey = _configuration["AzureSpeech:Key"];
        var endpoint = _configuration["AzureSpeech:Endpoint"];
        var region = _configuration["AzureSpeech:Region"];
        var language = _configuration["Recognition:Language"] ?? "ja-JP";

        if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("Speech configuration is missing");
        }

        try
        {
            Console.WriteLine($"Starting fast transcription with diarization for: {audioFilePath}");
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            
            // カスタムエンドポイントを使用する場合はそのまま、それ以外はリージョンベースのURL
            string transcriptionUrl;
            if (!string.IsNullOrEmpty(region))
            {
                transcriptionUrl = $"https://{region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15";
            }
            else
            {
                // カスタムドメインの場合
                var uri = new Uri(endpoint);
                var baseUrl = $"{uri.Scheme}://{uri.Host}";
                transcriptionUrl = $"{baseUrl}/speechtotext/transcriptions:transcribe?api-version=2024-11-15";
            }
            
            Console.WriteLine($"Using transcription URL: {transcriptionUrl}");
            
            using var multipartContent = new MultipartFormDataContent();
            
            // オーディオファイルを追加
            var audioBytes = await File.ReadAllBytesAsync(audioFilePath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
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
            
            // リクエスト送信
            var request = new HttpRequestMessage(HttpMethod.Post, transcriptionUrl);
            request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
            request.Content = multipartContent;
            
            Console.WriteLine("Sending transcription request...");
            var response = await httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Error response: {errorContent}");
                response.EnsureSuccessStatusCode();
            }
            
            var responseJson = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Transcription completed, parsing results...");
            Console.WriteLine($"Response JSON (first 500 chars): {responseJson.Substring(0, Math.Min(500, responseJson.Length))}");
            
            // 結果を解析
            var result = JsonSerializer.Deserialize<JsonElement>(responseJson);
            
            if (result.TryGetProperty("phrases", out var phrases))
            {
                Console.WriteLine($"Found {phrases.GetArrayLength()} phrases");
                
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
                        speakerId = $"Guest-{speakerNum + 1}"; // 0-based to 1-based
                        Console.WriteLine($"Phrase: Speaker={speakerId}, Text={text.Substring(0, Math.Min(50, text.Length))}...");
                    }
                    else
                    {
                        Console.WriteLine($"Warning: No speaker property found for phrase: {text.Substring(0, Math.Min(50, text.Length))}...");
                        // speaker プロパティがない場合、channel を確認
                        if (phrase.TryGetProperty("channel", out var channelElement))
                        {
                            var channelNum = channelElement.GetInt32();
                            speakerId = $"Guest-{channelNum + 1}";
                            Console.WriteLine($"Using channel instead: {speakerId}");
                        }
                    }
                    
                    results.Add((text, speakerId, offset));
                }
            }
            else
            {
                Console.WriteLine("Warning: No 'phrases' property in response");
            }
            
            Console.WriteLine($"Parsed {results.Count} transcription segments");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fast transcription error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }

        return results;
    }

    private string ExtractRegion(string endpoint)
    {
        // https://your-resource.cognitiveservices.azure.com/ から地域を抽出
        // Speech Serviceのエンドポイントは通常 {region}.api.cognitive.microsoft.com 形式
        // カスタムドメインの場合は、最初のセグメントを使用
        var uri = new Uri(endpoint);
        var host = uri.Host.Split('.')[0];
        
        // 一般的なリージョン名を確認
        var knownRegions = new[] { "eastus", "westus", "westeurope", "southeastasia", "japaneast", "japanwest" };
        foreach (var region in knownRegions)
        {
            if (host.Contains(region, StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }
        }
        
        // カスタムドメインの場合、appsettings.jsonにリージョンを追加する必要がある
        Console.WriteLine($"Warning: Could not extract region from endpoint {endpoint}, using host segment: {host}");
        return host;
    }
}
