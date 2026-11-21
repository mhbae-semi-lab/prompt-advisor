using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PromptAdvisor;

public sealed class Settings
{
    public string Theme { get; set; } = "System"; // System | Light | Dark
    public string Language { get; set; } = "";     // "" = OS 언어 따름 | ko | en
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string GenModel { get; set; } = "exaone3.5:2.4b";
    public string EmbedModel { get; set; } = "bge-m3";
    public bool AutoImport { get; set; }
    public bool UpdateCheckOnStart { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public string LastWeeklyReport { get; set; } = ""; // 마지막으로 주간 리포트를 보여준 날짜 (yyyy-MM-dd)
    public bool HasAnalyzed { get; set; }
    public string SelectedModel { get; set; } = ""; // 분석 탭에서 직접 지정한 모델 이름 (""=자동 추천)
    public int RecommendCount { get; set; } = 3;    // 추천 순위 개수 1~5
    public List<string> FavoriteModels { get; set; } = new(); // 즐겨찾기 모델 이름들
    public string LastModelMetricCheck { get; set; } = "";    // 모델 지표 마지막 자동 점검일 (yyyy-MM-dd)
    public string KnowledgeDir { get; set; } = "";            // 지식 내보내기 대상 repo 폴더 (""=자동 탐지)
    public string ActiveProjectDir { get; set; } = "";        // 현재 분석 대상 프로젝트 폴더 (""=자동 감지)
    public string ActiveProjectName { get; set; } = "";       // 현재 분석 대상 프로젝트 이름 (""=자동 감지)
    public RuleConfig Rules { get; set; } = new();            // 린트 규칙 설정 (검증 탭에서 비교 → 전환 · 롤백)

    private static string Path_ => System.IO.Path.Combine(Store.DataDir, "settings.json");
    private static string BackupPath => System.IO.Path.Combine(Store.DataDir, "settings-backup.json");

    public static Settings Load()
    {
        foreach (var path in new[] { Path_, BackupPath }) // 본 파일이 손상되면 백업으로 복원
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                    if (s != null) { s.Rules ??= new(); return s; }
                }
            }
            catch { }
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Store.DataDir);
        try { if (File.Exists(Path_)) File.Copy(Path_, BackupPath, overwrite: true); } catch { }
        File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>처리되지 않은 예외를 데이터 폴더 crash.log 에 남긴다 (외부 전송 없음, 경로 · 메일 · 키는 마스킹).</summary>
public static class CrashLog
{
    public static string PathOf => System.IO.Path.Combine(Store.DataDir, "crash.log");

    public static void Write(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Store.DataDir);
            var fi = new FileInfo(PathOf);
            if (fi.Exists && fi.Length > 1_000_000) File.Move(PathOf, PathOf + ".1", overwrite: true);
            File.AppendAllText(PathOf,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{App.Version} {where}\n{Advisor.MaskSensitive(ex?.ToString() ?? "(no exception)")}\n\n");
        }
        catch { /* 기록 실패는 무시 (앱을 더 망가뜨리지 않는다) */ }
    }
}

public partial class App : Application
{
    public static Settings Settings { get; private set; } = Settings.Load();
    public static Store Store { get; private set; } = null!;
    public static Ollama Ollama { get; private set; } = null!;
    public static bool DemoMode { get; set; }
    public static string? LangOverride { get; set; }
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public static void ApplyTheme()
    {
        Current.ThemeMode = Settings.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    private void HookGlobalErrors()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            CrashLog.Write("UI", ex.Exception);
            try { Store?.LogError(L.T("처리되지 않은 오류", "Unhandled error"), ex.Exception.Message); } catch { }
            MessageBox.Show(L.T(
                    $"예상하지 못한 오류가 났습니다. 작업은 계속할 수 있습니다.\n\n{ex.Exception.Message}\n\n기록: {Store.DisplayPath(CrashLog.PathOf)}",
                    $"An unexpected error occurred. You can keep working.\n\n{ex.Exception.Message}\n\nLog: {Store.DisplayPath(CrashLog.PathOf)}"),
                "Prompt Advisor", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => CrashLog.Write("AppDomain", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { CrashLog.Write("Task", ex.Exception); ex.SetObserved(); };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookGlobalErrors();
        Settings = Settings.Load();
        L.Init(LangOverride ?? Settings.Language);
        Advisor.Rules = Settings.Rules;
        ApplyTheme();
        Ollama = new Ollama { BaseUrl = Settings.OllamaUrl, GenModel = Settings.GenModel, EmbedModel = Settings.EmbedModel };

        // 헤드리스 모드: --selftest / --mcp / --ollama-test / --export-knowledge / --import / --update
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }
        if (e.Args.Contains("--mcp")) { McpServer.Run(); Shutdown(); return; }
        if (e.Args.Contains("--measure"))
        {
            // 측정 보고 (보고서용): --demo 와 같이 쓰면 합성 예시 DB 를 새로 만들어 잰다
            if (DemoMode) SampleData.CreateDemoDb(Store.DbPath);
            using var store = new Store();
            File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "measure-result.txt"), Verification.MeasureReport(store, Settings.Rules));
            Shutdown();
            return;
        }
        if (e.Args.Contains("--update"))
        {
            // 헤드리스 업데이트 검증: 적용 뒤 새 버전이 같은 데이터 폴더로 셀프테스트를 돌려 버전을 남긴다
            var resultPath = System.IO.Path.Combine(Store.DataDir, "update-result.txt");
            Directory.CreateDirectory(Store.DataDir);
            var r = await Updater.CheckAsync(apply: true, restartArgs: new[] { "--selftest", "--data", Store.DataDir },
                beforeApply: msg => File.WriteAllText(resultPath, msg));
            File.WriteAllText(resultPath, r);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--ollama-test"))
        {
            Directory.CreateDirectory(Store.DataDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vec = await Ollama.Embed("이 프롬프트를 요약해 주세요");
            var sb = new System.Text.StringBuilder();
            await Ollama.ChatStream(
                "너는 프롬프트 개선 전문가다. 개선된 프롬프트만 한 줄로 출력한다.",
                "[원본 프롬프트]\n이거 고쳐줘\n\n위 원본을 개선해라.", s => sb.Append(s));
            File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "ollama-result.txt"),
                $"embedDim={vec?.Length} elapsed={sw.Elapsed.TotalSeconds:F0}s\n---\n{sb}");
            Shutdown();
            return;
        }
        if (e.Args.Contains("--export-knowledge"))
        {
            using var store = new Store();
            var dir = Advisor.FindRepoDir(Settings.KnowledgeDir);
            string result;
            if (dir == null) result = "repo-not-found";
            else
            {
                var (count, _) = Advisor.WriteKnowledgeExport(dir, store, Settings.FavoriteModels, Settings.SelectedModel, DateTime.Now);
                result = $"exported prompts={count}";
            }
            File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "knowledge-result.txt"), result);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--import"))
        {
            using var store = new Store();
            var reports = new List<ImportReport>();
            int total = 0;
            foreach (var job in new Func<ImportReport>[] { () => Importers.ClaudeCode(), () => Importers.Cursor(), Importers.Antigravity })
            {
                var rep = job();
                reports.Add(rep);
                total += store.InsertMany(rep.Rows);
            }
            Advisor.ComputeLabels(store);
            int matched = store.MatchPredictions() + store.MatchRewrites();
            var (cnt, sessions, inTok, outTok, bySource) = store.Stats();
            File.WriteAllText(System.IO.Path.Combine(Store.DataDir, "import-result.txt"),
                $"inserted={total} total={cnt} sessions={sessions} inTok={inTok} outTok={outTok} matched={matched}\n" +
                string.Join("\n", bySource.Select(kv => $"{kv.Key}={kv.Value}")) + "\n" +
                string.Join("\n", reports.SelectMany(r => r.Warnings)));
            Shutdown();
            return;
        }

        try
        {
            if (DemoMode) SampleData.CreateDemoDb(Store.DbPath);
            var pending = Store.DbPath + ".restore"; // [DB 복원] 은 다음 실행 때 교체한다 (열린 연결과 충돌하지 않게)
            if (File.Exists(pending)) { File.Copy(pending, Store.DbPath, overwrite: true); File.Delete(pending); }
            Store = new Store();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Store", ex);
            MessageBox.Show(L.T($"데이터베이스를 열지 못했습니다.\n{ex.Message}\n\n데이터 폴더: {Store.DisplayPath(Store.DataDir)}",
                $"Could not open the database.\n{ex.Message}\n\nData folder: {Store.DisplayPath(Store.DataDir)}"),
                "Prompt Advisor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        // models.json 편집본을 데이터 폴더에 복사해두기(최초 1회)
        var editable = System.IO.Path.Combine(Store.DataDir, "models.json");
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "models.json");
        if (!File.Exists(editable) && File.Exists(bundled)) File.Copy(bundled, editable);

        var win = new MainWindow();
        win.Show();
        if (e.Args.Contains("--shots"))
        {
            // 화면 촬영: 합성 예시 DB 로만 동작 (Program 이 --shots 면 예시 모드를 강제)
            int i = Array.IndexOf(e.Args, "--shots");
            var dir = i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith("--") ? e.Args[i + 1] : System.IO.Path.Combine(Store.DataDir, "shots");
            await win.CaptureShotsAsync(System.IO.Path.GetFullPath(dir));
            Shutdown();
        }
    }
}
