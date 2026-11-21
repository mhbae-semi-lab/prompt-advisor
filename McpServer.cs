using System.Text;
using System.Text.Json;

namespace PromptAdvisor;

/// <summary>
/// MCP(Model Context Protocol) 서버 — stdio JSON-RPC 2.0.
/// Cursor·Claude Code·Codex가 작업 중 로컬 프롬프트 지식(DB·임베딩·통계)에 접근하는 통로.
/// 데이터는 로컬 DB에만 머물고, 반환 텍스트의 예시는 마스킹된다. 호출: PromptAdvisor.exe --mcp
/// </summary>
public static class McpServer
{
    public sealed record ToolDef(string Name, string Description, string SchemaJson);
    public sealed record ToolRegistration(
        string Name,
        string Description,
        string SchemaJson,
        JsonElement ParsedSchema,
        Func<JsonElement, Store, Ollama, Task<string>> Handler);

    private static List<PromptRow>? _cachedCorpus;
    private static (int Count, long MaxId, int EmbeddedCount) _cacheVersion;

    /// <summary>DB 변경이 없으면 메모리에 적재된 코퍼스(임베딩 포함)를 즉시 반환해 반복 역직렬화 지연을 제거한다.</summary>
    public static List<PromptRow> GetCachedCorpus(Store store)
    {
        var ver = store.GetCorpusVersion();
        if (_cachedCorpus == null || _cacheVersion != ver)
        {
            _cachedCorpus = store.All(withEmbedding: true);
            _cacheVersion = ver;
        }
        return _cachedCorpus;
    }

    public static readonly ToolRegistration[] Registry = BuildRegistry();

    public static readonly ToolDef[] Tools = Registry
        .Select(r => new ToolDef(r.Name, r.Description, r.SchemaJson))
        .ToArray();

    private static ToolRegistration[] BuildRegistry()
    {
        static ToolRegistration Reg(string name, string desc, string schemaJson, Func<JsonElement, Store, Ollama, Task<string>> handler)
        {
            using var doc = JsonDocument.Parse(schemaJson);
            return new ToolRegistration(name, desc, schemaJson, doc.RootElement.Clone(), handler);
        }

        return new[]
        {
            Reg("advisor_stats",
                "PromptAdvisor 현재 현황 요약 (프롬프트 수·토큰·실패율·모델표·예측 보정 상태). 세션 시작 시 맥락 파악용.",
                """{"type":"object","properties":{}}""",
                (_, store, _) => Task.FromResult(
                    Advisor.BuildStatusReport(GetCachedCorpus(store), Advisor.LoadModels(),
                        store.CalibrationRows(), store.ErrorCount(), store.CountEmbedded(), DateTime.Now))),

            Reg("search_history",
                "과거 프롬프트 이력을 의미 검색한다 (로컬 임베딩 bge-m3, 불가 시 키워드). 유사 사례와 그 성과(라벨·토큰)를 마스킹된 발췌로 반환.",
                """{"type":"object","properties":{"query":{"type":"string","description":"찾을 내용 (자연어)"},"project":{"type":"string","description":"우선 검색할 프로젝트 이름 (선택)"},"limit":{"type":"number","description":"결과 수 1~10 (기본 5)"}},"required":["query"]}""",
                (args, store, ollama) => SearchAsync(
                    ArgStr(args, "query"),
                    Math.Clamp(ArgInt(args, "limit", 5), 1, 10),
                    store, ollama, ArgStr(args, "project"))),

            Reg("recommend_prompt",
                "새 프롬프트 초안을 프로젝트·세션 맥락과 함께 분석해 작업 유형·린트 경고·예상 토큰·모델 추천 순위와 개선안을 반환한다. 전송 전에 호출할 것.",
                """{"type":"object","properties":{"prompt":{"type":"string","description":"분석할 프롬프트 초안"},"project":{"type":"string","description":"작업 중인 프로젝트 폴더 경로 또는 이름 (선택)"},"goal":{"type":"string","description":"현재 세션의 작업 목적 한 줄 (선택)"},"files":{"type":"string","description":"관련 파일 목록 (쉼표 구분, 선택)"},"model":{"type":"string","description":"고정할 모델 이름 (선택)"}},"required":["prompt"]}""",
                (args, store, _) =>
                {
                    var prompt = ArgStr(args, "prompt");
                    if (string.IsNullOrWhiteSpace(prompt)) throw new McpError(-32602, "prompt required");
                    var modelArg = ArgStr(args, "model");
                    return Task.FromResult(Advisor.BuildRecommendText(
                        prompt, store,
                        string.IsNullOrEmpty(modelArg) ? App.Settings.SelectedModel : modelArg,
                        App.Settings.RecommendCount,
                        ArgStr(args, "project"),
                        ArgStr(args, "goal"),
                        ArgStr(args, "files").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)));
                }),

            Reg("weekly_report",
                "최근 7일 프롬프트 사용 리포트 (토큰 TOP 낭비·잘 먹힌 프롬프트·조언).",
                """{"type":"object","properties":{}}""",
                (_, store, _) => Task.FromResult(Advisor.WeeklyReport(GetCachedCorpus(store), DateTime.Now))),
        };
    }

    private static readonly Dictionary<string, ToolRegistration> _toolMap =
        Registry.ToDictionary(r => r.Name, StringComparer.Ordinal);

    private static string ArgStr(JsonElement args, string key)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int ArgInt(JsonElement args, string key, int def)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;

    public static void Run()
    {
        try { Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8; } catch { }
        using var store = new Store();
        var ollama = App.Ollama;
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // WPF 스레드 컨텍스트에서 비동기 대기가 교착되지 않도록 스레드풀에서 처리
            var response = Task.Run(() => HandleAsync(line, store, ollama)).GetAwaiter().GetResult();
            if (response != null)
            {
                Console.Out.WriteLine(response);
                Console.Out.Flush();
            }
        }
    }

    private static async Task<string?> HandleAsync(string line, Store store, Ollama ollama)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return Error(null, -32700, "parse error"); }
        using (doc)
        {
            var root = doc.RootElement;
            string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            bool hasId = root.TryGetProperty("id", out var idEl);
            if (!hasId) return null; // notification (initialized 등) — 응답 없음

            object id = idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var n) ? n
                      : idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
            try
            {
                object? result = method switch
                {
                    "initialize" => new
                    {
                        protocolVersion = root.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var pv)
                            ? pv.GetString() ?? "2024-11-05" : "2024-11-05",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "promptadvisor", version = "1.0.0" },
                    },
                    "ping" => new { },
                    "tools/list" => new
                    {
                        tools = Registry.Select(t => new
                        {
                            name = t.Name,
                            description = t.Description,
                            inputSchema = t.ParsedSchema,
                        }).ToArray(),
                    },
                    "tools/call" => await CallToolAsync(root, store, ollama),
                    _ => throw new McpError(-32601, $"method not found: {method}"),
                };
                return JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (McpError ex) { return Error(id, ex.Code, ex.Message); }
            catch (Exception ex)
            {
                store.LogError("MCP", $"{method}: {ex.Message}");
                return Error(id, -32603, ex.Message);
            }
        }
    }

    private sealed class McpError(int code, string message) : Exception(message) { public int Code = code; }

    private static string Error(object? id, int code, string message)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new { code, message },
        });

    private static object ToolResult(string text, bool isError = false)
        => new { content = new[] { new { type = "text", text } }, isError };

    private static async Task<object> CallToolAsync(JsonElement root, Store store, Ollama ollama)
    {
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var nameEl))
            throw new McpError(-32602, "params.name required");
        string name = nameEl.GetString() ?? "";
        var args = p.TryGetProperty("arguments", out var a) ? a : default;

        if (!_toolMap.TryGetValue(name, out var reg))
            throw new McpError(-32602, $"unknown tool: {name}");

        try
        {
            string text = await reg.Handler(args, store, ollama);
            return ToolResult(text);
        }
        catch (McpError) { throw; }
        catch (Exception ex) { return ToolResult("오류: " + ex.Message, isError: true); }
    }

    private static async Task<string> SearchAsync(string query, int limit, Store store, Ollama ollama, string? project = null)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpError(-32602, "query required");
        var corpus = GetCachedCorpus(store);
        var sb = new StringBuilder();

        var vec = await ollama.Embed(query);
        if (vec != null)
        {
            var hits = Advisor.Knn(vec, corpus, limit, project);
            sb.AppendLine($"의미 검색 \"{query}\" — {hits.Count}건 (임베딩 {corpus.Count(p => p.Embedding != null):N0}건 중{(string.IsNullOrWhiteSpace(project) ? "" : $", 프로젝트 '{project}' 우선")})");
            foreach (var h in hits)
                sb.AppendLine(FormatHit(h.Row, $"유사도 {h.Similarity:F2}"));
        }
        else
        {
            var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).ToList();
            var hits = corpus.Select(r =>
                             {
                                 int sc = words.Count(w => r.Text.Contains(w, StringComparison.OrdinalIgnoreCase));
                                 if (!string.IsNullOrWhiteSpace(project) && r.Project.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase)) sc += 2;
                                 return (Row: r, Score: sc);
                             })
                             .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(limit).ToList();
            sb.AppendLine($"키워드 검색 \"{query}\" — {hits.Count}건 (Ollama 미연결이라 의미 검색 대신 사용)");
            foreach (var h in hits)
                sb.AppendLine(FormatHit(h.Row, $"키워드 {h.Score}점"));
        }
        if (sb.Length < 60) sb.AppendLine("결과가 없습니다. [가져오기]로 이력을 먼저 쌓거나 검색어를 바꿔 보세요.");
        return sb.ToString().TrimEnd();
    }

    private static string FormatHit(PromptRow r, string scoreDesc)
    {
        var preview = Advisor.MaskSensitive(r.Text.Replace("\n", " "));
        if (preview.Length > 200) preview = preview[..200] + "…";
        string proj = string.IsNullOrWhiteSpace(r.Project) ? "" : $"·{r.Project}";
        return $"- ({scoreDesc}) [{r.DisplaySource}{proj}·{r.DisplayTime}] {r.EffectiveLabel} · {r.TotalTok:N0} tok · 실패 {r.Fail}\n  {preview}";
    }
}
