using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
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

// Basic認証ミドルウェア（デモ用簡易認証。本番環境では Microsoft Entra ID / Easy Auth 等を使用すること）
var basicAuthUser = app.Configuration["BasicAuth:Username"] ?? "";
var basicAuthPass = app.Configuration["BasicAuth:Password"] ?? "";
if (!string.IsNullOrEmpty(basicAuthUser))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            try
            {
                var auth = AuthenticationHeaderValue.Parse(authHeader!);
                if (auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) && auth.Parameter is not null)
                {
                    var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter)).Split(':', 2);
                    if (credentials.Length == 2 &&
                        credentials[0] == basicAuthUser &&
                        credentials[1] == basicAuthPass)
                    {
                        await next();
                        return;
                    }
                }
            }
            catch { }
        }

        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Speech2Text\"";
    });
}

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

