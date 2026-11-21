using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PromptAdvisor;

/// <summary>가져오기 결과: 행 + 형식 경고(몇 번째 줄 · 이유). 경고가 많으면 원본 형식이 바뀐 것으로 보고 안내한다.</summary>
public sealed class ImportReport
{
    public string Name = "";
    public List<PromptRow> Rows = new();
    public List<string> Warnings = new();
    public int Files, Lines, BadLines;
    public bool FormatChanged;
    private const int MaxListed = 8;

    public void Bad(string where, int line, string reason)
    {
        BadLines++;
        if (BadLines <= MaxListed) Warnings.Add(L.T($"{where} {line}번째 줄: {reason}", $"{where} line {line}: {reason}"));
    }

    /// <summary>읽은 줄이 있는데 결과가 0건이거나 깨진 줄 비율이 20%를 넘으면 형식 변경으로 판단.</summary>
    public void Finish(string what)
    {
        if (BadLines > MaxListed) Warnings.Add(L.T($"… 외 {BadLines - MaxListed}줄", $"… and {BadLines - MaxListed} more lines"));
        FormatChanged = Lines > 20 && (Rows.Count == 0 || BadLines > Lines * 0.2);
        if (FormatChanged)
            Warnings.Insert(0, L.T(
                $"{what} 기록 형식이 바뀐 것 같습니다 (읽은 줄 {Lines:N0}, 해석 실패 {BadLines:N0}, 추출 {Rows.Count:N0}건). 앱을 업데이트하거나 이슈로 알려 주세요.",
                $"The {what} log format seems to have changed ({Lines:N0} lines read, {BadLines:N0} unreadable, {Rows.Count:N0} extracted). Update the app or report an issue."));
    }
}

/// <summary>로컬 원천들 → PromptRow 추출. 네트워크 없음, 읽기 전용.</summary>
public static partial class Importers
{
    [GeneratedRegex(@"[A-Za-z]:\\(?:\\|[A-Za-z0-9_\-\. ]+\\)+[A-Za-z0-9_\-\.]+")]
    private static partial Regex WinPathRegex();

    /// <summary>경로(cwd) 또는 Claude 프로젝트 슬러그에서 프로젝트 이름을 추출한다.</summary>
    public static string ProjectNameFrom(string? cwd, string? claudeSlug = null)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var trimmed = cwd.Trim().TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        if (!string.IsNullOrWhiteSpace(claudeSlug))
        {
            var s = claudeSlug.Trim();
            // 슬러그 prefix(예: d--src-MyApp, C--Users-foo-source-repos-MyApp)에서 흔한 상위 폴더 제거
            var stripped = Regex.Replace(s, @"^[A-Za-z]--(?:Users-[^-]+-)?(?:source-repos-|src-|dev-|Git-|repos-|Projects-|Desktop-|Documents-|work-)?", "", RegexOptions.IgnoreCase);
            if (!string.IsNullOrWhiteSpace(stripped) && stripped != s) return stripped;
            var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts[^1];
        }
        return "";
    }

    private static readonly string[] RepoParents = { "repos", "Projects", "source", "workspace" };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dirRootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>파일/텍스트 내 로컬 경로에서 워크스페이스 루트(.git 또는 .csproj/.sln 상위)나 프로젝트명을 추정.</summary>
    public static string ProjectFromRawJson(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson)) return "";
        var slice = rawJson.Length > 8000 ? rawJson[..8000] : rawJson;
        foreach (Match m in WinPathRegex().Matches(slice))
        {
            var p = m.Value.Replace(@"\\", @"\");
            if (p.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
                || p.Contains(@"\.cursor\", StringComparison.OrdinalIgnoreCase)
                || p.Contains(@"\.claude\", StringComparison.OrdinalIgnoreCase)
                || p.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) continue;
            // repos/<Project>/... · Projects/<Project>/... 같은 흔한 작업 폴더 패턴이면 디스크 조회 없이 즉시 추출
            var segs = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length - 2; i++)
            {
                if (RepoParents.Contains(segs[i], StringComparer.OrdinalIgnoreCase) && !segs[i + 1].Contains('.'))
                    return segs[i + 1];
            }
            // 실제 디스크에 있으면 .git / .csproj 루트까지 올라가서 폴더명 반환 (캐시 적용)
            try
            {
                var startDir = Path.GetDirectoryName(p) ?? p;
                if (_dirRootCache.TryGetValue(startDir, out var cached))
                {
                    if (cached.Length > 0) return cached;
                    continue;
                }
                var dir = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p).Directory;
                string resolved = "";
                for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                        || File.Exists(Path.Combine(dir.FullName, "package.json"))
                        || File.Exists(Path.Combine(dir.FullName, "pyproject.toml"))
                        || Directory.GetFiles(dir.FullName, "*.csproj").Length > 0)
                    {
                        resolved = dir.Name;
                        break;
                    }
                }
                _dirRootCache[startDir] = resolved;
                if (resolved.Length > 0) return resolved;
            }
            catch { }
        }
        return "";
    }

    /// <summary>로컬 Claude Code 세션들에서 실제 존재하는 프로젝트 폴더 목록을 최근 사용순으로 수집.</summary>
    public static List<(string Name, string Dir)> DiscoverProjectDirs(string? preferredDir = null)
    {
        var found = new Dictionary<string, (string Name, string Dir, DateTime LastWrite)>(StringComparer.OrdinalIgnoreCase);
        void AddDir(string? d, DateTime ts)
        {
            if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) return;
            var full = Path.GetFullPath(d.Trim().TrimEnd('\\', '/'));
            var name = Path.GetFileName(full);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!found.TryGetValue(full, out var prev) || ts > prev.LastWrite)
                found[full] = (name, full, ts);
        }

        var repoDir = Advisor.FindRepoDir(preferredDir ?? "");
        if (repoDir != null) AddDir(repoDir, DateTime.Now);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (Directory.Exists(root))
        {
            foreach (var projFolder in Directory.GetDirectories(root))
            {
                var jsonls = Directory.GetFiles(projFolder, "*.jsonl", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(3);
                foreach (var jf in jsonls)
                {
                    var lw = File.GetLastWriteTime(jf);
                    try
                    {
                        foreach (var line in File.ReadLines(jf).Take(15))
                        {
                            if (!line.Contains("\"cwd\"")) continue;
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                            {
                                AddDir(cwdEl.GetString(), lw);
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        return found.Values.OrderByDescending(x => x.LastWrite).Select(x => (x.Name, x.Dir)).ToList();
    }
    private static readonly string[] CorrectionPrefixes =
        { "아니", "그게 아니라", "다시 해", "다시해", "틀렸", "잘못", "아니라", "아닌데", "그거 말고",
          "no,", "no ", "nope", "wrong", "that's wrong", "that is wrong", "not what i", "try again", "redo", "still not", "still broken", "it still" };

    public static bool IsCorrection(string text)
    {
        var t = text.TrimStart();
        return CorrectionPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractUserText(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var ty)) continue;
            if (ty.GetString() == "tool_result") return null; // 도구 결과는 사용자 프롬프트가 아님
            if (ty.GetString() == "text" && block.TryGetProperty("text", out var tx))
                parts.Add(tx.GetString() ?? "");
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string ExtractAssistantText(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
            if (block.TryGetProperty("type", out var ty) && ty.GetString() == "text"
                && block.TryGetProperty("text", out var tx)) parts.Add(tx.GetString() ?? "");
        return string.Join("\n", parts);
    }

    private static bool IsSkippablePrompt(string text)
    {
        var t = text.TrimStart();
        return t.Length == 0
            || t.StartsWith("<command-name>") || t.StartsWith("<command-message>")
            || t.StartsWith("<local-command") || t.StartsWith("Caveat:")
            || t.StartsWith("<system-reminder>") || t.StartsWith("[Request interrupted")
            || t.StartsWith("<task-notification>") || t.StartsWith("<task-id>")
            || t.StartsWith("This session is being continued");
    }

    /// <summary>~/.claude/projects/**/*.jsonl — 구간(프롬프트→다음 프롬프트) 단위로 토큰 합산. root 는 테스트용.</summary>
    public static ImportReport ClaudeCode(IProgress<string>? progress = null, string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var rep = new ImportReport { Name = "Claude Code" };
        var rows = rep.Rows;
        if (!Directory.Exists(root)) { rep.Warnings.Add(L.T("Claude Code 기록 폴더가 없습니다 (~/.claude/projects).", "No Claude Code log folder (~/.claude/projects).")); return rep; }
        var files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
        rep.Files = files.Length;
        int done = 0, typed = 0;
        foreach (var file in files)
        {
            progress?.Report(L.T($"Claude Code {++done}/{files.Length} 파일…", $"Claude Code file {done}/{files.Length}…"));
            string session = Path.GetFileNameWithoutExtension(file);
            // 상위 폴더 슬러그를 기본 프로젝트명으로 쓰고, jsonl 내 cwd가 있으면 실제 폴더명으로 덮어씀
            string rel = Path.GetRelativePath(root, file);
            string slug = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string project = ProjectNameFrom(null, slug);
            PromptRow? cur = null;
            void Flush()
            {
                if (cur != null && cur.Text.Length > 0)
                {
                    if (string.IsNullOrEmpty(cur.Project)) cur.Project = project;
                    rows.Add(cur);
                }
                cur = null;
            }

            int lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (line.Length < 10) continue;
                rep.Lines++;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { rep.Bad(Path.GetFileName(file), lineNo, L.T("JSON 으로 읽을 수 없음", "not valid JSON")); continue; }
                using var _ = doc;
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) { rep.Bad(Path.GetFileName(file), lineNo, L.T("객체가 아님", "not a JSON object")); continue; }
                if (e.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                {
                    var fromCwd = ProjectNameFrom(cwdEl.GetString(), slug);
                    if (!string.IsNullOrEmpty(fromCwd))
                    {
                        project = fromCwd;
                        if (cur != null) cur.Project = project;
                    }
                }
                if (!e.TryGetProperty("type", out var typeEl)) continue;
                string type = typeEl.GetString() ?? "";
                if (type is "user" or "assistant") typed++;
                string ts = e.TryGetProperty("timestamp", out var tse) ? tse.GetString() ?? "" : "";

                if (type == "user")
                {
                    if (e.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                    var text = ExtractUserText(e);
                    if (text == null) continue;
                    if (text.Contains("[Request interrupted by user"))
                    {
                        if (cur != null) cur.Fail++;
                        continue;
                    }
                    if (IsSkippablePrompt(text)) continue;
                    if (cur != null && IsCorrection(text)) cur.Fail++;
                    Flush();
                    cur = new PromptRow { Source = "claude", Session = session, Project = project, Text = text.Trim(), CreatedAt = ts };
                }
                else if (type == "assistant" && cur != null)
                {
                    cur.SegTurns++;
                    if (e.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var u))
                    {
                        long Get(string k) => u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                        cur.InTok += Get("input_tokens") + Get("cache_creation_input_tokens");
                        cur.CacheTok += Get("cache_read_input_tokens");
                        cur.OutTok += Get("output_tokens");
                    }
                    if (ExtractAssistantText(e).Contains("no visible output", StringComparison.OrdinalIgnoreCase))
                        cur.Fail++;
                }
            }
            Flush();
        }
        if (rep.Lines > 20 && typed == 0)
            rep.BadLines = Math.Max(rep.BadLines, rep.Lines); // user/assistant 항목을 하나도 못 찾으면 형식이 바뀐 것
        rep.Finish("Claude Code");
        return rep;
    }

    /// <summary>Cursor state.vscdb — 사용 중 잠기므로 임시 복사 후 읽기. bubbleId type1=user.</summary>
    public static ImportReport Cursor(IProgress<string>? progress = null, string? src = null)
    {
        src ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");
        var rep = new ImportReport { Name = "Cursor" };
        var rows = rep.Rows;
        if (!File.Exists(src)) { rep.Warnings.Add(L.T("Cursor 기록 파일(state.vscdb)이 없습니다.", "No Cursor history file (state.vscdb).")); return rep; }
        var tmp = Path.Combine(Path.GetTempPath(), $"promptadvisor_cursor_{Environment.ProcessId}.db");
        File.Copy(src, tmp, overwrite: true);
        try
        {
            using var db = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Pooling=False");
            db.Open();
            using (var chk = db.CreateCommand())
            {
                chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cursorDiskKV'";
                if (Convert.ToInt32(chk.ExecuteScalar()) == 0)
                {
                    rep.FormatChanged = true;
                    rep.Warnings.Add(L.T("Cursor 기록 형식이 바뀐 것 같습니다 (cursorDiskKV 테이블 없음). 앱을 업데이트하거나 이슈로 알려 주세요.",
                        "The Cursor history format seems to have changed (no cursorDiskKV table). Update the app or report an issue."));
                    return rep;
                }
            }
            // bubble 로드: key = bubbleId:{composerId}:{bubbleId}
            var bubbles = new Dictionary<string, (int type, string text, string projHint)>();
            var byComposer = new Dictionary<string, List<string>>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%'";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    rep.Lines++;
                    string key = rd.GetString(0);
                    string json = rd.IsDBNull(1) ? "" : rd.GetValue(1) is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : rd.GetString(1);
                    var parts = key.Split(':', 3);
                    if (parts.Length < 3) { rep.Bad("cursorDiskKV", rep.Lines, L.T("키 형식이 다름", "unexpected key format")); continue; }
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var r = doc.RootElement;
                        int btype = r.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                        string text = btype == 1 && r.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        string projHint = btype == 1 ? ProjectFromRawJson(json) : "";
                        bubbles[parts[2]] = (btype, text, projHint);
                        if (!byComposer.TryGetValue(parts[1], out var l)) byComposer[parts[1]] = l = new();
                        l.Add(parts[2]);
                    }
                    catch { rep.Bad("cursorDiskKV", rep.Lines, L.T("JSON 으로 읽을 수 없음", "not valid JSON")); }
                }
            }
            progress?.Report($"Cursor bubble {bubbles.Count}개 로드…");
            // composerData의 헤더 순서와 프로젝트 힌트 수집
            var order = new Dictionary<string, List<string>>();
            var composerProj = new Dictionary<string, string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM cursorDiskKV WHERE key LIKE 'composerData:%'";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string json = rd.GetValue(1) is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : rd.GetString(1);
                    try
                    {
                        string cid = rd.GetString(0).Split(':', 2)[1];
                        var pHint = ProjectFromRawJson(json);
                        if (!string.IsNullOrEmpty(pHint)) composerProj[cid] = pHint;
                        using var doc = JsonDocument.Parse(json);
                        var r = doc.RootElement;
                        if (!r.TryGetProperty("fullConversationHeadersOnly", out var headers)) continue;
                        var list = new List<string>();
                        foreach (var h in headers.EnumerateArray())
                            if (h.TryGetProperty("bubbleId", out var bid)) list.Add(bid.GetString() ?? "");
                        if (list.Count > 0) order[cid] = list;
                    }
                    catch { }
                }
            }
            foreach (var (cid, ids) in byComposer)
            {
                var seq = order.TryGetValue(cid, out var o) ? o.Where(ids.Contains).ToList() : ids;
                string sessProj = composerProj.TryGetValue(cid, out var cp) ? cp : "";
                if (string.IsNullOrEmpty(sessProj))
                    sessProj = seq.Select(id => bubbles.TryGetValue(id, out var bb) ? bb.projHint : "").FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
                PromptRow? cur = null;
                foreach (var id in seq)
                {
                    if (!bubbles.TryGetValue(id, out var b)) continue;
                    if (string.IsNullOrEmpty(sessProj) && !string.IsNullOrEmpty(b.projHint)) sessProj = b.projHint;
                    if (b.type == 1 && !IsSkippablePrompt(b.text) && b.text.Trim().Length > 0)
                    {
                        if (cur != null && IsCorrection(b.text)) cur.Fail++;
                        if (cur != null) { if (string.IsNullOrEmpty(cur.Project)) cur.Project = sessProj; rows.Add(cur); }
                        cur = new PromptRow { Source = "cursor", Session = cid, Project = sessProj, Text = b.text.Trim(), CreatedAt = "" };
                    }
                    else if (b.type == 2 && cur != null) cur.SegTurns++;
                }
                if (cur != null) { if (string.IsNullOrEmpty(cur.Project)) cur.Project = sessProj; rows.Add(cur); }
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
        rep.Finish("Cursor");
        return rep;
    }

    /// <summary>~/.gemini/antigravity/brain/**/*.md — 산출물 문서를 프롬프트성 텍스트로 취급.</summary>
    public static ImportReport Antigravity()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity", "brain");
        var rep = new ImportReport { Name = "Antigravity" };
        var rows = rep.Rows;
        if (!Directory.Exists(root)) { rep.Warnings.Add(L.T("Antigravity 기록 폴더가 없습니다.", "No Antigravity folder.")); return rep; }
        foreach (var file in Directory.GetFiles(root, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file).Trim();
                if (text.Length < 20) continue;
                rows.Add(new PromptRow
                {
                    Source = "antigravity",
                    Session = Path.GetFileName(Path.GetDirectoryName(file) ?? ""),
                    Text = text.Length > 4000 ? text[..4000] : text,
                    CreatedAt = File.GetLastWriteTime(file).ToString("s"),
                });
            }
            catch { }
        }
        return rep;
    }

    /// <summary>claude.ai export zip — conversations.json의 human 메시지.</summary>
    public static ImportReport ClaudeExportZip(string zipPath)
    {
        var rep = new ImportReport { Name = "claude.ai" };
        var rows = rep.Rows;
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("conversations.json", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (entry == null) { rep.FormatChanged = true; rep.Warnings.Add(L.T("zip 안에 conversations.json 이 없습니다.", "No conversations.json inside the zip.")); return rep; }
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            rep.FormatChanged = true;
            rep.Warnings.Add(L.T("conversations.json 형식이 예상(배열)과 다릅니다.", "conversations.json is not the expected array."));
            return rep;
        }
        foreach (var conv in doc.RootElement.EnumerateArray())
        {
            rep.Lines++;
            string session = conv.TryGetProperty("name", out var n) ? n.GetString() ?? "export" : "export";
            if (!conv.TryGetProperty("chat_messages", out var msgs)) continue;
            foreach (var m in msgs.EnumerateArray())
            {
                if (!m.TryGetProperty("sender", out var s) || s.GetString() != "human") continue;
                string text = "";
                if (m.TryGetProperty("text", out var t)) text = t.GetString() ?? "";
                else if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    text = string.Join("\n", c.EnumerateArray()
                        .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text")
                        .Select(b => b.TryGetProperty("text", out var bt2) ? bt2.GetString() ?? "" : ""));
                if (text.Trim().Length == 0) continue;
                rows.Add(new PromptRow
                {
                    Source = "claude.ai", Session = session, Text = text.Trim(),
                    CreatedAt = m.TryGetProperty("created_at", out var ca) ? ca.GetString() ?? "" : "",
                });
            }
        }
        rep.Finish("claude.ai");
        return rep;
    }

    // ---------- 내 데이터 CSV (백업 · 다른 도구 기록 옮기기) ----------
    public static readonly string[] CsvColumns = { "source", "session", "project", "created_at", "in_tok", "out_tok", "turns", "fail", "label", "text" };

    public static string ToCsv(IEnumerable<PromptRow> rows)
    {
        static string Esc(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new System.Text.StringBuilder(string.Join(",", CsvColumns)).Append('\n');
        foreach (var r in rows)
            sb.Append(string.Join(",", Esc(r.Source), Esc(r.Session), Esc(r.Project), Esc(r.CreatedAt),
                r.InTok, r.OutTok, r.SegTurns, r.Fail, r.Label, Esc(r.Text))).Append('\n');
        return sb.ToString();
    }

    /// <summary>RFC 4180 CSV 를 레코드 단위로 자른다. (레코드 시작 줄 번호, 칸들). 따옴표가 안 닫히면 예외.</summary>
    public static IEnumerable<(int Line, List<string> Cells)> ParseCsv(string text)
    {
        int line = 1, start = 1, i = 0;
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i += 2; continue; }
                if (c == '"') { quoted = false; i++; continue; }
                if (c == '\n') line++;
                cell.Append(c); i++; continue;
            }
            if (c == '"') { quoted = true; any = true; i++; continue; }
            if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); any = true; i++; continue; }
            if (c == '\r') { i++; continue; }
            if (c == '\n')
            {
                cells.Add(cell.ToString()); cell.Clear();
                if (any || cells.Any(x => x.Length > 0)) yield return (start, cells);
                cells = new List<string>(); any = false; line++; start = line; i++; continue;
            }
            cell.Append(c); any = true; i++;
        }
        if (quoted) throw new FormatException(L.T($"{start}번째 줄에서 시작한 따옴표가 닫히지 않았습니다", $"quote opened on line {start} is never closed"));
        cells.Add(cell.ToString());
        if (any || cells.Any(x => x.Length > 0)) yield return (start, cells);
    }

    /// <summary>CSV 가져오기. 머리줄에 text 칸이 있어야 한다. 잘못된 줄은 줄 번호와 이유를 경고로 남기고 건너뛴다.</summary>
    public static ImportReport Csv(string path)
    {
        var rep = new ImportReport { Name = "CSV" };
        string where = Path.GetFileName(path);
        List<(int Line, List<string> Cells)> recs;
        try { recs = ParseCsv(File.ReadAllText(path, System.Text.Encoding.UTF8)).ToList(); }
        catch (FormatException ex) { rep.Warnings.Add($"{where}: {ex.Message}"); return rep; }
        if (recs.Count == 0) { rep.Warnings.Add(L.T($"{where}: 빈 파일입니다", $"{where}: empty file")); return rep; }
        var header = recs[0].Cells.Select(h => h.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToList();
        int Col(string n) => header.IndexOf(n);
        int ti = Col("text");
        if (ti < 0)
        {
            rep.Warnings.Add(L.T($"{where} 1번째 줄: 머리줄에 text 칸이 없습니다 (필요: {string.Join(",", CsvColumns)})",
                $"{where} line 1: header has no 'text' column (expected: {string.Join(",", CsvColumns)})"));
            return rep;
        }
        foreach (var (line, cells) in recs.Skip(1))
        {
            rep.Lines++;
            if (cells.Count != header.Count) { rep.Bad(where, line, L.T($"칸 수 {cells.Count}개, 머리줄은 {header.Count}개", $"{cells.Count} cells, header has {header.Count}")); continue; }
            string Get(string n) => Col(n) is var k && k >= 0 ? cells[k].Trim() : "";
            var text = cells[ti].Trim();
            if (text.Length == 0) { rep.Bad(where, line, L.T("text 칸이 비었습니다", "'text' is empty")); continue; }
            bool ok = true;
            long Num(string n)
            {
                var v = Get(n);
                if (v.Length == 0) return 0;
                if (long.TryParse(v, out var x) && x >= 0) return x;
                rep.Bad(where, line, L.T($"{n} 값 '{v}' 이(가) 0 이상 정수가 아닙니다", $"{n} value '{v}' is not a non-negative integer"));
                ok = false; return 0;
            }
            var row = new PromptRow
            {
                Source = Get("source") is { Length: > 0 } src ? src : "csv",
                Session = Get("session") is { Length: > 0 } se ? se : "csv",
                Project = Get("project"), CreatedAt = Get("created_at"), Text = text,
                InTok = Num("in_tok"), OutTok = Num("out_tok"), SegTurns = (int)Num("turns"), Fail = (int)Num("fail"),
            };
            if (!ok) continue;
            var created = Get("created_at");
            if (created.Length > 0 && !DateTime.TryParse(created, out _)) { rep.Bad(where, line, L.T($"created_at '{created}' 을(를) 날짜로 읽을 수 없습니다", $"created_at '{created}' is not a date")); continue; }
            row.Label = Get("label") switch { "1" or "good" => 1, "-1" or "bad" => -1, _ => 0 };
            rep.Rows.Add(row);
        }
        rep.Finish("CSV");
        return rep;
    }

    public static PromptRow Paste(string source, string text) => new()
    {
        Source = source, Session = "paste", Text = text.Trim(), CreatedAt = DateTime.Now.ToString("s"),
    };
}
