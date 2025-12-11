using Speech2Text.Components;
using Speech2Text.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Enable detailed Blazor errors for debugging
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options => 
    {
        options.DetailedErrors = true;
    });

// Add Speech Service
builder.Services.AddSingleton<SpeechRecognitionService>();

// Add Summarization Service
builder.Services.AddSingleton<SummarizationService>();

var app = builder.Build();

// 起動時に古い音声ファイルをクリーンアップ
CleanupOldAudioFiles();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

void CleanupOldAudioFiles()
{
    try
    {
        var tempPath = Path.GetTempPath();
        var oldFiles = Directory.GetFiles(tempPath, "recording_*.wav")
            .Where(f => File.GetCreationTime(f) < DateTime.Now.AddHours(-1));
        
        foreach (var file in oldFiles)
        {
            try
            {
                File.Delete(file);
                Console.WriteLine($"古い音声ファイルを削除しました: {file}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ファイル削除エラー: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"クリーンアップエラー: {ex.Message}");
    }
}

