using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PromptAdvisor;

/// <summary>Ollama 로컬 HTTP 연동. 미설치/미실행이면 IsAvailable=false로 조용히 비활성화.</summary>
public sealed class Ollama
{
    // 기본 생성 모델 bonsai-2.5:27b 는 RTX 3050 4GB 에서 생성 1.8 토큰/초, 처음 불러오기 1~2분이라 넉넉히 둔다
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(20) };
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string GenModel { get; set; } = DefaultGenModel;
    public string EmbedModel { get; set; } = "bge-m3";

    /// <summary>PrismML Ternary Bonsai 2.5 27B. Ollama 라이브러리에 없어 GGUF + Modelfile 로 만든다 (README 의 Ollama 절).</summary>
    public const string DefaultGenModel = "bonsai-2.5:27b";

    /// <summary>모델을 설치하는 명령 안내. bonsai-2.5 는 pull 로 받을 수 없어 README 를 가리킨다.</summary>
    public static string InstallHint(string model) => model.StartsWith("bonsai-2.5", StringComparison.OrdinalIgnoreCase)
        ? L.T($"README 의 'Bonsai 2.5 만들기' 대로 ollama create {model}", $"ollama create {model} as in README 'Bonsai 2.5'")
        : "ollama pull " + model;

    public async Task<bool> IsAvailable()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 첫 응답이 2초를 넘는 경우가 있어 5초로 둔다 (꺼진 경우엔 연결 거부로 즉시 실패)
            using var res = await _http.GetAsync($"{BaseUrl}/api/tags", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>꺼져 있으면 설치된 ollama를 직접 띄우고 최대 ~12초 대기. (앱만 켜고 서버를 안 켠 상태 대응)</summary>
    public async Task<bool> EnsureAvailable()
    {
        if (await IsAvailable()) return true;
        string? exe = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
        }.FirstOrDefault(File.Exists);
        if (exe == null) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "serve")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { return false; }
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(1000);
            if (await IsAvailable()) return true;
        }
        return false;
    }

    /// <summary>설치된 모델 이름 목록 (/api/tags). 연결이 안 되면 빈 목록.</summary>
    public async Task<List<string>> InstalledModels()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{BaseUrl}/api/tags", cts.Token));
            return doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").ToList();
        }
        catch { return new(); }
    }

    /// <summary>모델 이름이 설치 목록에 있는지 (태그 없이 쓰면 :latest 로 본다).</summary>
    public static bool Has(List<string> installed, string model)
        => installed.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase) || n.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase));

    public async Task<float[]?> Embed(string text)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { model = EmbedModel, input = text.Length > 3000 ? text[..3000] : text });
            using var res = await _http.PostAsync($"{BaseUrl}/api/embed",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var arr = doc.RootElement.GetProperty("embeddings")[0];
            var vec = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var v in arr.EnumerateArray()) vec[i++] = v.GetSingle();
            return vec;
        }
        catch { return null; }
    }

    /// <summary>/api/chat 스트리밍. 토큰 조각마다 onToken 호출.</summary>
    public async Task ChatStream(string system, string user, Action<string> onToken, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = GenModel,
            stream = true,
            think = false, // qwen3 등 thinking 모델의 장황한 사고 과정 생략 → 속도·출력 품질
            options = new { num_predict = 1200, temperature = 0.3 },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/chat")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("message", out var m)
                    && m.TryGetProperty("content", out var c))
                    onToken(c.GetString() ?? "");
            }
            catch { /* 부분 라인 무시 */ }
        }
    }
}
