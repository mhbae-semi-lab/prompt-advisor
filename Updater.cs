using Velopack;
using Velopack.Sources;

namespace PromptAdvisor;

/// <summary>
/// GitHub Releases 자동 업데이트 (Velopack). 설치판에서만 동작하고, 개발 실행(dotnet run)에서는 "설치판 아님"을 돌려준다.
/// PROMPTADVISOR_UPDATE_FEED 에 폴더나 URL 을 주면 그 피드를 쓴다 (로컬 v1.0.0 → v1.0.1 검증용).
/// </summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/mhbae-semi-lab/prompt-advisor";

    private static UpdateManager Manager()
    {
        var feed = Environment.GetEnvironmentVariable("PROMPTADVISOR_UPDATE_FEED");
        return string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(RepoUrl, null, false))
            : new UpdateManager(feed);
    }

    /// <summary>업데이트 확인. apply=true 면 내려받아 적용하고 재시작한다. 결과는 사람이 읽을 한 줄.</summary>
    public static async Task<string> CheckAsync(bool apply, string[]? restartArgs)
    {
        try
        {
            var mgr = Manager();
            if (!mgr.IsInstalled)
                return L.T($"설치판이 아니라 업데이트를 확인하지 않습니다 (현재 v{App.Version}).", $"Not an installed build, so updates are not checked (v{App.Version}).");
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return L.T($"최신 버전입니다 (v{mgr.CurrentVersion}).", $"You are up to date (v{mgr.CurrentVersion}).");
            var next = info.TargetFullRelease.Version;
            if (!apply) return L.T($"새 버전 v{next} 이 있습니다 (현재 v{mgr.CurrentVersion}).", $"Version v{next} is available (current v{mgr.CurrentVersion}).");
            await mgr.DownloadUpdatesAsync(info);
            mgr.ApplyUpdatesAndRestart(info.TargetFullRelease, restartArgs);
            return L.T($"v{next} 적용 후 재시작합니다.", $"Applying v{next} and restarting.");
        }
        catch (Exception ex)
        {
            return L.T($"업데이트 확인 실패: {ex.Message}", $"Update check failed: {ex.Message}");
        }
    }
}
