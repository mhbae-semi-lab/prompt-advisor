using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptAdvisor;

public sealed record LintHit(string Id, string Rule, string Message, long WasteTokens, int Severity); // Id=규칙 키(언어 무관), Rule=표시명, Severity 1=주의 2=경고
public sealed record ModelInfo(string Name, string Tool, string Plan, double InputPerM, double OutputPerM, string[] Strengths, string Note, double OutputFactor = 1.0);
public sealed record ModelRank(ModelInfo Model, int Rank, string Fit, long EstTotalTokens, long EstOutTokens, double Cost, string Reason, bool Pinned);
public sealed record NeighborInfo(PromptRow Row, double Similarity);
public sealed record SkillInfo(string Name, string Description, string Path);

/// <summary>
/// 프롬프트 한 줄 밖의 작업 맥락: 현재 프로젝트(문서·파일 트리·최근 변경 파일) + 세션 직전 대화 + 추론된 작업 목적/대상 파일.
/// UI에서 사용자가 작업 목적과 파일 체크칩을 수정하면 개선 프롬프트 생성에 즉시 반영된다.
/// </summary>
public sealed class WorkContext
{
    public string ProjectName = "";
    public string ProjectDir = "";
    public string ProjectSummary = "";
    public string SessionGoal = "";
    public List<string> RecentPrompts = new();
    public List<(string File, string Reason, bool Selected)> CandidateFiles = new();
    public string WhyInferred = "";
    public bool IsFollowUp;
    public int HistoricalSampleCount;
    public double HistoricalFailRate;
    public double HistoricalAvgTurns;
    public long HistoricalAvgTokens;

    public List<string> SelectedFiles => CandidateFiles.Where(f => f.Selected).Select(f => f.File).ToList();
}

public sealed class Analysis
{
    public List<LintHit> Lints = new();
    public string TaskType = "chat";
    public int ImageCount;
    public long EstInputTokens, EstOutputTokens;
    public double CalibrationFactor = 1.0;
    public int CalibrationSamples;
    public double PredictionErrorPct;
    public double InputCalibrationFactor = 1.0;
    public int InputCalibrationSamples;
    public List<NeighborInfo> Neighbors = new();
    public List<ModelRank> Models = new();
    public List<(SkillInfo Skill, double Similarity)> Skills = new();
    public string? OllamaNote;
    public WorkContext? Context;
}

public static partial class Advisor
{
    // ---------- 작업 유형 분류 ----------
    private static readonly (string type, string[] kw)[] TaskRules =
    {
        ("research", new[] { "검색", "조사", "최신", "찾아봐", "리서치", "뉴스", "search", "latest", "look up", "research" }),
        ("image",    new[] { "이미지", "그림", "사진", "로고", "image", "picture", "screenshot of" }),
        ("summary",  new[] { "요약", "짧게", "한 줄", "한줄", "tldr", "tl;dr", "정리해줘", "summarize", "summary" }),
        ("refactor", new[] { "리팩터", "리팩토링", "refactor", "구조 개선", "전부 바꿔", "마이그레이션", "restructure", "migrate" }),
        ("code",     new[] { "고쳐", "오류", "에러", "error", "exception", "함수", "빌드", "컴파일", ".cs", ".xaml", ".py", ".ts", ".js", ".cpp", "버그", "fix", "nullreference", "구현", "수정", "바꿔", "추가해", "기능", "동작", "버튼", "컬럼", "테이블", "메서드", "클래스", "연동", "반영", "안 됨", "안됨", "안 나", "깨짐", "알람", "인터락", "bug", "crash", "implement", "compile", "build fails", "doesn't work", "not working", "broken", "alarm" }),
        ("document", new[] { "보고서", "문서", "작성해", "초안", "메일", "기획서", "가이드", "guide", "메뉴얼", "매뉴얼", "document", "report", "readme", "write up", "email" }),
        ("analysis", new[] { "분석", "원인", "왜 ", "왜그", "대규모", "전체", "비교", "어떻게", "차이", "진단", "파악", "문제", "방향", "설계", "analyze", "analyse", "root cause", "why ", "compare", "diagnose", "design", "how do" }),
    };

    private static readonly string[] FollowUpKeywords =
    {
        "이거", "그거", "저거", "방금", "위에서", "여기서", "다시", "마저", "계속", "똑같이",
        "추가로", "아까", "그럼", "근데", "그런데", "왜 안", "안 되", "안되", "안 나", "똑같은",
        "again", "same error", "still", "as before", "the above", "that one"
    };

    public static bool IsFollowUpPrompt(string text)
    {
        var t = text.Trim();
        if (Importers.IsCorrection(t)) return true;
        return FollowUpKeywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public static string Classify(string text, WorkContext? ctx = null)
    {
        var low = text.ToLowerInvariant();
        foreach (var (type, kw) in TaskRules)
            if (kw.Any(k => low.Contains(k))) return type;

        if (ctx != null)
        {
            // 현재 문장에 명시적 키워드가 없어도 직전 프롬프트/세션 목표나 매칭된 코드 파일이 있으면 맥락으로 분류
            if (ctx.SelectedFiles.Any(f => Regex.IsMatch(f, @"\.(cs|xaml|csproj|py|ts|tsx|js|cpp|h|sql)$", RegexOptions.IgnoreCase)))
            {
                bool questionLike = low.Contains('?') || low.Contains("있어") || low.Contains("없어") || low.Contains("맞아") || low.Contains("어디");
                return questionLike ? "analysis" : "code";
            }
            foreach (var prev in ctx.RecentPrompts)
            {
                var prevLow = prev.ToLowerInvariant();
                foreach (var (type, kw) in TaskRules)
                    if (kw.Any(k => prevLow.Contains(k))) return type;
            }
            if (!string.IsNullOrWhiteSpace(ctx.SessionGoal))
            {
                var goalLow = ctx.SessionGoal.ToLowerInvariant();
                foreach (var (type, kw) in TaskRules)
                    if (kw.Any(k => goalLow.Contains(k))) return type;
            }
        }
        // 영문 식별자(CamelCase/snake_case)나 번호 매긴 기술 질문이면 일반 대화가 아니라 분석/코드로 판정
        if (Regex.IsMatch(text, @"\b[A-Za-z]+[A-Z_][A-Za-z0-9_]*\b"))
            return text.Contains('?') ? "analysis" : "code";

        return text.Length > 600 ? "analysis" : "chat";
    }

    public static string TaskLabel(string t) => t switch
    {
        "code" => L.T("코드 수정", "Code change"), "refactor" => L.T("대규모 리팩터", "Large refactor"), "summary" => L.T("요약/짧은 문구", "Summary"),
        "document" => L.T("문서 작성", "Document"), "analysis" => L.T("분석", "Analysis"), "research" => L.T("웹 리서치", "Web research"),
        "image" => L.T("이미지", "Image"), _ => L.T("일반 대화", "Chat"),
    };

    /// <summary>규칙 키 → 짧은 표시 이름 (표 · 비교용).</summary>
    public static string RuleName(string id) => id switch
    {
        "large-paste" => L.T("대량 붙여넣기", "large paste"), "raw-log" => L.T("로그 원문", "raw log"),
        "vague" => L.T("범위 불명확", "vague"), "no-format" => L.T("형식 없음", "no format"),
        "no-file" => L.T("파일 경로 없음", "no file"), "multi-task" => L.T("다중 작업", "many tasks"),
        "neighbor-risk" => L.T("유사 정정 이력", "similar corrections"),
        "eq-target" => L.T("장비·모듈 없음", "no target"), "eq-version" => L.T("버전 없음", "no version"),
        "eq-log" => L.T("로그 없음", "no log"), "eq-repro" => L.T("재현 조건 없음", "no repro"),
        "eq-expected" => L.T("기대 결과 없음", "no expected"), "big-image" => L.T("큰 이미지", "large image"),
        _ => id,
    };

    /// <summary>개선안에 남은 "(예: …)" · "(여기에 …)" 같은 채울 칸 안내를 지운다. 개선 뒤 경고 수를 셀 때 안내문이 답으로 잡히지 않게.</summary>
    public static string StripPlaceholders(string text)
        => Regex.Replace(text, @"\((?:예:|e\.g\.|여기에|write the|알람 전후|어떤 동작|정상이면 어떻게|which action|what should|5–10 lines)[^)]*\)", "");

    private static readonly string[] TaskHeaders = { "[작업]", "[Task]" };
    private static readonly string[] EquipHeaders = { "[장비 정보", "[Equipment info" };
    private static readonly string[] FormatHeaders = { "[출력 형식]", "[Output format]" };

    /// <summary>
    /// 개선안 기준 린트: 템플릿 문구(조건 · 출력 형식 · 칸 이름)가 답으로 잡히지 않게
    /// [작업] 본문 + [장비 정보] 에 채운 값 + 지정된 대상 파일만 린트한다. [출력 형식] 칸이 있으면 형식 경고는 해결로 본다.
    /// 섹션 머리가 없는 글(LLM 이 자유롭게 쓴 개선안)은 전체를 린트한다.
    /// </summary>
    public static List<LintHit> LintRewrite(string improved, RuleConfig? cfg = null)
    {
        var text = StripPlaceholders(improved).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        string section = "";
        var body = new List<string>();
        bool hasFormat = false, hasSections = false;
        var files = new List<string>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith('[') && l.Contains(']') && l.IndexOf(']') > 1)
            {
                hasSections = true;
                section = TaskHeaders.Any(h => l.StartsWith(h)) ? "task"
                        : EquipHeaders.Any(h => l.StartsWith(h)) ? "equip"
                        : FormatHeaders.Any(h => l.StartsWith(h)) ? "format" : "other";
                continue;
            }
            if (l.Length == 0) continue;
            foreach (Match m in FilePathRegex().Matches(l)) files.Add(m.Value);
            if (section == "task") body.Add(l);
            else if (section == "equip")
            {
                var m = Regex.Match(l, @"^-\s*[^:]{1,30}:\s*(.*)$");
                var value = m.Success ? m.Groups[1].Value.Trim() : l;
                if (value.Length > 0) body.Add(value);
            }
            else if (section == "format") hasFormat = true;
        }
        if (!hasSections) return Lint(text, null, cfg);
        var joined = string.Join("\n", body) + (files.Count > 0 ? "\n" + string.Join(" ", files.Distinct()) : "");
        var hits = Lint(joined, null, cfg);
        if (hasFormat) hits.RemoveAll(h => h.Id == "no-format");
        return hits;
    }

    /// <summary>현재 적용 중인 린트 규칙 설정 (App 시작 시 설정에서 읽음, 검증 탭에서 전환 · 롤백).</summary>
    public static RuleConfig Rules { get; set; } = new();

    // ---------- 프로젝트·세션 맥락 스캔 및 추론 ----------
    private static readonly string[] IgnoredDirs =
        { ".git", "bin", "obj", "node_modules", "publish", ".vs", ".idea", "packages", "dist", "build", "__pycache__", ".next" };
    private static readonly string[] CodeExts =
        { ".cs", ".xaml", ".csproj", ".py", ".ts", ".tsx", ".js", ".cpp", ".h", ".sql", ".json", ".md" };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime CachedAt, string Summary, Dictionary<string, string> Roles, List<(string RelPath, DateTime LastWrite)> Files)> _scanCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>프로젝트 폴더의 AGENTS.md/CLAUDE.md/README.md와 소스 파일 트리를 스캔해 요약·역할 맵·파일 목록을 반환 (60초 캐시).</summary>
    public static (string summary, Dictionary<string, string> fileRoles, List<(string RelPath, DateTime LastWrite)> files) ScanProjectFolder(string? projectDir)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string RelPath, DateTime LastWrite)>();
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return ("", roles, files);

        var fullKey = Path.GetFullPath(projectDir.Trim().TrimEnd('\\', '/'));
        if (_scanCache.TryGetValue(fullKey, out var cached) && (DateTime.UtcNow - cached.CachedAt).TotalSeconds < 60)
            return (cached.Summary, cached.Roles, cached.Files);

        string summary = "";
        foreach (var docName in new[] { "AGENTS.md", "CLAUDE.md", "README.md" })
        {
            var docPath = Path.Combine(projectDir, docName);
            if (!File.Exists(docPath)) continue;
            try
            {
                var lines = File.ReadAllLines(docPath);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    // 마크다운 표에서 `| 파일명 | 역할 |` 추출
                    if (line.StartsWith('|') && line.EndsWith('|'))
                    {
                        var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray();
                        if (cells.Length >= 2)
                        {
                            var fileMatches = Regex.Matches(cells[0], @"[\w\-]+\.(cs|xaml|py|ts|tsx|js|json|md|cpp|h)");
                            foreach (Match fm in fileMatches)
                                roles[fm.Value] = cells[1];
                            // `MainWindow.xaml(.cs)` 같은 표기 처리
                            if (cells[0].Contains("(.cs)", StringComparison.OrdinalIgnoreCase))
                            {
                                var baseFile = Regex.Match(cells[0], @"[\w\-]+\.xaml").Value;
                                if (baseFile.Length > 0)
                                {
                                    roles[baseFile] = cells[1];
                                    roles[baseFile + ".cs"] = cells[1];
                                }
                            }
                        }
                    }
                    else if (summary.Length == 0 && line.Length >= 12
                             && !line.StartsWith('#') && !line.StartsWith('>') && !line.StartsWith('@')
                             && !line.StartsWith('-') && !line.StartsWith("<!--") && !line.StartsWith("```"))
                    {
                        summary = Regex.Replace(line, @"\*\*|__", "");
                        if (summary.Length > 140) summary = summary[..140] + "…";
                    }
                }
            }
            catch { }
        }

        // 하위 소스 파일 수집 (최대 깊이 4, 무시 폴더 제외)
        void Walk(string currentDir, int depth)
        {
            if (depth > 4 || files.Count >= 400) return;
            try
            {
                foreach (var f in Directory.GetFiles(currentDir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!CodeExts.Contains(ext)) continue;
                    var rel = Path.GetRelativePath(projectDir, f).Replace('\\', '/');
                    files.Add((rel, File.GetLastWriteTimeUtc(f)));
                }
                foreach (var d in Directory.GetDirectories(currentDir))
                {
                    var dn = Path.GetFileName(d);
                    if (IgnoredDirs.Contains(dn, StringComparer.OrdinalIgnoreCase)) continue;
                    Walk(d, depth + 1);
                }
            }
            catch { }
        }
        Walk(projectDir, 0);
        _scanCache[fullKey] = (DateTime.UtcNow, summary, roles, files);
        return (summary, roles, files);
    }

    /// <summary>
    /// 프롬프트 + 로컬 프로젝트 파일 트리(캐시) + SQLite 과거 실측 이력(토큰·턴·실패 신호·빈출 파일)을 통합 분석한다.
    /// </summary>
    public static WorkContext AnalyzePromptContext(
        string prompt,
        string? projectDir,
        string? projectName,
        IReadOnlyList<PromptRow>? projectHistory = null,
        IReadOnlyList<string>? extraSessionPrompts = null)
    {
        var recentTexts = new List<string>();
        if (projectHistory is { Count: > 0 })
        {
            var latestSession = projectHistory[0].Session;
            foreach (var r in projectHistory.Where(x => x.Session == latestSession).Take(3).Reverse())
                if (!recentTexts.Contains(r.Text)) recentTexts.Add(r.Text);
        }
        if (extraSessionPrompts != null)
            foreach (var sp in extraSessionPrompts)
                if (!string.IsNullOrWhiteSpace(sp) && !recentTexts.Contains(sp)) recentTexts.Add(sp);

        var ctx = BuildWorkContext(prompt, projectDir, projectName, recentTexts);

        if (projectHistory is { Count: > 0 })
        {
            int total = projectHistory.Count;
            int fails = projectHistory.Count(r => r.Fail > 0);
            double avgTurns = projectHistory.Where(r => r.SegTurns > 0).Select(r => (double)r.SegTurns).DefaultIfEmpty(1).Average();
            long avgTokens = (long)projectHistory.Where(r => r.TotalTok > 0).Select(r => (double)r.TotalTok).DefaultIfEmpty(0).Average();

            ctx.HistoricalSampleCount = total;
            ctx.HistoricalFailRate = (double)fails / total;
            ctx.HistoricalAvgTurns = avgTurns;
            ctx.HistoricalAvgTokens = avgTokens;

            // 과거 '최적(good)' 판정 프롬프트에서 현재 프롬프트 키워드와 함께 자주 등장한 파일 추출
            var words = Regex.Matches(prompt.ToLowerInvariant(), @"[a-z0-9]{2,}|[가-힣]{2,}")
                .Select(m => m.Value).Distinct().ToList();
            var fileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in projectHistory.Where(r => r.EffectiveLabel == "최적" || r.Fail == 0))
            {
                bool relevant = words.Count == 0 || words.Any(w => row.Text.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (!relevant) continue;
                foreach (Match m in FilePathRegex().Matches(row.Text))
                {
                    var fn = Path.GetFileName(m.Value);
                    fileCounts[fn] = fileCounts.GetValueOrDefault(fn) + (row.EffectiveLabel == "최적" ? 2 : 1);
                }
            }
            foreach (var (fn, cnt) in fileCounts.OrderByDescending(kv => kv.Value).Take(3))
            {
                if (!ctx.CandidateFiles.Any(c => Path.GetFileName(c.File).Equals(fn, StringComparison.OrdinalIgnoreCase)))
                    ctx.CandidateFiles.Add((fn, $"과거 이력 빈출 파일 ({cnt}회)", ctx.CandidateFiles.Count(c => c.Selected) < 3));
            }

            ctx.WhyInferred += $" · DB 이력 {total}건(실패율 {ctx.HistoricalFailRate * 100:F0}%, 평균 {avgTurns:F1}턴)";
        }
        return ctx;
    }

    /// <summary>입력 프롬프트 + 직전 대화 + 프로젝트 폴더를 종합해 WorkContext를 추론한다.</summary>
    public static WorkContext BuildWorkContext(
        string prompt,
        string? projectDir,
        string? projectName,
        IReadOnlyList<string>? recentPrompts = null)
    {
        var ctx = new WorkContext
        {
            IsFollowUp = IsFollowUpPrompt(prompt),
        };
        if (recentPrompts != null)
            ctx.RecentPrompts = recentPrompts.Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim() != prompt.Trim()).TakeLast(3).ToList();

        // 1) 프로젝트 경로·이름 확정
        string resolvedDir = projectDir?.Trim() ?? "";
        string resolvedName = projectName?.Trim() ?? "";
        if (!string.IsNullOrEmpty(resolvedDir) && Directory.Exists(resolvedDir))
        {
            resolvedDir = Path.GetFullPath(resolvedDir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(resolvedName)) resolvedName = Path.GetFileName(resolvedDir);
        }
        else if (!string.IsNullOrEmpty(resolvedName))
        {
            var match = Importers.DiscoverProjectDirs().FirstOrDefault(d => d.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Dir)) resolvedDir = match.Dir;
        }
        ctx.ProjectDir = resolvedDir;
        ctx.ProjectName = resolvedName;

        // 2) 프로젝트 폴더 스캔
        var (summary, roles, projFiles) = ScanProjectFolder(resolvedDir);
        ctx.ProjectSummary = summary;

        // 3) 세션 작업 목적(SessionGoal) 추론
        static string OneLine(string s, int max = 85)
        {
            var clean = Regex.Replace(s.Trim(), @"\s+", " ");
            return clean.Length > max ? clean[..max] + "…" : clean;
        }
        if (ctx.IsFollowUp && ctx.RecentPrompts.Count > 0)
        {
            var anchor = ctx.RecentPrompts.FirstOrDefault(p => p.Length >= 20) ?? ctx.RecentPrompts[^1];
            ctx.SessionGoal = $"{OneLine(anchor, 55)} → {OneLine(prompt, 45)}";
        }
        else
        {
            ctx.SessionGoal = !string.IsNullOrEmpty(ctx.ProjectName)
                ? $"[{ctx.ProjectName}] {OneLine(prompt, 75)}"
                : OneLine(prompt, 80);
        }

        // 4) 대상 파일 후보 추론 (프롬프트 명시 → 직전 대화 명시 → 프로젝트 파일/역할 키워드 매칭 → 최근 수정 파일)
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string file, string reason, bool selected)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            var key = Path.GetFileName(file);
            if (!added.Add(key)) return;
            ctx.CandidateFiles.Add((file, reason, selected));
        }

        foreach (Match m in FilePathRegex().Matches(prompt))
            AddCandidate(m.Value, "프롬프트에 직접 명시", true);

        foreach (var prev in ctx.RecentPrompts)
            foreach (Match m in FilePathRegex().Matches(prev))
                AddCandidate(m.Value, "직전 대화에서 언급", ctx.CandidateFiles.Count < 3);

        if (projFiles.Count > 0)
        {
            var combinedText = (prompt + " " + string.Join(" ", ctx.RecentPrompts)).ToLowerInvariant();
            var words = Regex.Matches(combinedText, @"[a-z0-9]{2,}|[가-힣]{2,}")
                .Select(m => m.Value).Where(w => w.Length >= 2).Distinct().ToList();

            var scoredFiles = new List<(string RelPath, int Score, string Why, DateTime LastWrite)>();
            foreach (var (rel, lw) in projFiles)
            {
                var fileName = Path.GetFileName(rel);
                var stem = Path.GetFileNameWithoutExtension(rel).ToLowerInvariant();
                if (fileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                int score = 0;
                var reasons = new List<string>();
                if (stem.Length >= 3 && combinedText.Contains(stem))
                {
                    score += 5;
                    reasons.Add($"파일명 '{stem}' 일치");
                }
                if (roles.TryGetValue(fileName, out var roleDesc))
                {
                    int roleHits = words.Count(w => roleDesc.Contains(w, StringComparison.OrdinalIgnoreCase));
                    if (roleHits > 0)
                    {
                        score += roleHits * 2;
                        reasons.Add($"역할({ (roleDesc.Length > 28 ? roleDesc[..28] + "…" : roleDesc) }) 매칭");
                    }
                }
                if (score > 0)
                    scoredFiles.Add((rel, score, string.Join(", ", reasons), lw));
            }

            foreach (var hit in scoredFiles.OrderByDescending(x => x.Score).ThenByDescending(x => x.LastWrite).Take(4))
                AddCandidate(hit.RelPath, hit.Why, ctx.CandidateFiles.Count(c => c.Selected) < 3);

            // 매칭된 코드 파일이 아직 없으면 최근 수정된 코드 파일 상위 2개를 후보로 제시
            foreach (var recentFile in projFiles
                .Where(f => f.RelPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.RelPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                         || f.RelPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                         || f.RelPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                         || f.RelPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWrite)
                .Take(3))
            {
                bool autoPick = ctx.CandidateFiles.Count(c => c.Selected) == 0 && ctx.IsFollowUp;
                AddCandidate(recentFile.RelPath, "최근 수정 파일", autoPick);
            }
        }

        // 5) 추론 근거 요약
        var whyParts = new List<string>();
        if (!string.IsNullOrEmpty(ctx.ProjectName))
            whyParts.Add($"프로젝트 '{ctx.ProjectName}'" + (projFiles.Count > 0 ? $" (파일 {projFiles.Count}개 스캔)" : ""));
        if (ctx.RecentPrompts.Count > 0)
            whyParts.Add($"직전 대화 {ctx.RecentPrompts.Count}건 연결" + (ctx.IsFollowUp ? " (후속 지시 감지)" : ""));
        if (ctx.SelectedFiles.Count > 0)
            whyParts.Add($"대상 파일 {ctx.SelectedFiles.Count}개 자동 선택");
        ctx.WhyInferred = whyParts.Count > 0
            ? string.Join(" · ", whyParts)
            : "단일 프롬프트 기준 (프로젝트 폴더를 지정하면 파일·문서 맥락이 자동 연동됩니다)";

        return ctx;
    }

    // ---------- 규칙 린트 (LLM 없음, 즉시 + 맥락 보완) ----------
    [GeneratedRegex(@"([A-Za-z]:\\[^\s]+|\b[\w\-]+\.(cs|xaml|csproj|py|ts|tsx|js|json|md)\b)")]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"(Exception|StackTrace|at [\w\.]+\(|오류\s*CS\d{4}|error [A-Z]+\d+)")]
    private static partial Regex LogRegex();

    private static readonly string[] FormatKeywords =
    {
        "형식", "표로", "목록", "짧게", "줄 이내", "줄로", "코드만", "설명 없이", "bullet", "표 형식", "diff", "json",
        "format", "as a table", "table", "list", "bullets", "briefly", "in one line", "lines max", "code only", "no explanation", "markdown", "step by step",
    };
    private static readonly string[] VagueKeywords =
        { "이거", "그거", "고쳐줘", "해줘", "fix it", "fix this", "do it", "make it work", "doesn't work", "not working", "it's broken", "check this" };

    /// <summary>
    /// 규칙 린트. cfg 가 없으면 현재 설정(Rules). impact 가 있으면 "내 기록"으로 학습한 수치를 메시지와 예상 낭비에 쓴다
    /// (규칙별로 경고가 뜬 요청과 안 뜬 요청의 정정 · 턴 · 토큰 차이, 표본 3건 미만이면 기본값).
    /// </summary>
    public static List<LintHit> Lint(string text, WorkContext? ctx = null, RuleConfig? cfg = null,
        IReadOnlyDictionary<string, RuleImpact.Stat>? impact = null)
    {
        cfg ??= Rules;
        var hits = new List<LintHit>();
        var t = text.Trim();
        void Add(string id, string rule, string msg, long waste, int sev)
        {
            if (impact != null && impact.TryGetValue(id, out var st) && st.Enough)
            {
                msg += "\n" + RuleImpact.Describe(st);
                if (st.ExtraTokens > 0) waste = st.ExtraTokens;
            }
            hits.Add(new LintHit(id, rule, msg, waste, sev));
        }

        // 1) 대량 붙여넣기
        if (t.Length > cfg.LargePasteChars)
            Add("large-paste", L.T("대량 붙여넣기", "Large paste"),
                L.T($"입력 {t.Length:N0}자 — 필요 구간만 발췌하면 컨텍스트·캐시 토큰을 크게 절약합니다.",
                    $"{t.Length:N0} characters — paste only the relevant part to save context and cache tokens."),
                EstimateInputTokens(t) / 2, 2);
        // 2) 로그/스택트레이스 원문
        int logLines = t.Split('\n').Count(l => LogRegex().IsMatch(l));
        if (logLines >= cfg.LogLinesMin)
            Add("raw-log", L.T("로그 원문 포함", "Raw log pasted"),
                L.T($"로그/스택트레이스로 보이는 줄 {logLines}개 — 핵심 예외 3~5줄 + 발생 위치만 남기세요.",
                    $"{logLines} log/stack-trace lines — keep the 3–5 key exception lines and where it happened."),
                logLines * 18L, 2);
        // 3) 범위 불명확
        bool vague = t.Length < cfg.VagueMaxChars && VagueKeywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (vague && !FilePathRegex().IsMatch(t))
        {
            string ctxFix = ctx is { SelectedFiles.Count: > 0 }
                ? L.T($" (직전 대화·프로젝트 맥락에서 [{string.Join(", ", ctx.SelectedFiles.Take(2))}] 및 작업 목표를 자동 보충합니다)",
                      $" (target [{string.Join(", ", ctx.SelectedFiles.Take(2))}] and goal are filled in from recent context)")
                : "";
            Add("vague", L.T("범위 불명확", "Vague scope"),
                L.T("대상 파일/함수가 없습니다. 이런 짧은 요청은 정정 턴이 잦습니다 — 파일 경로와 원하는 결과를 명시하세요.",
                    "No target file or function. Short requests like this often need corrections — name the file and the result you want.") + ctxFix,
                ctxFix.Length > 0 ? 600 : 1500, 2);
        }
        // 4) 출력 형식 미지정
        if (cfg.FormatLint && t.Length >= 30 && !FormatKeywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)))
            Add("no-format", L.T("출력 형식 미지정", "No output format"),
                L.T("출력 형식(길이·표/목록·코드만 등)이 없어 장황한 답 + 재요청 가능성이 큽니다.",
                    "No output format (length, table/list, code only…) — expect a long answer and a follow-up."),
                800, 1);
        // 5) 코드 작업인데 파일 경로 없음
        string task = Classify(t, ctx);
        if ((task is "code" or "refactor") && !FilePathRegex().IsMatch(t))
        {
            string inferredFiles = ctx is { SelectedFiles.Count: > 0 }
                ? L.T($" (프로젝트 스캔으로 추론된 [{string.Join(", ", ctx.SelectedFiles.Take(3))}]을 개선안에 자동 반영합니다)",
                      $" (inferred [{string.Join(", ", ctx.SelectedFiles.Take(3))}] will be added to the rewrite)")
                : "";
            Add("no-file", L.T("파일 경로 없음", "No file path"),
                L.T("코드 작업인데 파일/클래스명이 없습니다. 모델이 탐색에 토큰을 씁니다 — 경로를 직접 지정하세요.",
                    "Code task without a file or class name. The model will spend tokens searching — name the path.") + inferredFiles,
                inferredFiles.Length > 0 ? 700 : 2000, 1);
        }
        // 6) 한 번에 여러 작업
        int numbered = Regex.Matches(t, @"(?m)^\s*\d+[\.\)]\s").Count;
        bool chained = (t.Contains("그리고") || t.Contains(" and also ", StringComparison.OrdinalIgnoreCase)) && t.Length > 200;
        if (numbered >= 3 || (chained && numbered >= 1))
            Add("multi-task", L.T("다중 작업 일괄 요청", "Many tasks at once"),
                L.T($"작업이 {Math.Max(numbered, 2)}개 이상 섞여 있습니다. 하나씩 나눠 보내면 실패 시 재시도 비용이 줄고 결과 확인이 쉽습니다.",
                    $"{Math.Max(numbered, 2)}+ tasks in one request. Send them one by one to cut retry cost and make results easy to check."),
                EstimateInputTokens(t) / 3, 1);
        // 7) 장비 SW 요청: 대상 장비 · 버전 · 로그 발췌 · 재현 조건 · 기대 결과
        if (cfg.EquipmentLint && task is not ("summary" or "research" or "image" or "document"))
            foreach (var e in Equipment.Missing(t))
                Add(e.Id, L.T($"장비: {e.Ko} 없음", $"Equipment: no {e.En.ToLowerInvariant()}"),
                    L.T($"장비 SW 요청인데 {e.Ko}이(가) 없습니다. 모델이 추측하거나 되묻습니다 — {e.HintKo}",
                        $"Equipment software request without {e.En.ToLowerInvariant()}. The model will guess or ask back — {e.HintEn}"),
                    e.DefaultWaste, e.Severity);
        return hits;
    }

    /// <summary>kNN 유사 사례의 과거 실패 신호·턴 수를 기반으로 사전 리스크 경고를 생성한다.</summary>
    public static LintHit? CheckNeighborFailureRisk(IReadOnlyList<NeighborInfo> neighbors)
    {
        if (neighbors.Count < 2) return null;
        var risky = neighbors.Where(n => n.Similarity >= 0.50 && (n.Row.Fail > 0 || n.Row.SegTurns >= 6)).ToList();
        if (risky.Count == 0) return null;

        long avgWaste = Math.Max(800, (long)risky.Average(n => n.Row.TotalTok) / 3);
        double avgTurns = risky.Average(n => Math.Max(1, n.Row.SegTurns));
        return new LintHit(
            "neighbor-risk",
            L.T("유사 작업 정정 이력", "Similar requests needed corrections"),
            L.T($"과거 유사 프롬프트 {neighbors.Count}건 중 {risky.Count}건에서 정정/재시도(평균 {avgTurns:F1}턴)가 발생했습니다 — 변경 대상 파일과 건드리지 말아야 할 조건을 먼저 고정하세요.",
                $"{risky.Count} of {neighbors.Count} similar past prompts needed corrections (avg {avgTurns:F1} turns) — pin the target files and what must not change first."),
            avgWaste,
            Severity: 2);
    }

    // ---------- 토큰 추정: 문자 종류별 계수 (한글 1.4자/토큰, 영문 4, 코드 3.2) ----------
    public static long BaseOutputForTask(string task) => task switch
    {
        "summary" => 400, "code" => 2500, "refactor" => 6000, "analysis" => 3500, "document" => 3000, _ => 1200,
    };

    /// <summary>유사 사례 유사도 가중평균 출력(성공 사례 우선, 단순평균보다 이상치·저유사 사례 영향 축소) → 없으면 작업 유형 기본값 → 보정계수.</summary>
    public static (long estOut, double factor, int samples) EstimateOutputTokens(
        string task, IReadOnlyList<NeighborInfo> neighbors, IEnumerable<(string task, long estOut, long actOut)> calibRows)
    {
        static long WeightedAvg(List<NeighborInfo> ns)
        {
            double w = ns.Sum(n => Math.Max(0.01, n.Similarity));
            return (long)(ns.Sum(n => n.Row.OutTok * Math.Max(0.01, n.Similarity)) / w);
        }
        var validNeighbors = neighbors.Where(n => n.Row.OutTok > 0).ToList();
        var cleanNeighbors = validNeighbors.Where(n => n.Row.Fail == 0).ToList();
        long baseOut = cleanNeighbors.Count > 0
            ? WeightedAvg(cleanNeighbors)
            : validNeighbors.Count > 0
                ? WeightedAvg(validNeighbors)
                : BaseOutputForTask(task);
        var (factor, samples) = Calibration(calibRows, task);
        return ((long)(baseOut * factor), factor, samples);
    }

    /// <summary>추정값에 작업유형별 실측 보정계수를 적용 (입력·출력 공용).</summary>
    public static (long est, double factor, int samples) CalibratedEstimate(
        long baseEst, IEnumerable<(string task, long estOut, long actOut)> rows, string task)
    {
        var (factor, samples) = Calibration(rows, task);
        return ((long)(baseEst * factor), factor, samples);
    }

    /// <summary>(모델, 작업유형) 이원 보정: 모델 전체 중앙값으로 나눠 정규화하므로 7일 자동 보정(outputFactor)과 이중 계산되지 않는다. 표본 3건 미만이면 기본값 유지.</summary>
    public static double ModelTaskFactor(
        IEnumerable<(string model, string task, long estOut, long actOut)> rows, string model, string task, double baseFactor)
    {
        var mine = rows.Where(r => r.model == model && r.estOut > 0 && r.actOut > 0).ToList();
        var taskRatios = mine.Where(r => r.task == task).Select(r => (double)r.actOut / r.estOut).OrderBy(x => x).ToList();
        var modelRatios = mine.Select(r => (double)r.actOut / r.estOut).OrderBy(x => x).ToList();
        if (taskRatios.Count < 3 || modelRatios.Count < 3) return baseFactor;
        double ratio = taskRatios[taskRatios.Count / 2] / modelRatios[modelRatios.Count / 2];
        return Math.Clamp(baseFactor * ratio, 0.4, 2.0);
    }

    public static long EstimateInputTokens(string text)
    {
        int hangul = 0, ascii = 0, other = 0;
        bool inCode = text.Contains("```") || text.Contains("    ") || text.Contains("{\n");
        foreach (char c in text)
        {
            if (c is >= '가' and <= '힣') hangul++;
            else if (c < 128) ascii++;
            else other++;
        }
        double asciiPerTok = inCode ? 3.2 : 4.0;
        return (long)Math.Ceiling(hangul / 1.4 + ascii / asciiPerTok + other / 2.0);
    }

    // ---------- 자동 라벨링: 실패 신호 + 토큰 과다 → 점수, 상·하위 15% ----------
    public static void ComputeLabels(Store store)
    {
        var all = store.All();
        if (all.Count == 0) return;
        var totals = all.Where(p => p.TotalTok > 0).Select(p => (double)p.TotalTok).OrderBy(x => x).ToList();
        double median = totals.Count > 0 ? totals[totals.Count / 2] : 0;

        var scored = new List<(PromptRow row, double score)>();
        foreach (var p in all)
        {
            double score = 100;
            score -= 45 * p.Fail;                                            // 중단/정정 신호
            if (median > 0 && p.TotalTok > median * 3) score -= 25;          // 비정상적 토큰 과다
            if (median > 0 && p.TotalTok > median * 1.5) score -= 10;
            if (p.Source == "claude" && p.SegTurns > 0 && p.OutTok == 0) score -= 20; // 답 없이 끝남
            if (p.Text.Length < 15) score -= 10;                             // 맥락 부족한 초단문
            scored.Add((p, score));
        }
        int k = Math.Max(1, (int)(scored.Count * 0.15));
        var ordered = scored.OrderByDescending(s => s.score).ToList();
        var goodIds = ordered.Take(k).Where(s => s.score >= 80).Select(s => s.row.Id).ToHashSet();
        var badIds = ordered.TakeLast(k).Where(s => s.score <= 55).Select(s => s.row.Id).ToHashSet();
        foreach (var (row, score) in scored)
            store.SetScoreLabel(row.Id, score,
                goodIds.Contains(row.Id) ? "good" : badIds.Contains(row.Id) ? "bad" : "");
    }

    // ---------- kNN (코사인 brute-force) ----------
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static List<NeighborInfo> Knn(float[] query, List<PromptRow> corpus, int k = 5, string? activeProject = null)
        => corpus.Where(p => p.Embedding != null)
                 .Select(p =>
                 {
                     double sim = Cosine(query, p.Embedding!);
                     bool sameProj = !string.IsNullOrWhiteSpace(activeProject)
                         && !string.IsNullOrWhiteSpace(p.Project)
                         && p.Project.Equals(activeProject.Trim(), StringComparison.OrdinalIgnoreCase);
                     double rankScore = sim + (sameProj ? 0.12 : 0); // 같은 프로젝트 사례 우선 가중치
                     return (info: new NeighborInfo(p, Math.Min(1.0, rankScore)), rankScore, sameProj);
                 })
                 .Where(x => x.info.Similarity > 0.3)
                 .OrderByDescending(x => x.rankScore)
                 .ThenByDescending(x => x.sameProj)
                 .Take(k)
                 .Select(x => x.info)
                 .ToList();

    /// <summary>임베딩이 없을 때(Ollama 미설치) 쓰는 이웃: 단어 · 3-gram Jaccard 유사도 상위 k. 같은 프로젝트 가중치는 kNN 과 같다.</summary>
    public static List<NeighborInfo> TextNeighbors(string query, IEnumerable<PromptRow> corpus, int k = 5, string? activeProject = null)
        => corpus.Where(p => p.Text.Length > 0 && p.Text.Trim() != query.Trim())
                 .Select(p =>
                 {
                     double sim = TextSimilarity(query, p.Text);
                     bool same = !string.IsNullOrWhiteSpace(activeProject) && p.Project.Equals(activeProject.Trim(), StringComparison.OrdinalIgnoreCase);
                     return (info: new NeighborInfo(p, Math.Min(1.0, sim + (same ? 0.05 : 0))), sim);
                 })
                 .Where(x => x.sim >= 0.18)
                 .OrderByDescending(x => x.info.Similarity)
                 .Take(k).Select(x => x.info).ToList();

    // ---------- 모델 카탈로그 ----------
    private static List<ModelInfo>? _modelsCache;
    private static string _modelsCacheKey = "";

    /// <summary>models.json을 파일 변경(경로·수정시각·크기) 시에만 다시 읽는다 — 분석마다 디스크 읽기·역직렬화 반복 제거.</summary>
    public static List<ModelInfo> LoadModels()
    {
        foreach (var path in new[]
        {
            Path.Combine(Store.DataDir, "models.json"),
            Path.Combine(AppContext.BaseDirectory, "models.json"),
        })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var fi = new FileInfo(path);
                string key = $"{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
                if (_modelsCache is { Count: > 0 } && _modelsCacheKey == key) return _modelsCache;
                var list = JsonSerializer.Deserialize<List<ModelInfo>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (list is { Count: > 0 }) { _modelsCache = list; _modelsCacheKey = key; return list; }
            }
            catch { }
        }
        return new();
    }

    /// <summary>순위·적합도·모델별 예상 토큰을 포함한 추천. pinnedName이 있으면 그 모델이 1순위 고정.</summary>
    public static List<ModelRank> RecommendModels(string task, long estIn, long estOut, List<ModelInfo> models, int take = 3, string? pinnedName = null,
        List<(string model, string task, long estOut, long actOut)>? modelTaskRows = null)
    {
        take = Math.Clamp(take, 1, 10);
        var scored = models.Select(m =>
        {
            bool primary = m.Strengths.Contains(task);
            bool secondary = task == "chat" && m.Strengths.Contains("summary")
                             || m.Strengths.Contains("chat") || m.Strengths.Contains("analysis");
            string fit = primary ? "높음" : secondary ? "중간" : "낮음";
            double effFactor = modelTaskRows == null ? m.OutputFactor : ModelTaskFactor(modelTaskRows, m.Name, task, m.OutputFactor);
            long mOut = (long)(estOut * effFactor);
            long total = estIn + mOut;
            double cost = (estIn * m.InputPerM + mOut * m.OutputPerM) / 1_000_000.0;
            int fitScore = fit == "높음" ? 2 : fit == "중간" ? 1 : 0;
            return (m, fit, fitScore, total, mOut, cost);
        }).ToList();

        IEnumerable<(ModelInfo m, string fit, int fitScore, long total, long mOut, double cost)> ordered =
            scored.OrderByDescending(x => x.fitScore).ThenBy(x => x.total);
        var list = ordered.ToList();
        if (!string.IsNullOrEmpty(pinnedName))
        {
            int idx = list.FindIndex(x => x.m.Name == pinnedName);
            if (idx > 0) { var p = list[idx]; list.RemoveAt(idx); list.Insert(0, p); }
        }

        var result = new List<ModelRank>();
        foreach (var x in list.Take(take))
        {
            bool pinned = !string.IsNullOrEmpty(pinnedName) && x.m.Name == pinnedName;
            string reason = pinned
                ? $"사용자 지정 모델 — {x.m.Note}"
                : x.fit == "높음" ? $"작업 유형({TaskLabel(task)}) 강점 일치 — {x.m.Note}"
                : x.fit == "중간" ? $"범용으로 무난 — {x.m.Note}"
                : $"대안 (이 작업은 강점 아님) — {x.m.Note}";
            result.Add(new ModelRank(x.m, result.Count + 1, x.fit, x.total, x.mOut, x.cost, reason, pinned));
        }
        return result;
    }

    // ---------- 예상 vs 실제 보정 ----------
    /// <summary>작업유형별 실제/예상 출력 비율 중앙값 → 보정계수(표본 3건 미만이면 1.0, 0.5~2.0 클램프).</summary>
    public static (double factor, int samples) Calibration(IEnumerable<(string task, long estOut, long actOut)> rows, string task)
    {
        var all = rows.Where(r => r.estOut > 0 && r.actOut > 0).ToList();
        var mine = all.Where(r => r.task == task).Select(r => (double)r.actOut / r.estOut).OrderBy(x => x).ToList();
        var use = mine.Count >= 3 ? mine
            : all.Select(r => (double)r.actOut / r.estOut).OrderBy(x => x).ToList();
        if (use.Count < 3) return (1.0, use.Count);
        double median = use[use.Count / 2];
        return (Math.Clamp(median, 0.5, 2.0), use.Count);
    }

    /// <summary>보정 표본 수·평균 오차로 만든 예측 신뢰도 라벨 (분석 칩 표시용).</summary>
    public static string ConfidenceLabel(int samples, double errorPct) => samples switch
    {
        0 => L.T("신뢰도 낮음 · 실측 보정 없음", "Low confidence · no calibration"),
        < 10 => L.T($"신뢰도 중간 · 평균 오차 ±{errorPct:F0}% (보정 표본 {samples}건)", $"Medium confidence · ±{errorPct:F0}% avg error (n={samples})"),
        _ => L.T($"신뢰도 높음 · 평균 오차 ±{errorPct:F0}% (보정 표본 {samples}건)", $"High confidence · ±{errorPct:F0}% avg error (n={samples})"),
    };

    /// <summary>예측 평균 오차율(%) — |실제-예상|/실제 평균.</summary>
    public static double PredictionErrorPct(IEnumerable<(string task, long estOut, long actOut)> rows)
    {
        var valid = rows.Where(r => r.estOut > 0 && r.actOut > 0).ToList();
        if (valid.Count == 0) return 0;
        return 100.0 * valid.Average(r => Math.Abs((double)r.actOut - r.estOut) / r.actOut);
    }

    // ---------- 생성 결과 주제 검증 ----------
    /// <summary>원본에서 파일명·긴 단어를 뽑아 생성 결과에 남아 있는지 검사 (모델이 예시를 베껴 주제가 바뀐 경우 탐지).</summary>
    public static bool IsOnTopic(string original, string generated)
    {
        var fileAnchors = Regex.Matches(original, @"[\w\-]+\.(cs|xaml|csproj|py|ts|tsx|js|json|md)\b")
            .Select(m => m.Value).Distinct().ToList();
        if (fileAnchors.Count > 0) return fileAnchors.Any(f => generated.Contains(f));

        var anchors = Regex.Matches(original, @"[A-Za-z][A-Za-z0-9_]{3,}|[가-힣]{3,}")
            .Select(m => m.Value).Where(w => w.Length >= 3).Distinct().Take(10).ToList();
        if (anchors.Count == 0) return true; // 검증할 단서가 없으면 통과
        return anchors.Any(a => generated.Contains(a));
    }

    // ---------- 규칙 기반 개선 프롬프트 (Ollama 없어도 동작 + 프로젝트/세션 맥락 반영) ----------
    /// <summary>프롬프트가 한국어인지 (한글 비율 20% 이상). 개선안 · LLM 지시문을 프롬프트 언어에 맞춘다.</summary>
    public static bool IsKorean(string text)
    {
        int hangul = text.Count(c => c is >= '가' and <= '힣');
        int letters = text.Count(char.IsLetter);
        return hangul > 0 && hangul >= letters * 0.2;
    }

    /// <summary>개선안 헤더 줄. ExtractPromptOnly 가 이 줄 뒤만 꺼낸다.</summary>
    public static string ImprovedHeader(string modelName, bool ko) => ko ? $"[{modelName}용 개선 프롬프트]" : $"[Improved prompt for {modelName}]";
    public const string ShadowMarker = "---- Shadow";

    /// <summary>분석 결과(린트·작업유형·작업맥락)를 결합해 지금 입력한 프롬프트를 구체적으로 다시 쓴다. 출력 언어는 프롬프트 언어를 따른다.</summary>
    public static string BuildImprovedPrompt(string original, Analysis a, string modelName)
    {
        bool ko = IsKorean(original) || (!original.Any(char.IsLetter) && !L.En);
        string K(string k, string e) => ko ? k : e;
        var sb = new System.Text.StringBuilder();
        var rules = a.Lints.Select(l => l.Id).ToHashSet();
        var ctx = a.Context;
        sb.AppendLine(ImprovedHeader(modelName, ko));
        sb.AppendLine();

        bool hasContextBlock = ctx != null && (
            !string.IsNullOrWhiteSpace(ctx.ProjectName)
            || !string.IsNullOrWhiteSpace(ctx.SessionGoal)
            || ctx.SelectedFiles.Count > 0
            || (ctx.IsFollowUp && ctx.RecentPrompts.Count > 0));

        if (hasContextBlock && ctx != null)
        {
            sb.AppendLine(K("[작업 맥락]", "[Context]"));
            if (!string.IsNullOrWhiteSpace(ctx.ProjectName))
                sb.AppendLine(K("- 프로젝트: ", "- Project: ") + ctx.ProjectName + (!string.IsNullOrWhiteSpace(ctx.ProjectSummary) ? $" ({ctx.ProjectSummary})" : ""));
            if (!string.IsNullOrWhiteSpace(ctx.SessionGoal) && !ctx.SessionGoal.Equals(original.Trim(), StringComparison.Ordinal))
                sb.AppendLine(K("- 작업 목적: ", "- Goal: ") + ctx.SessionGoal);
            if (ctx.IsFollowUp && ctx.RecentPrompts.Count > 0)
            {
                var prev = Regex.Replace(ctx.RecentPrompts[^1].Trim(), @"\s+", " ");
                if (prev.Length > 100) prev = prev[..100] + "…";
                sb.AppendLine(K("- 직전 작업 흐름: ", "- Previous step: ") + prev);
            }
            if (ctx.SelectedFiles.Count > 0)
                sb.AppendLine(K("- 대상 파일: ", "- Target files: ") + string.Join(", ", ctx.SelectedFiles));
            if (ctx.HistoricalSampleCount >= 5)
                sb.AppendLine(K($"- 프로젝트 실측 이력: 과거 {ctx.HistoricalSampleCount:N0}건 · 평균 {ctx.HistoricalAvgTurns:F1}턴 · 실패율 {ctx.HistoricalFailRate * 100:F0}%",
                                $"- Project history: {ctx.HistoricalSampleCount:N0} prompts · avg {ctx.HistoricalAvgTurns:F1} turns · {ctx.HistoricalFailRate * 100:F0}% needed corrections"));
            sb.AppendLine();
        }

        sb.AppendLine(K("[작업]", "[Task]"));
        sb.AppendLine(original.Trim());
        sb.AppendLine();

        // 장비 SW 요청에서 빠진 항목은 채울 칸으로 내준다 (모델이 추측하지 않게)
        var eqMissing = Equipment.Elements.Where(e => rules.Contains(e.Id)).ToList();
        if (eqMissing.Count > 0)
        {
            sb.AppendLine(K("[장비 정보 — 빈 칸을 채워 보내세요]", "[Equipment info — fill in before sending]"));
            foreach (var e in eqMissing)
                sb.AppendLine($"- {(ko ? e.Ko : e.En)}: {(ko ? e.HintKo : e.HintEn)}");
            sb.AppendLine();
        }

        var conditions = new List<string>();
        if (ctx != null && ctx.SelectedFiles.Count > 0)
            conditions.Add(K($"대상 파일/클래스: {string.Join(", ", ctx.SelectedFiles)} 중심으로 확인·수정하고, 관련 없는 파일은 건드리지 마세요",
                             $"Work in {string.Join(", ", ctx.SelectedFiles)} only; do not touch unrelated files"));
        else if (rules.Contains("no-file"))
            conditions.Add(K("대상 파일/클래스: (여기에 경로를 적어주세요 — 예: MainWindow.xaml.cs의 저장 버튼 핸들러)",
                             "Target file/class: (write the path here — e.g. the Save button handler in MainWindow.xaml.cs)"));

        if (rules.Contains("vague"))
        {
            if (ctx != null && !string.IsNullOrWhiteSpace(ctx.SessionGoal))
                conditions.Add(K($"원하는 결과: '{ctx.SessionGoal}' 목적에 맞게 변경 범위를 한정하고 그 밖의 동작은 유지하세요",
                                 $"Desired result: limit the change to '{ctx.SessionGoal}' and keep everything else as is"));
            else
                conditions.Add(K("원하는 결과를 한 줄로 먼저 확정하고, 그 범위 밖의 것은 건드리지 마세요",
                                 "State the desired result in one line first and do not change anything outside it"));
        }
        if (rules.Contains("raw-log"))
            conditions.Add(K("로그는 핵심 예외 3~5줄과 발생 위치만 근거로 삼으세요 (원문 전체 재인용 금지)",
                             "Use only the 3–5 key exception lines and their location as evidence (do not quote the whole log)"));
        if (rules.Contains("large-paste"))
            conditions.Add(K("붙여넣은 자료 중 이 작업에 직접 관련된 부분만 근거로 쓰고, 나머지는 무시하세요",
                             "Use only the pasted parts that matter for this task and ignore the rest"));
        if (rules.Contains("multi-task"))
            conditions.Add(K("작업이 여러 개면 1번부터 순서대로, 하나 끝날 때마다 결과를 확인하고 다음으로 넘어가세요",
                             "If there are several tasks, do them in order and confirm each result before the next"));
        if (eqMissing.Count > 0)
            conditions.Add(K("장비 정보 칸이 비어 있으면 추측하지 말고 무엇이 필요한지 먼저 물어보세요",
                             "If any equipment info is missing, ask for it instead of guessing"));
        if (a.TaskType is "code" or "refactor")
        {
            conditions.Add(K("기존 동작을 깨지 말고, 변경 지점은 최소로 유지하세요", "Keep existing behavior and keep the change minimal"));
            conditions.Add(K("추측이 필요하면 임의로 정하지 말고 가정한 내용을 먼저 밝히세요", "If you must assume something, say what you assumed first"));
        }
        if (rules.Contains("neighbor-risk") || (ctx is { HistoricalSampleCount: >= 5, HistoricalFailRate: >= 0.08 }))
            conditions.Add(K("과거 유사 작업에서 정정 턴이 잦았습니다 — 코드 작성 전 수정할 함수·영향 범위를 한 줄로 먼저 검증하세요",
                             "Similar past tasks needed corrections — state the function to change and its impact in one line before coding"));
        if (conditions.Count > 0)
        {
            sb.AppendLine(K("[조건]", "[Conditions]"));
            foreach (var c in conditions) sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        sb.AppendLine(K("[출력 형식]", "[Output format]"));
        bool hasFormat = FormatKeywords.Any(k => original.Contains(k, StringComparison.OrdinalIgnoreCase));
        sb.AppendLine(!hasFormat
            ? a.TaskType switch
            {
                "summary" => K("- 핵심만 3줄 이내로, 부연 설명 없이", "- 3 lines max, no extra explanation"),
                "code" or "refactor" => K("- 1) 변경 요약 2) 수정 코드 3) 확인 방법 순서로", "- 1) summary of the change 2) code 3) how to verify"),
                "document" => K("- 제목 → 본문(소제목 포함) → 요약 한 줄 순서로", "- title → body with subheadings → one-line summary"),
                "research" => K("- 결론 먼저, 근거는 출처와 함께 목록으로", "- conclusion first, then sources as a list"),
                _ => K("- 결론 먼저, 근거는 짧은 목록으로", "- conclusion first, then a short list of reasons"),
            }
            : K("- 지정된 형식대로", "- as specified above"));
        sb.AppendLine(K("- 모르는 부분은 지어내지 말고 \"확인 필요\"라고 표시하세요", "- Do not make things up; mark unknowns as \"needs check\""));
        return sb.ToString().TrimEnd();
    }

    /// <summary>말풍선 전체에서 실제로 대상 도구에 붙여넣을 프롬프트 본문만 추출한다.
    /// 모델 헤더(===== …용 ===== / […용 개선 프롬프트]), 안내문, '— 왜:' 설명, Shadow 칸을 제거하고,
    /// 모델이 여럿이면 첫 번째(최상위 추천) 섹션만 남긴다. 표식이 없으면 원문을 그대로 반환한다.</summary>
    public static string ExtractPromptOnly(string bubbleText)
    {
        if (string.IsNullOrWhiteSpace(bubbleText)) return "";
        var lines = bubbleText.Replace("\r\n", "\n").Split('\n').ToList();
        static bool IsSectionHeader(string l) => Regex.IsMatch(l.Trim(), @"^===== .+ =====$");
        static bool IsPromptHeader(string l) => Regex.IsMatch(l.Trim(), @"^\[(.+용 개선 프롬프트|Improved prompt for .+)\]$");

        // 1) ===== 모델용 ===== 섹션이 있으면 첫 섹션만
        int sec = lines.FindIndex(l => IsSectionHeader(l));
        if (sec >= 0)
        {
            int end = lines.FindIndex(sec + 1, l => IsSectionHeader(l));
            lines = lines.Skip(sec + 1).Take((end < 0 ? lines.Count : end) - sec - 1).ToList();
        }
        // 2) Shadow 칸(채택하지 않은 안)은 버린다
        int shadow = lines.FindIndex(l => l.TrimStart().StartsWith(ShadowMarker, StringComparison.Ordinal));
        if (shadow >= 0) lines = lines.Take(shadow).ToList();
        // 3) […용 개선 프롬프트] 헤더가 있으면 그 뒤 본문만 (LLM 실패·주제 이탈 시 뒤에 붙은 규칙 기반본이 최종본)
        int hdr = lines.FindLastIndex(l => IsPromptHeader(l));
        if (hdr >= 0) lines = lines.Skip(hdr + 1).ToList();

        // 4) 안내문·설명 줄 제거 (괄호 안내문은 한/영 모두)
        lines = lines.Where(l =>
        {
            var t = l.Trim();
            return !t.StartsWith("— 왜:") && !t.StartsWith("— Why:")
                && !t.StartsWith("— 심사:") && !t.StartsWith("— Review:")
                && !(t.StartsWith("(") && t.EndsWith(")") && (t.Contains("Ollama") || t.Contains("LLM") || t.Contains("규칙 기반") || t.Contains("rule-based")));
        }).ToList();

        return string.Join("\n", lines).Trim();
    }

    // ---------- 로컬 LLM 심사 결과 파싱 ----------
    /// <summary>심사 LLM 출력에서 점수(0~100)와 한 줄 이유를 추출. 점수를 못 찾으면 -1.</summary>
    public static (int score, string reason) ParseJudgeResult(string judgeOutput)
    {
        if (string.IsNullOrWhiteSpace(judgeOutput)) return (-1, "");
        var m = Regex.Match(judgeOutput, @"점수\s*[:：]?\s*(\d{1,3})");
        if (!m.Success) m = Regex.Match(judgeOutput, @"(\d{1,3})\s*(?:점|/100)");
        if (!m.Success) return (-1, "");
        int score = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100);
        string reason = judgeOutput.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.Contains(m.Value)) ?? "";
        return (score, reason.Length > 120 ? reason[..120] : reason);
    }

    /// <summary>유사도 0~1: 단어 Jaccard와 문자 3-gram Jaccard 중 큰 값 (한국어 어미 변화·띄어쓰기 차이에 강하게).</summary>
    public static double TextSimilarity(string a, string b)
    {
        static HashSet<string> Tokens(string s) => Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}_]+")
            .Where(w => w.Length >= 2).ToHashSet();
        static HashSet<string> Grams(string s)
        {
            var t = Regex.Replace(s.ToLowerInvariant(), @"\s+", "");
            return t.Length < 3 ? new HashSet<string> { t } : Enumerable.Range(0, t.Length - 2).Select(i => t.Substring(i, 3)).ToHashSet();
        }
        static double Jac(HashSet<string> x, HashSet<string> y) =>
            x.Count == 0 || y.Count == 0 ? 0 : (double)x.Intersect(y).Count() / x.Union(y).Count();
        return Math.Max(Jac(Tokens(a), Tokens(b)), Jac(Grams(a), Grams(b)));
    }

    // ---------- 폴더 일괄 이미지 처리 리포트 (단계 추적 + 토큰 패턴 피드백) ----------
    /// <summary>스캔 결과만으로 단계 로그와 패턴 피드백을 만든다. LLM을 쓰지 않아 주제가 벗어나지 않는다.</summary>
    public static string ImageBatchReport(string folder, List<(string Name, string Ext, long Bytes)> files, int copied, int calibSamples, double calibErrorPct)
    {
        var sb = new System.Text.StringBuilder();
        long totalBytes = files.Sum(f => f.Bytes);
        var large = files.Where(f => f.Bytes > 2_000_000).OrderByDescending(f => f.Bytes).ToList();
        long estTokens = files.Count * 1100L;

        sb.AppendLine("[1/4] 폴더 스캔");
        sb.AppendLine($"  대상: {folder}");
        sb.AppendLine($"  이미지 {files.Count}장 발견 — {string.Join(", ", files.GroupBy(f => f.Ext).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
        sb.AppendLine($"  총 용량 {totalBytes / 1_000_000.0:F1}MB (평균 {files.Average(f => f.Bytes) / 1000.0:F0}KB/장)");
        sb.AppendLine("[2/4] 첨부 복사");
        sb.AppendLine($"  데이터 폴더 attachments에 {copied}장 복사 완료 (원본 무수정)");
        sb.AppendLine("[3/4] 토큰 추정 (장당 ~1,100 tok 근사)");
        sb.AppendLine($"  전체 예상 ~{estTokens:N0} tok · 큰 이미지(2MB 초과) {large.Count}장");
        foreach (var f in large.Take(5)) sb.AppendLine($"   - {f.Name} ({f.Bytes / 1_000_000.0:F1}MB)");
        sb.AppendLine("[4/4] 패턴 피드백");
        if (large.Count > 0)
            sb.AppendLine($"  - 큰 이미지가 {large.Count}장({100.0 * large.Count / Math.Max(1, files.Count):F0}%)입니다. 보내기 전에 너비 1600px 이하로 줄이면 토큰·시간을 크게 아낍니다.");
        if (estTokens > 20000)
            sb.AppendLine($"  - 한 번에 {files.Count}장(~{estTokens:N0} tok)은 컨텍스트 부담이 큽니다. 대표 이미지 3~5장만 추려 보내는 패턴을 권장합니다.");
        if (files.Count >= 10)
            sb.AppendLine("  - 장수가 많으면 '전체를 한 번에 설명'보다 '이미지마다 한 줄 소견 → 표로 정리' 형식이 정확도가 높습니다.");
        if (files.Any(f => f.Ext is ".png") && files.Count(f => f.Ext is ".png") >= files.Count / 2)
            sb.AppendLine("  - PNG(스크린샷) 비중이 높습니다. 글자 판독이 목적이면 원본, 장면 이해가 목적이면 JPG로 줄여도 됩니다.");
        sb.AppendLine(calibSamples > 0
            ? $"  - 현재 예측 보정 표본 {calibSamples}건, 평균 오차 ±{calibErrorPct:F0}% — 실제 전송 후 가져오기를 하면 이 수치가 개선됩니다."
            : "  - 아직 예측-실측 표본이 없습니다. 이 배치를 실제로 보낸 뒤 [가져오기]를 하면 다음 예측부터 보정이 걸립니다.");
        sb.AppendLine("  - 이미지 이해가 핵심이면 Gemini 4 / ChatGPT(GPT-6) / Claude 계열처럼 비전 강점 모델이 유리합니다 (모델 추천의 image 유형 참고).");
        return sb.ToString().TrimEnd();
    }

    // ---------- 주간 낭비 리포트 ----------
    public static string WeeklyReport(List<PromptRow> all, DateTime now)
    {
        var week = all.Where(p => DateTime.TryParse(p.CreatedAt, out var d) && d >= now.AddDays(-7)).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📊 최근 7일 리포트 ({now.AddDays(-7):MM/dd} ~ {now:MM/dd})");
        if (week.Count == 0)
        {
            // 시각 정보 없는 출처(Cursor 등)가 많으면 전체 기준으로 대체
            week = all;
            sb.AppendLine("(날짜가 있는 기록이 없어 전체 기간 기준으로 표시합니다)");
        }
        long inTok = week.Sum(p => p.InTok), outTok = week.Sum(p => p.OutTok);
        double failRate = week.Count > 0 ? 100.0 * week.Count(p => p.Fail > 0) / week.Count : 0;
        sb.AppendLine($"프롬프트 {week.Count:N0}건 · 입력 {inTok:N0} tok · 출력 {outTok:N0} tok · 실패신호 비율 {failRate:F0}%");
        sb.AppendLine($"출처: {string.Join(", ", week.GroupBy(p => p.Source).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
        sb.AppendLine();
        sb.AppendLine("⛔ 토큰을 가장 많이 쓴 프롬프트 TOP 5 (줄일 여지)");
        foreach (var p in week.OrderByDescending(p => p.TotalTok).Take(5))
            sb.AppendLine($"- [{p.Source}] {p.Preview}  → {p.TotalTok:N0} tok, 턴 {p.SegTurns}, 실패 {p.Fail}");
        sb.AppendLine();
        sb.AppendLine("✨ 가장 잘 먹힌 프롬프트 TOP 3 (이렇게 쓰세요)");
        foreach (var p in week.Where(p => p.EffectiveLabel.Contains("최적")).OrderByDescending(p => p.Score).Take(3))
            sb.AppendLine($"- [{p.Source}] {p.Preview}");
        sb.AppendLine();
        var worst = week.Where(p => p.Fail > 0).ToList();
        sb.AppendLine(failRate >= 20
            ? $"💡 조언: 실패신호 비율이 {failRate:F0}%로 높습니다. 보내기 전에 파일 경로·출력 형식을 한 줄씩만 더 적어도 정정 턴이 크게 줍니다."
            : "💡 조언: 실패신호 비율이 안정적입니다. 최악 TOP 5의 공통점(긴 붙여넣기 등)만 가끔 점검하세요.");
        if (worst.Count > 0)
            sb.AppendLine($"   실패가 있었던 프롬프트 {worst.Count}건의 누적 토큰은 {worst.Sum(p => p.TotalTok):N0} tok 입니다.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>점수 산출 근거를 사람이 읽을 문장으로 (이력 미리보기 표시용).</summary>
    public static string ScoreBreakdown(PromptRow r, double medianTotal)
    {
        var parts = new List<string> { $"점수 {r.Score:F0}" };
        if (r.Fail > 0) parts.Add($"실패신호 {r.Fail}회 (−{45 * r.Fail})");
        if (medianTotal > 0 && r.TotalTok > medianTotal * 3) parts.Add($"토큰 과다 (중앙값의 {r.TotalTok / medianTotal:F1}배, −25)");
        else if (medianTotal > 0 && r.TotalTok > medianTotal * 1.5) parts.Add("토큰 다소 많음 (−10)");
        if (r.Source == "claude" && r.SegTurns > 0 && r.OutTok == 0) parts.Add("답 없이 종료 (−20)");
        if (r.Text.Length < 15) parts.Add("초단문 (−10)");
        if (r.Label != 0) parts.Add("수동 라벨 우선 적용");
        else if (r.AutoLabel == "good") parts.Add("자동: 상위권");
        else if (r.AutoLabel == "bad") parts.Add("자동: 하위권");
        return string.Join(" · ", parts);
    }

    /// <summary>모델별 실측 표본으로 outputFactor를 자동 보정. 변경 목록 반환 (표본 3건 미만·변화 미미하면 제외).</summary>
    public static (List<ModelInfo> models, List<string> changes) AutoAdjustFactors(
        List<ModelInfo> models, List<(string model, long estOut, long actOut)> rows)
    {
        var changes = new List<string>();
        var updated = models.Select(m =>
        {
            var mine = rows.Where(r => r.model == m.Name && r.estOut > 0 && r.actOut > 0)
                           .Select(r => (double)r.actOut / r.estOut).OrderBy(x => x).ToList();
            if (mine.Count < 3) return m;
            double median = mine[mine.Count / 2];
            double next = Math.Clamp(m.OutputFactor * median, 0.4, 2.0);
            if (Math.Abs(next - m.OutputFactor) < 0.05) return m;
            changes.Add($"{m.Name}: outputFactor {m.OutputFactor:F2} → {next:F2} (표본 {mine.Count}건)");
            return m with { OutputFactor = Math.Round(next, 2) };
        }).ToList();
        return (updated, changes);
    }

    public static void SaveModels(List<ModelInfo> models)
    {
        var path = Path.Combine(Store.DataDir, "models.json");
        File.WriteAllText(path, JsonSerializer.Serialize(models,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    // ---------- 지식 내보내기 (Phase 1: STATUS + 지식 카드) ----------
    /// <summary>
    /// 지식 카드 · MCP 결과 · crash.log · LLM 전송 전에 개인정보성 패턴을 가린다.
    /// 경로(드라이브 · UNC · 홈), 메일, API 키 · 토큰(OpenAI · Anthropic · GitHub · Google · AWS · Slack · JWT · Bearer),
    /// 개인키 블록, 비밀번호 대입, IPv4, 전화번호, 주민번호 꼴, 긴 해시.
    /// </summary>
    public static string MaskSensitive(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = text;
        t = Regex.Replace(t, @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", "(개인키 마스킹)");
        t = Regex.Replace(t, @"\b(?:sk-(?:ant-)?[A-Za-z0-9\-_]{8,}|gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}|AIza[0-9A-Za-z\-_]{20,}|GOCSPX-[A-Za-z0-9\-_]{8,}|AKIA[A-Z0-9]{12,}|xox[abprs]-[A-Za-z0-9\-]{8,})", "(키 마스킹)");
        t = Regex.Replace(t, @"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", "(토큰 마스킹)");
        t = Regex.Replace(t, @"(?i)\b(bearer)\s+[A-Za-z0-9\-_\.=]{12,}", "$1 (토큰 마스킹)");
        t = Regex.Replace(t, @"(?i)\b(password|passwd|pwd|secret|api[_-]?key|token|비밀번호|암호)\s*[:=]\s*\S+", "$1=(마스킹)");
        t = Regex.Replace(t, @"\\\\[A-Za-z0-9_.\-$]+\\[^\s""'<>|]+", "(공유 경로)");
        t = Regex.Replace(t, @"[A-Za-z]:\\(?:Users|사용자)\\[^\\\s""']+", "%USERPROFILE%", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"(?<![\w%])[A-Za-z]:\\(?:[^\\\s""'<>|]+\\)*", "(경로)\\");
        t = Regex.Replace(t, @"(?<![\w.])/(?:home|Users)/[^/\s]+", "~");
        t = Regex.Replace(t, @"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)*\.[A-Za-z]{2,}", "(이메일)");
        t = Regex.Replace(t, @"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b", "(IP)");
        t = Regex.Replace(t, @"\b01[016789][- ]?\d{3,4}[- ]?\d{4}\b", "(전화번호)");
        t = Regex.Replace(t, @"\b\d{6}-[1-4]\d{6}\b", "(주민번호)");
        t = Regex.Replace(t, @"\b[A-Fa-f0-9]{32,}\b", "(해시 마스킹)");
        return t;
    }

    /// <summary>지식을 내보낼 repo 폴더 탐지: 지정값 우선, 없으면 실행 위치·현재 폴더에서 상위로 올라가며 csproj+.git이 있는 폴더.</summary>
    public static string? FindRepoDir(string preferred)
    {
        if (!string.IsNullOrEmpty(preferred) && Directory.Exists(preferred)) return preferred;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "PromptAdvisor.csproj")) && Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
        }
        return null;
    }

    private static double Pct(int part, int total) => total == 0 ? 0 : 100.0 * part / total;
    private static string AutoGenHeader(DateTime now, string what) =>
        $"<!-- AUTO-GENERATED by PromptAdvisor · {what} · 생성 {now:yyyy-MM-dd HH:mm} · 직접 수정 금지 (앱의 [📤 지식 내보내기]로 갱신) -->\n";

    /// <summary>docs/STATUS.md 본문 — 에이전트가 세션 시작 시 현재 상황을 파악하는 자동 스냅샷.</summary>
    public static string BuildStatusReport(List<PromptRow> all, List<ModelInfo> models,
        List<(string task, long estOut, long actOut)> calib, int errorCount, int embeddedCount, DateTime now)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(AutoGenHeader(now, "STATUS"));
        sb.AppendLine("# PromptAdvisor 현재 현황 (STATUS)");
        sb.AppendLine();
        int total = all.Count;
        long inTok = all.Sum(p => p.InTok), outTok = all.Sum(p => p.OutTok);
        int failRows = all.Count(p => p.Fail > 0);
        int good = all.Count(p => p.EffectiveLabel.Contains("최적")), bad = all.Count(p => p.EffectiveLabel.Contains("최악"));
        var recent = all.Where(p => DateTime.TryParse(p.CreatedAt, out var d) && d >= now.AddDays(-7)).ToList();
        sb.AppendLine("## 핵심 지표");
        sb.AppendLine($"- 프롬프트 {total:N0}건 · 세션 {all.Select(p => p.Session).Distinct().Count():N0}개 · 누적 토큰 입력 {inTok:N0} / 출력 {outTok:N0}");
        sb.AppendLine($"- 실패신호 비율 {Pct(failRows, total):F0}% ({failRows:N0}건) · 라벨 최적 {good:N0} / 최악 {bad:N0}");
        sb.AppendLine($"- 최근 7일 {recent.Count:N0}건 (날짜 없는 출처는 제외) · 임베딩 {embeddedCount:N0}/{total:N0}건 · 오류 누적 {errorCount}건");
        sb.AppendLine($"- 예측-실측 보정 표본 {calib.Count}건" + (calib.Count > 0 ? $" · 평균 오차 ±{PredictionErrorPct(calib):F0}%" : " (아직 없음 — 분석→실제 사용→가져오기 루프 필요)"));
        sb.AppendLine();
        sb.AppendLine("## 출처별");
        foreach (var g in all.GroupBy(p => p.Source).OrderByDescending(g => g.Count()))
            sb.AppendLine($"- {g.First().DisplaySource}: {g.Count():N0}건 · {g.Sum(p => p.TotalTok):N0} tok");
        int projCount = all.Where(p => !string.IsNullOrWhiteSpace(p.Project)).Select(p => p.Project).Distinct().Count();
        if (projCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## 프로젝트: {projCount}개 (이름은 내보내지 않음)");
        }
        sb.AppendLine();
        sb.AppendLine("## 모델표 (models.json 기준)");
        foreach (var m in models)
            sb.AppendLine($"- {m.Name} ({m.Tool}) · outputFactor {m.OutputFactor:F2} · 강점 {string.Join("/", m.Strengths)}");
        sb.AppendLine();
        sb.AppendLine("## 에이전트 참고");
        sb.AppendLine("- 작업 판단 전 `docs/knowledge/`의 지식 카드(패턴·실패·모델·선호)를 함께 읽을 것.");
        sb.AppendLine("- 이 파일이 7일 이상 오래됐으면 사용자에게 앱 설정 탭의 [📤 지식 내보내기] 실행을 제안할 것.");
        sb.AppendLine("- 프롬프트 원문은 이 repo에 없다 (로컬 DB 전용). 원문이 필요하면 앱 이력 탭을 안내할 것.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>docs/knowledge/*.md 지식 카드 4종. 통계·비율 중심이며 예시는 마스킹 후 짧게만 넣는다.</summary>
    public static List<(string File, string Content)> BuildKnowledgeCards(List<PromptRow> all, List<ModelInfo> models,
        List<(string model, long estOut, long actOut)> calibByModel, IReadOnlyList<string> favoriteModels, string selectedModel, DateTime now)
    {
        var good = all.Where(p => p.EffectiveLabel.Contains("최적")).ToList();
        var bad = all.Where(p => p.EffectiveLabel.Contains("최악")).ToList();
        var failed = all.Where(p => p.Fail > 0).ToList();

        double Ratio(List<PromptRow> rows, Func<PromptRow, bool> f) => rows.Count == 0 ? 0 : Pct(rows.Count(f), rows.Count);
        bool HasPath(PromptRow p) => FilePathRegex().IsMatch(p.Text);
        bool HasFmt(PromptRow p) => FormatKeywords.Any(k => p.Text.Contains(k, StringComparison.OrdinalIgnoreCase));

        var patterns = new System.Text.StringBuilder();
        patterns.Append(AutoGenHeader(now, "지식 카드: 잘 먹힌 패턴"));
        patterns.AppendLine("# 잘 먹힌 프롬프트 패턴");
        patterns.AppendLine();
        patterns.AppendLine($"표본: 최적 {good.Count:N0}건 vs 최악 {bad.Count:N0}건 (자동+수동 라벨)");
        patterns.AppendLine();
        patterns.AppendLine("## 최적 vs 최악의 차이 (비율)");
        patterns.AppendLine($"- 파일 경로 포함: 최적 {Ratio(good, HasPath):F0}% vs 최악 {Ratio(bad, HasPath):F0}%");
        patterns.AppendLine($"- 출력 형식 지정: 최적 {Ratio(good, HasFmt):F0}% vs 최악 {Ratio(bad, HasFmt):F0}%");
        patterns.AppendLine($"- 평균 길이: 최적 {(good.Count > 0 ? good.Average(p => (double)p.Text.Length) : 0):F0}자 vs 최악 {(bad.Count > 0 ? bad.Average(p => (double)p.Text.Length) : 0):F0}자");
        patterns.AppendLine($"- 실패신호 동반: 최적 {Ratio(good, p => p.Fail > 0):F0}% vs 최악 {Ratio(bad, p => p.Fail > 0):F0}%");
        patterns.AppendLine();
        // 원문 · 프로젝트명은 넣지 않는다. 작업 유형별로 잘 먹힌 요청의 "구조"만 통계로 남긴다.
        patterns.AppendLine("## 작업 유형별 잘 먹힌 요청의 구조 (원문 없음)");
        foreach (var g in good.GroupBy(p => Classify(p.Text)).OrderByDescending(g => g.Count()).Take(5))
        {
            var rows = g.ToList();
            patterns.AppendLine($"- {TaskLabel(g.Key)}: {rows.Count}건 · 평균 {rows.Average(p => (double)p.Text.Length):F0}자 · 파일 경로 {Ratio(rows, HasPath):F0}% · 출력 형식 {Ratio(rows, HasFmt):F0}% · 장비 정보 완비 {Ratio(rows, p => Equipment.IsEquipmentRequest(p.Text) && Equipment.Missing(p.Text).Count == 0):F0}%");
        }
        if (good.Count == 0) patterns.AppendLine("- (아직 최적 표본이 없습니다. 가져오기 후 자동 라벨이 쌓이면 채워집니다)");

        var failures = new System.Text.StringBuilder();
        failures.Append(AutoGenHeader(now, "지식 카드: 실패 유형"));
        failures.AppendLine("# 실패 유형과 회피법");
        failures.AppendLine();
        failures.AppendLine($"실패신호가 있었던 프롬프트 {failed.Count:N0}건 / 전체 {all.Count:N0}건 ({Pct(failed.Count, all.Count):F0}%)");
        failures.AppendLine();
        failures.AppendLine("## 실패 프롬프트에서 자주 걸린 린트 규칙");
        var ruleHits = failed.SelectMany(p => Lint(p.Text).Select(l => l.Rule)).GroupBy(r => r)
            .OrderByDescending(g => g.Count()).Take(6).ToList();
        foreach (var g in ruleHits) failures.AppendLine($"- {g.Key}: 실패건의 {Pct(g.Count(), Math.Max(1, failed.Count)):F0}%에서 검출");
        if (ruleHits.Count == 0) failures.AppendLine("- (검출 없음)");
        failures.AppendLine();
        failures.AppendLine("## 회피법");
        failures.AppendLine("- 보내기 전 파일 경로·원하는 결과 한 줄·출력 형식을 먼저 적으면 정정 턴이 크게 준다 (앱 린트와 동일 기준).");
        failures.AppendLine("- 로그 원문은 핵심 예외 3~5줄만, 큰 붙여넣기는 관련 구간만 발췌한다.");
        failures.AppendLine("- 작업이 여러 개면 나눠 보내고, 하나씩 확인한다.");

        var modelCard = new System.Text.StringBuilder();
        modelCard.Append(AutoGenHeader(now, "지식 카드: 모델 실측"));
        modelCard.AppendLine("# 모델 실측 지식");
        modelCard.AppendLine();
        modelCard.AppendLine("## 카탈로그와 실측 보정 상태");
        foreach (var m in models)
        {
            var mine = calibByModel.Where(r => r.model == m.Name).ToList();
            modelCard.AppendLine(mine.Count > 0
                ? $"- {m.Name}: outputFactor {m.OutputFactor:F2} · 실측 표본 {mine.Count}건 · 평균 오차 ±{PredictionErrorPct(mine.Select(r => (task: "", r.estOut, r.actOut))):F0}%"
                : $"- {m.Name}: outputFactor {m.OutputFactor:F2} · 실측 표본 없음 (카탈로그 기본값 사용 중)");
        }
        modelCard.AppendLine();
        modelCard.AppendLine("## 작업 유형별 표본 분포");
        foreach (var g in all.Where(p => p.TotalTok > 0).GroupBy(p => Classify(p.Text)).OrderByDescending(g => g.Count()).Take(6))
            modelCard.AppendLine($"- {TaskLabel(g.Key)}: {g.Count():N0}건 · 평균 {g.Average(p => (double)p.TotalTok):N0} tok · 실패율 {Pct(g.Count(p => p.Fail > 0), g.Count()):F0}%");

        var prefs = new System.Text.StringBuilder();
        prefs.Append(AutoGenHeader(now, "지식 카드: 사용자 선호"));
        prefs.AppendLine("# 사용자 선호 (관측 기반)");
        prefs.AppendLine();
        prefs.AppendLine($"- 지정 모델: {(string.IsNullOrEmpty(selectedModel) ? "자동 추천" : selectedModel)} · 즐겨찾기 모델: {(favoriteModels.Count > 0 ? string.Join(", ", favoriteModels) : "없음")}");
        prefs.AppendLine("- 자주 쓰는 작업 유형: " + string.Join(", ",
            all.GroupBy(p => Classify(p.Text)).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{TaskLabel(g.Key)} {Pct(g.Count(), all.Count):F0}%")));
        prefs.AppendLine("- 자주 쓰는 도구: " + string.Join(", ",
            all.GroupBy(p => p.DisplaySource).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} {Pct(g.Count(), all.Count):F0}%")));
        prefs.AppendLine($"- 좋은 결과에서 출력 형식 지정 비율 {Ratio(good, HasFmt):F0}% — 형식 미지정이면 짧은 목록/결론 먼저 형식을 기본 제안할 것.");
        prefs.AppendLine("- 답변 언어는 한국어. 코드 작업은 변경 요약 → 수정 코드 → 확인 방법 순서를 선호하는 것으로 관측됨.");

        return new List<(string, string)>
        {
            ("patterns.md", patterns.ToString().TrimEnd()),
            ("failures.md", failures.ToString().TrimEnd()),
            ("models.md", modelCard.ToString().TrimEnd()),
            ("preferences.md", prefs.ToString().TrimEnd()),
        };
    }

    /// <summary>docs/STATUS.md + knowledge 카드 4종을 repo에 쓴다. UI·--export-knowledge 공용.</summary>
    public static (int prompts, List<(string File, string Content)> cards) WriteKnowledgeExport(
        string repoDir, Store store, IReadOnlyList<string> favoriteModels, string selectedModel, DateTime now)
    {
        var all = store.All();
        var models = LoadModels();
        var knowledge = Path.Combine(repoDir, "docs", "knowledge");
        Directory.CreateDirectory(knowledge);
        File.WriteAllText(Path.Combine(repoDir, "docs", "STATUS.md"),
            BuildStatusReport(all, models, store.CalibrationRows(), store.ErrorCount(), store.CountEmbedded(), now));
        var cards = BuildKnowledgeCards(all, models, store.CalibrationRowsByModel(), favoriteModels, selectedModel, now);
        foreach (var (file, content) in cards)
            File.WriteAllText(Path.Combine(knowledge, file), content);
        return (all.Count, cards);
    }

    /// <summary>MCP recommend_prompt · 프로젝트/세션 맥락 + 린트·모델·규칙 기반 개선안을 한 덩어리 텍스트로 (Ollama 없음).</summary>
    public static string BuildRecommendText(
        string prompt, Store store, string? pinnedModel, int recommendCount,
        string? projectDir = null, string? goalOverride = null, IEnumerable<string>? filesOverride = null)
    {
        string resolvedDir = !string.IsNullOrWhiteSpace(projectDir) ? projectDir : FindRepoDir(App.Settings.ActiveProjectDir.Length > 0 ? App.Settings.ActiveProjectDir : App.Settings.KnowledgeDir) ?? "";
        string projName = !string.IsNullOrWhiteSpace(resolvedDir) ? Path.GetFileName(resolvedDir.TrimEnd('\\', '/')) : App.Settings.ActiveProjectName;
        var projectHistory = !string.IsNullOrWhiteSpace(projName) ? store.Query("all", limit: 250, project: projName) : store.RecentSessionPrompts(projName, 10);
        var ctx = AnalyzePromptContext(prompt, resolvedDir, projName, projectHistory);
        if (!string.IsNullOrWhiteSpace(goalOverride)) ctx.SessionGoal = goalOverride.Trim();
        if (filesOverride != null)
        {
            foreach (var f in filesOverride.Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!ctx.CandidateFiles.Any(c => c.File.Equals(f.Trim(), StringComparison.OrdinalIgnoreCase)))
                    ctx.CandidateFiles.Insert(0, (f.Trim(), "직접 지정", true));
        }

        string task = Classify(prompt, ctx);
        var lints = Lint(prompt, ctx);
        long estIn = EstimateInputTokens(prompt);
        (estIn, _, _) = CalibratedEstimate(estIn, store.InputCalibrationRows(), task);
        var (estOut, factor, samples) = EstimateOutputTokens(task, Array.Empty<NeighborInfo>(), store.CalibrationRows());
        string pinned = !string.IsNullOrEmpty(pinnedModel) ? pinnedModel : "";
        var models = RecommendModels(task, estIn, estOut, LoadModels(), recommendCount, pinned.Length == 0 ? null : pinned, store.CalibrationRowsByModelTask());
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"작업 유형: {TaskLabel(task)} · 예상 입력 ~{estIn:N0} tok · 예상 출력 ~{estOut:N0} tok (보정 ×{factor:F2}, 표본 {samples}건)");
        if (!string.IsNullOrWhiteSpace(ctx.ProjectName) || ctx.SelectedFiles.Count > 0)
            sb.AppendLine($"추론 맥락: {ctx.WhyInferred}" + (ctx.SelectedFiles.Count > 0 ? $" · 후보 파일 [{string.Join(", ", ctx.SelectedFiles)}]" : ""));
        sb.AppendLine(lints.Count == 0 ? "린트: 깨끗합니다." : $"린트 {lints.Count}건 (예상 낭비 {lints.Sum(l => l.WasteTokens):N0} tok):");
        foreach (var l in lints) sb.AppendLine($"- [{l.Rule}] {l.Message}");
        sb.AppendLine("모델 추천:");
        foreach (var r in models)
            sb.AppendLine($"{r.Rank}. {r.Model.Name} — 적합도 {r.Fit}, 예상 {r.EstTotalTokens:N0} tok, ${r.Cost:F3} · {r.Reason}");
        var a = new Analysis { TaskType = task, Lints = lints, EstInputTokens = estIn, EstOutputTokens = estOut, Context = ctx };
        sb.AppendLine();
        sb.AppendLine(BuildImprovedPrompt(prompt, a, models.FirstOrDefault()?.Model.Name ?? "추천 모델"));
        return sb.ToString().TrimEnd();
    }

    /// <summary>스킬 임베딩 후보 사전 선별: 질의 단어와 이름·설명 겹침이 큰 순. 전부 임베딩하면 첫 분석이 느려져 상위만 추린다.</summary>
    public static List<SkillInfo> PickSkillCandidates(List<SkillInfo> skills, string query, int take = 8)
    {
        var words = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9가-힣]{2,}")
            .Select(m => m.Value).Distinct().ToList();
        return skills
            .Select(s => (s, score: words.Count(w => (s.Name + " " + s.Description).ToLowerInvariant().Contains(w))))
            .OrderByDescending(x => x.score).ThenBy(x => x.s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take).Select(x => x.s).ToList();
    }

    // ---------- 스킬 스캔 ----------
    public static List<SkillInfo> ScanSkills()
    {
        var skills = new List<SkillInfo>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, ".claude", "skills"),
            Path.Combine(home, ".claude", "plugins"),
            Path.Combine(home, ".cursor", "skills"),
            Path.Combine(home, ".agents", "skills"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.GetFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    string name = Path.GetFileName(Path.GetDirectoryName(file) ?? ""), desc = "";
                    var m = Regex.Match(text, @"(?s)^---\s*(.*?)---");
                    if (m.Success)
                    {
                        var nm = Regex.Match(m.Groups[1].Value, @"(?m)^name:\s*(.+)$");
                        var dm = Regex.Match(m.Groups[1].Value, @"(?m)^description:\s*(.+)$");
                        if (nm.Success) name = nm.Groups[1].Value.Trim();
                        if (dm.Success) desc = dm.Groups[1].Value.Trim();
                    }
                    if (desc.Length == 0) desc = text.Length > 300 ? text[..300] : text;
                    skills.Add(new SkillInfo(name, desc, file));
                }
                catch { }
            }
        }
        return skills.GroupBy(s => s.Name).Select(g => g.First()).ToList();
    }
}
