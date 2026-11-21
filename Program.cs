using Velopack;

namespace PromptAdvisor;

/// <summary>
/// 진입점. Velopack 설치·업데이트 훅이 WPF 보다 먼저 돌아야 해서 Main 을 직접 둔다.
/// 명령줄: --data &lt;폴더&gt; (데이터 폴더 지정), --demo (합성 예시 DB), --lang ko|en,
///         --selftest, --mcp, --import, --ollama-test, --export-knowledge, --update
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        // 셀프테스트는 사용자 DB 를 건드리지 않도록 임시 폴더를 기본으로 쓴다
        var data = Arg("--data")
                   ?? (args.Contains("--selftest") ? Path.Combine(Path.GetTempPath(), $"prompt-advisor-selftest-{Environment.ProcessId}") : null);
        if (data != null) Store.DataDirOverride = Path.GetFullPath(data);
        if (args.Contains("--demo") || args.Contains("--shots"))
        {
            App.DemoMode = true;
            Store.DataDirOverride = Path.Combine(Store.DataDir, "demo");
        }
        App.LangOverride = Arg("--lang");

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
