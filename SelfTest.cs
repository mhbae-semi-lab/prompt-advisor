namespace PromptAdvisor;

/// <summary>
/// 앱 내장 셀프테스트. `PromptAdvisor.exe --selftest [--data 폴더]` → 데이터 폴더 selftest-result.txt, 종료 코드 = 실패 수.
/// 데이터 폴더를 주지 않으면 임시 폴더를 쓰므로 사용자 DB 를 건드리지 않는다.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        Directory.CreateDirectory(Store.DataDir);
        var lines = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { lines.Add($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) fail++; }
        void Try(string name, Func<bool> f)
        {
            try { Check(name, f()); }
            catch (Exception ex) { lines.Add($"FAIL {name} ({ex.GetType().Name}: {ex.Message})"); fail++; }
        }
        bool wasEn = L.En;
        L.Init("ko");
        var tmp = Path.Combine(Store.DataDir, "selftest-tmp");
        Directory.CreateDirectory(tmp);

        // ---- 기본 린트 · 토큰 · 분류 ----
        Try("vague-lint", () => Advisor.Lint("이거 고쳐줘").Any(l => l.Id == "vague"));
        var loggy = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"System.Exception at My.App.Foo() line {i}")) + "\n이 코드 왜 이래? 파일 어딘가에서 오류 나는데 전체적으로 고쳐주고 그리고 문서도 만들어주고 그리고 테스트도 해줘. 길게 설명해줘.";
        Try("log-lint", () => Advisor.Lint(loggy).Any(l => l.Id == "raw-log"));
        Try("format-lint", () => Advisor.Lint("이 함수가 왜 느린지 분석해서 알려주세요. 자세히 부탁합니다.").Any(l => l.Id == "no-format"));
        Try("token-estimate-korean", () => Advisor.EstimateInputTokens("안녕하세요 이 문장을 요약해 주세요") is > 3 and < 40);
        Try("token-estimate-long", () => Advisor.EstimateInputTokens(new string('a', 4000)) == 1000);
        var v = new float[] { 1, 2, 3 };
        Try("cosine-self", () => Math.Abs(Advisor.Cosine(v, v) - 1.0) < 1e-9);
        Try("cosine-ortho", () => Math.Abs(Advisor.Cosine(new float[] { 1, 0 }, new float[] { 0, 1 })) < 1e-9);
        Try("classify-code", () => Advisor.Classify("NullReference 오류 고쳐줘 MainWindow.cs") == "code");
        Try("classify-summary", () => Advisor.Classify("이 글 요약해줘") == "summary");
        Try("correction", () => Importers.IsCorrection("아니 그게 아니라 다시 해줘"));
        Try("models-load", () => Advisor.LoadModels().Count > 0);
        Try("weekly-report", () =>
        {
            var report = Advisor.WeeklyReport(new List<PromptRow>
            {
                new() { Source = "claude", Text = "테스트 프롬프트입니다 길게 써봅니다", InTok = 1000, OutTok = 500, Fail = 1, CreatedAt = DateTime.Now.ToString("s") },
            }, DateTime.Now);
            return report.Contains("최근 7일") && report.Contains("TOP 5");
        });

        // ---- 영어 입력 ----
        Try("en-vague-lint", () => Advisor.Lint("fix it please").Any(l => l.Id == "vague"));
        Try("en-format-ok", () => !Advisor.Lint("Explain why the build fails, as a table with cause and fix columns").Any(l => l.Id == "no-format"));
        Try("en-classify-code", () => Advisor.Classify("the build fails with an exception in Loader.cs") == "code");
        Try("en-correction", () => Importers.IsCorrection("No, that's wrong, I meant the X axis"));

        // ---- 장비 SW 린트 (차별점 2) ----
        Try("eq-detect-ko", () => Equipment.IsEquipmentRequest("로더 화면 알람 뜨는 거 고쳐줘"));
        Try("eq-detect-not-generic", () => !Equipment.IsEquipmentRequest("README 에 설치 방법 추가해줘") && !Equipment.IsEquipmentRequest("at this stage, refactor the parser"));
        Try("eq-missing-ko", () =>
        {
            var ids = Advisor.Lint("로더 화면 알람 뜨는 거 고쳐줘").Select(l => l.Id).ToHashSet();
            return ids.SetEquals(ids) && ids.Contains("eq-version") && ids.Contains("eq-log") && ids.Contains("eq-repro") && ids.Contains("eq-expected") && !ids.Contains("eq-target");
        });
        Try("eq-missing-en", () =>
        {
            var ids = Advisor.Lint("Fix the homing timeout on the Z axis").Select(l => l.Id).ToHashSet();
            return ids.Contains("eq-version") && ids.Contains("eq-log") && ids.Contains("eq-repro") && !ids.Contains("eq-target");
        });
        Try("eq-complete", () => !Advisor.Lint(SampleData.ExampleSet[^1]).Any(l => l.Id.StartsWith("eq-")));
        Try("eq-lint-off", () => !Advisor.Lint("로더 화면 알람 뜨는 거 고쳐줘", null, new RuleConfig { EquipmentLint = false }).Any(l => l.Id.StartsWith("eq-")));
        Try("eq-improved-block", () =>
        {
            var a = new Analysis { TaskType = "code", Lints = Advisor.Lint("서보 알람 자꾸 떠요") };
            var t = Advisor.BuildImprovedPrompt("서보 알람 자꾸 떠요", a, "M");
            return t.Contains("[장비 정보") && t.Contains("재현 조건") && t.Contains("추측하지 말고");
        });
        Try("en-improved-prompt", () =>
        {
            var a = new Analysis { TaskType = "code", Lints = Advisor.Lint("Fix the homing timeout on the Z axis") };
            var t = Advisor.BuildImprovedPrompt("Fix the homing timeout on the Z axis", a, "M");
            return t.StartsWith("[Improved prompt for M]") && t.Contains("[Equipment info") && t.Contains("[Output format]") && !t.Contains("[작업]");
        });

        // ---- 추천 · 보정 ----
        var testModels = new List<ModelInfo>
        {
            new("A-small", "T", "P", 1, 1, new[] { "summary" }, "작음", 0.5),
            new("B-big", "T", "P", 1, 1, new[] { "code" }, "큼", 2.0),
        };
        Try("pinned-first", () => Advisor.RecommendModels("code", 1000, 1000, testModels, 2, "A-small") is var r && r[0].Model.Name == "A-small" && r[0].Pinned && r[0].Rank == 1);
        var ranks2 = Advisor.RecommendModels("code", 1000, 1000, testModels, 2);
        Try("factor-total", () => ranks2.First(r => r.Model.Name == "B-big").EstTotalTokens > ranks2.First(r => r.Model.Name == "A-small").EstTotalTokens);
        Try("fit-label", () => ranks2.First(r => r.Model.Name == "B-big").Fit == "높음");
        Try("recommend-count", () => Advisor.RecommendModels("code", 1000, 1000, testModels, 1).Count == 1);
        Try("calib-insufficient", () => Advisor.Calibration(new[] { ("code", 100L, 200L) }, "code").factor == 1.0);
        Try("calib-clamp", () => Advisor.Calibration(new[] { ("code", 100L, 300L), ("code", 100L, 300L), ("code", 100L, 300L), ("code", 100L, 300L) }, "code") is (2.0, 4));
        Try("prediction-error", () => Advisor.PredictionErrorPct(new[] { ("code", 100L, 200L) }) == 50.0);
        Try("display-time-empty", () => new PromptRow { CreatedAt = "" }.DisplayTime == "—");
        Try("display-time-iso", () => new PromptRow { CreatedAt = "2026-10-17T00:43:55Z" }.DisplayTime.Contains(':'));
        Try("display-source", () => new PromptRow { Source = "claude" }.DisplaySource == "Claude Code");
        Try("hashnorm", () => Store.HashNorm("a  b\n c") == Store.HashNorm(" a b c "));
        Try("rule-improve-has-original", () =>
        {
            var a = new Analysis { TaskType = "code", Lints = Advisor.Lint("이거 고쳐줘") };
            var t = Advisor.BuildImprovedPrompt("이거 고쳐줘", a, "Claude Sonnet");
            return t.Contains("이거 고쳐줘") && t.Contains("[작업]") && t.Contains("대상 파일") && t.Contains("[출력 형식]");
        });
        Try("estimate-output", () => Advisor.EstimateOutputTokens("code", new List<NeighborInfo>(), Array.Empty<(string, long, long)>()).estOut == 2500);
        Try("ontopic-ok", () => Advisor.IsOnTopic("MainWindow.xaml.cs 저장 버튼 오류 고쳐줘", "MainWindow.xaml.cs의 저장 버튼 NullReference를 고쳐주세요"));
        Try("ontopic-drift", () => !Advisor.IsOnTopic("MainWindow.xaml.cs 저장 버튼 오류 고쳐줘", "결제 모듈 로그인 화면 디자인을 바꿔 주세요"));
        Try("score-breakdown", () => Advisor.ScoreBreakdown(new PromptRow { Score = 10, Fail = 2, InTok = 9000, OutTok = 1000 }, 1000).Contains("실패신호 2회"));
        Try("multi-model", () => Advisor.LoadModels().Count >= 10);
        Try("auto-adjust", () =>
        {
            var (m, c) = Advisor.AutoAdjustFactors(new List<ModelInfo> { new("M", "T", "P", 1, 1, new[] { "code" }, "", 1.0) },
                new List<(string, long, long)> { ("M", 100, 200), ("M", 100, 200), ("M", 100, 200) });
            return c.Count == 1 && m[0].OutputFactor == 2.0;
        });
        Try("auto-adjust-skip", () => Advisor.AutoAdjustFactors(new List<ModelInfo> { new("M", "T", "P", 1, 1, new[] { "code" }, "", 1.0) },
            new List<(string, long, long)> { ("M", 100, 200) }).models[0].OutputFactor == 1.0);
        Try("batch-report", () =>
        {
            var b = Advisor.ImageBatchReport("imgs", new List<(string, string, long)> { ("a.png", ".png", 500_000), ("big.jpg", ".jpg", 3_500_000), ("c.png", ".png", 700_000) }, 3, 0, 0);
            return b.Contains("[1/4]") && b.Contains("[4/4]") && b.Contains("3,300");
        });
        Try("skills-scan", () => { _ = Advisor.ScanSkills(); return true; });
        Try("knn-weighted-estimate", () => Advisor.EstimateOutputTokens("code", new List<NeighborInfo>
        {
            new(new PromptRow { OutTok = 1000 }, 1.0), new(new PromptRow { OutTok = 2600 }, 0.25),
        }, Array.Empty<(string, long, long)>()).estOut == 1320);
        Try("input-calibration", () => Advisor.CalibratedEstimate(1000, new[] { ("code", 100L, 200L), ("code", 100L, 200L), ("code", 100L, 200L) }, "code") is (2000, 2.0, 3));
        var mtRows = new List<(string, string, long, long)>();
        for (int i = 0; i < 3; i++) mtRows.Add(("M", "code", 100L, 300L));
        for (int i = 0; i < 5; i++) mtRows.Add(("M", "summary", 100L, 100L));
        Try("model-task-factor", () => Advisor.ModelTaskFactor(mtRows, "M", "code", 1.0) == 2.0);
        Try("model-task-factor-skip", () => Advisor.ModelTaskFactor(mtRows.Take(2).ToList(), "M", "code", 1.0) == 1.0);
        Try("models-cache", () => ReferenceEquals(Advisor.LoadModels(), Advisor.LoadModels()));
        Try("judge-parse", () => Advisor.ParseJudgeResult("점수: 75\n대상 파일이 명시됨").score == 75 && Advisor.ParseJudgeResult("85/100점 수준").score == 85 && Advisor.ParseJudgeResult("잘했습니다").score == -1);
        Try("text-similarity", () => Advisor.TextSimilarity("MainWindow 저장 오류 수정", "MainWindow 저장 오류 수정") == 1.0
            && Advisor.TextSimilarity("전혀 다른 주제 문장", "MainWindow 저장 오류 수정") < 0.3
            && Advisor.TextSimilarity("MainWindow 저장 오류 수정해줘", "MainWindow 저장 오류 수정") >= 0.65);
        Try("text-neighbors", () => Advisor.TextNeighbors("Z축 원점 복귀가 실패해요",
            new List<PromptRow> { new() { Text = "Z축 원점 복귀 실패하는 거 고쳐줘" }, new() { Text = "리포트 차트 색 바꿔줘" } }, 3).FirstOrDefault()?.Row.Text.Contains("원점") == true);
        Try("confidence-label", () => Advisor.ConfidenceLabel(0, 0).Contains("낮음") && Advisor.ConfidenceLabel(4, 32).Contains("±32%") && Advisor.ConfidenceLabel(20, 5).Contains("높음"));

        // ---- 프롬프트만 복사 ----
        Try("extract-prompt-only-rule", () =>
        {
            var o = Advisor.ExtractPromptOnly("(Ollama가 꺼져 있어 규칙 기반 개선안을 만들었습니다.)\n\n[Claude Sonnet용 개선 프롬프트]\n\n[작업]\n이거 고쳐줘\n\n[출력 형식]\n- 결론 먼저\n— 왜: 범위 불명확 항목을 보완했습니다.");
            return o.StartsWith("[작업]") && o.Contains("이거 고쳐줘") && !o.Contains("왜:") && !o.Contains("Ollama");
        });
        Try("extract-prompt-only-first", () => Advisor.ExtractPromptOnly("\n\n===== A모델용 =====\n프롬프트 A 본문\n— 왜: 설명\n\n===== B모델용 =====\n프롬프트 B 본문\n") == "프롬프트 A 본문");
        Try("extract-prompt-only-shadow", () => Advisor.ExtractPromptOnly("[M용 개선 프롬프트]\n[작업]\n채택본\n---- Shadow (LLM안) ----\n버릴 본문") == "[작업]\n채택본");
        Try("extract-prompt-only-en", () => Advisor.ExtractPromptOnly("[Improved prompt for M]\n[Task]\nfix it\n— Why: added scope") == "[Task]\nfix it");
        Try("extract-prompt-only-plain", () => Advisor.ExtractPromptOnly("  그냥 프롬프트  ") == "그냥 프롬프트");
        Try("extract-prompt-only-empty", () => Advisor.ExtractPromptOnly("  ") == "");

        // ---- 마스킹 · 지식 카드 원문 제거 ----
        Try("mask-sensitive", () =>
        {
            var m = Advisor.MaskSensitive(@"C:\Users\kim\a.cs, D:\work\secret-proj\b.cs, mail a@b.com, key sk-abcdefgh12345678, ghp_abcdefgh1234, AIzaSyA1234567890123456789012, 10.20.30.40, 010-1234-5678, password=hunter2, Bearer abcdefghijklmnop");
            return m.Contains("%USERPROFILE%") && !m.Contains("secret-proj") && m.Contains("b.cs") && !m.Contains("@") && !m.Contains("sk-abc")
                && !m.Contains("ghp_") && !m.Contains("AIza") && !m.Contains("10.20.30.40") && !m.Contains("1234-5678") && !m.Contains("hunter2") && !m.Contains("abcdefghijklmnop");
        });
        var sampleRows = new List<PromptRow>
        {
            new() { Source = "claude", Session = "s1", Project = "secret-project-x", Text = "MainWindow.xaml.cs 저장 오류 고쳐줘. 표로 정리 고유문장XYZ", InTok = 1000, OutTok = 500, AutoLabel = "good", Score = 95, CreatedAt = DateTime.Now.ToString("s") },
            new() { Source = "cursor", Session = "s2", Project = "secret-project-x", Text = "이거 고쳐줘", InTok = 300, OutTok = 100, Fail = 1, AutoLabel = "bad", Score = 20, CreatedAt = DateTime.Now.ToString("s") },
        };
        Try("status-report", () => Advisor.BuildStatusReport(sampleRows, Advisor.LoadModels(), new(), 0, 1, DateTime.Now) is var s
            && s.Contains("AUTO-GENERATED") && s.Contains("핵심 지표") && !s.Contains("secret-project-x"));
        Try("knowledge-cards-no-raw", () =>
        {
            var cards = Advisor.BuildKnowledgeCards(sampleRows, Advisor.LoadModels(), new(), new List<string> { "Claude" }, "", DateTime.Now);
            var all = string.Join("\n", cards.Select(c => c.Content));
            return cards.Count == 4 && all.Contains("파일 경로 포함") && !all.Contains("고유문장XYZ") && !all.Contains("secret-project-x");
        });

        // ---- 저장: 스키마 마이그레이션 ----
        Try("schema-migrate-v0", () =>
        {
            var p = Path.Combine(tmp, "v0.db");
            if (File.Exists(p)) File.Delete(p);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={p};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE prompts(id INTEGER PRIMARY KEY, source TEXT, session TEXT, text TEXT, created_at TEXT, in_tok INTEGER, out_tok INTEGER, cache_tok INTEGER, seg_turns INTEGER, fail INTEGER, score REAL DEFAULT 0, label INTEGER DEFAULT 0, auto_label TEXT DEFAULT '', embedding BLOB, hash TEXT UNIQUE);" +
                                  "INSERT INTO prompts(source,session,text,created_at,in_tok,out_tok,cache_tok,seg_turns,fail,hash) VALUES('claude','s','old row','',1,2,0,1,0,'h1');";
                cmd.ExecuteNonQuery();
            }
            using var st = new Store(p);
            var rows = st.All();
            return st.OpenedFromVersion == 0 && rows.Count == 1 && rows[0].Project == "" && !rows[0].Repro && st.Rewrites().Count == 0 && st.ShadowRows().Count == 0;
        });
        Try("schema-newer-rejected", () =>
        {
            var p = Path.Combine(tmp, "future.db");
            if (File.Exists(p)) File.Delete(p);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={p};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"PRAGMA user_version = {Store.SchemaVersion + 1}";
                cmd.ExecuteNonQuery();
            }
            try { using var st = new Store(p); return false; } catch (InvalidOperationException) { return true; }
        });

        // ---- 내 데이터: CSV 왕복 · 잘못된 줄 · 가져오기 형식 변경 감지 ----
        Try("csv-roundtrip", () =>
        {
            var p = Path.Combine(tmp, "rt.csv");
            File.WriteAllText(p, Importers.ToCsv(new[] { new PromptRow { Source = "claude", Session = "s", Project = "p", Text = "쉼표, \"따옴표\"\n줄바꿈", InTok = 5, OutTok = 6, SegTurns = 2, Fail = 1, Label = 1 } }), System.Text.Encoding.UTF8);
            var rep = Importers.Csv(p);
            return rep.Rows.Count == 1 && rep.Rows[0].Text == "쉼표, \"따옴표\"\n줄바꿈" && rep.Rows[0].InTok == 5 && rep.Rows[0].Label == 1 && rep.Warnings.Count == 0;
        });
        Try("csv-bad-lines", () =>
        {
            var p = Path.Combine(tmp, "bad.csv");
            File.WriteAllText(p, "text,in_tok,created_at\n좋은 줄,10,2026-11-01\n,5,\n숫자 아님,abc,\n칸 부족\n날짜 틀림,1,어제\n", System.Text.Encoding.UTF8);
            var rep = Importers.Csv(p);
            var w = string.Join("|", rep.Warnings);
            return rep.Rows.Count == 1 && rep.BadLines == 4 && w.Contains("3번째 줄") && w.Contains("4번째 줄") && w.Contains("5번째 줄") && w.Contains("6번째 줄") && w.Contains("abc");
        });
        Try("csv-no-text-column", () =>
        {
            var p = Path.Combine(tmp, "nohdr.csv");
            File.WriteAllText(p, "a,b\n1,2\n");
            return Importers.Csv(p) is { Rows.Count: 0 } r && r.Warnings[0].Contains("text");
        });
        Try("claude-format-change", () =>
        {
            var root = Path.Combine(tmp, "claude-new-format", "proj");
            Directory.CreateDirectory(root);
            File.WriteAllLines(Path.Combine(root, "s.jsonl"), Enumerable.Range(0, 30).Select(i => $"{{\"kind\":\"turn\",\"payload\":{{\"n\":{i}}}}}"));
            var rep = Importers.ClaudeCode(null, Path.GetDirectoryName(root));
            return rep.FormatChanged && rep.Rows.Count == 0 && rep.Warnings[0].Contains("형식");
        });
        Try("claude-bad-json-line", () =>
        {
            var root = Path.Combine(tmp, "claude-ok", "proj");
            Directory.CreateDirectory(root);
            File.WriteAllLines(Path.Combine(root, "s.jsonl"), new[]
            {
                "{\"type\":\"user\",\"cwd\":\"C:\\\\repos\\\\demo-app\",\"timestamp\":\"2026-11-01T00:00:00Z\",\"message\":{\"content\":\"Loader.cs 버그 고쳐줘\"}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":20}}}",
                "{broken json line",
            });
            var rep = Importers.ClaudeCode(null, Path.GetDirectoryName(root));
            return rep.Rows.Count == 1 && rep.Rows[0].OutTok == 20 && rep.Rows[0].Project == "demo-app" && rep.Warnings.Any(w => w.Contains("3번째 줄"));
        });
        Try("project-extract", () => Importers.ProjectNameFrom(@"D:\src\MyApp", "d--src-MyApp") == "MyApp"
            && Importers.ProjectNameFrom(null, "C--Users-kim-source-repos-shop-api") == "shop-api");

        // ---- 합성 예시 DB · 학습한 경고 · 개선 추적 · Shadow ----
        Try("demo-db", () =>
        {
            var p = Path.Combine(tmp, "demo.db");
            SampleData.CreateDemoDb(p);
            using var st = new Store(p);
            var all = st.All();
            var projects = all.Select(r => r.Project).Distinct().Count();
            return all.Count is >= 90 and <= 120 && projects == 3 && st.CalibrationRows().Count >= 10 && st.Rewrites().Count >= 10 && st.ShadowRows().Count >= 10;
        });
        Try("rule-impact-learned", () =>
        {
            using var st = new Store(Path.Combine(tmp, "demo.db"));
            var impact = RuleImpact.Compute(st.All(), new RuleConfig());
            var lint = Advisor.Lint("서보 알람 자꾸 떠요", null, null, impact);
            return impact.TryGetValue("eq-log", out var s) && s.Enough && lint.Any(l => l.Message.Contains("내 기록"));
        });
        Try("rewrite-tracking", () =>
        {
            using var st = new Store(Path.Combine(tmp, "demo.db"));
            var s = Verification.Rewrites(st, new RuleConfig());
            return s.Matched >= 10 && s.BaselineN >= 10 && s.ImprovedTurns < s.BaselineTurns;
        });
        Try("shadow-gate", () =>
        {
            var few = Enumerable.Range(0, 5).Select(_ => new Store.ShadowRow("", "M", true, 80, 1, 0, "rule", "")).ToList();
            var many = Enumerable.Range(0, 12).Select(_ => new Store.ShadowRow("", "M", true, 80, 1, 0, "rule", "")).ToList();
            var drift = many.Select((r, i) => i < 3 ? r with { OnTopic = false } : r).ToList();
            return !Verification.Shadow(few).CanSwitch && Verification.Shadow(many).CanSwitch && !Verification.Shadow(drift).CanSwitch;
        });
        Try("compare-highlight", () =>
        {
            var rows = Verification.Compare(SampleData.ExampleSet, new RuleConfig(), new RuleConfig { EquipmentLint = false });
            return rows.Any(r => r.Changed && r.Removed.Length > 0 && r.Before > r.After) && rows.Any(r => !r.Changed);
        });
        Try("config-history", () =>
        {
            using var st = new Store(Path.Combine(tmp, "demo.db"));
            int before = st.ConfigHistory().Count;
            st.SaveConfigHistory(System.Text.Json.JsonSerializer.Serialize(new RuleConfig { VagueMaxChars = 80 }), "test");
            var last = System.Text.Json.JsonSerializer.Deserialize<RuleConfig>(st.ConfigHistory()[0].Json);
            return st.ConfigHistory().Count == before + 1 && last?.VagueMaxChars == 80;
        });

        // ---- 에이전트 연동 (MCP) · 프로젝트 맥락 ----
        Try("skill-candidates", () => Advisor.PickSkillCandidates(new List<SkillInfo>
        {
            new("ppt-maker", "프레젠테이션 슬라이드 생성", ""), new("db-tool", "데이터베이스 마이그레이션", ""), new("img-gen", "이미지 생성", ""),
        }, "PPT 슬라이드 만들어줘").First().Name == "ppt-maker");
        Try("mcp-tools", () => McpServer.Tools.Length == 4 && McpServer.Tools.Select(t => t.Name).Distinct().Count() == 4
            && McpServer.Tools.All(t => { using var d = System.Text.Json.JsonDocument.Parse(t.SchemaJson); return d.RootElement.TryGetProperty("type", out _); }));
        Try("mcp-registry", () => McpServer.Registry.Length == 4 && McpServer.Registry.All(r => r.ParsedSchema.ValueKind == System.Text.Json.JsonValueKind.Object && r.Handler != null));
        var proj = Path.Combine(tmp, "ctx-proj");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "AGENTS.md"), "# Demo\n\n장비 운영 화면 데모 프로젝트입니다.\n\n| 파일 | 역할 |\n|---|---|\n| `MainWindow.xaml.cs` | 이력 탭 필터와 화면 |\n| `Store.cs` | SQLite 쿼리 |\n| `Charts.cs` | 막대 그래프 |\n");
        foreach (var f in new[] { "MainWindow.xaml.cs", "Store.cs", "Charts.cs" }) File.WriteAllText(Path.Combine(proj, f), "// demo");
        var workCtx = Advisor.BuildWorkContext("방금 거에서 그것도 같이 반영해줘", proj, "ctx-proj",
            new[] { "MainWindow.xaml.cs의 이력 탭 필터에 프로젝트 선택을 추가하고 Store.cs 쿼리를 수정해줘" });
        Try("context-scan", () => workCtx.ProjectName == "ctx-proj" && workCtx.IsFollowUp
            && workCtx.SelectedFiles.Any(f => f.Contains("MainWindow.xaml.cs") || f.Contains("Store.cs")) && workCtx.SessionGoal.Contains("이력 탭"));
        Try("context-classify-followup", () => Advisor.Classify("방금 거에서 그것도 같이 처리해", workCtx) == "code");
        Try("context-improved-prompt", () =>
        {
            var a = new Analysis { Context = workCtx, TaskType = Advisor.Classify("방금 거에서 그것도 같이 반영해줘", workCtx), Lints = Advisor.Lint("방금 거에서 그것도 같이 반영해줘", workCtx) };
            var t = Advisor.BuildImprovedPrompt("방금 거에서 그것도 같이 반영해줘", a, "Claude Sonnet 5.5");
            return t.Contains("[작업 맥락]") && t.Contains("ctx-proj") && t.Contains("MainWindow.xaml.cs") && !t.Contains("(여기에 경로를 적어주세요");
        });
        Try("knn-project-boost", () => Advisor.Knn(new float[] { 1, 0, 0 }, new List<PromptRow>
        {
            new() { Id = 1, Project = "OtherApp", Text = "a", Embedding = new float[] { 0.96f, 0.28f, 0 } },
            new() { Id = 2, Project = "ctx-proj", Text = "b", Embedding = new float[] { 0.90f, 0.43f, 0 } },
        }, 2, "ctx-proj") is var h && h.Count == 2 && h[0].Row.Project == "ctx-proj");
        Try("context-history-enrich", () =>
        {
            var hist = new List<PromptRow>
            {
                new() { Id = 10, Session = "s1", Project = "ctx-proj", Text = "Charts.cs 막대 그래프 색상 수정", InTok = 1200, OutTok = 800, SegTurns = 2, Label = 1 },
                new() { Id = 11, Session = "s1", Project = "ctx-proj", Text = "Charts.cs 게이지 애니메이션 오류 수정", InTok = 2400, OutTok = 1500, SegTurns = 5, Fail = 1, Label = -1 },
            };
            var c = Advisor.AnalyzePromptContext("막대 그래프 여백도 조정해줘", proj, "ctx-proj", hist);
            return c.HistoricalSampleCount == 2 && Math.Abs(c.HistoricalFailRate - 0.5) < 0.01 && c.CandidateFiles.Any(x => x.File.Contains("Charts.cs"));
        });
        Try("neighbor-risk-lint", () => Advisor.CheckNeighborFailureRisk(new List<NeighborInfo>
        {
            new(new PromptRow { Id = 1, InTok = 3000, OutTok = 2000, SegTurns = 7, Fail = 2 }, 0.82),
            new(new PromptRow { Id = 2, InTok = 1500, OutTok = 900, SegTurns = 2 }, 0.75),
        }) is { Id: "neighbor-risk" });
        Try("write-knowledge-export", () =>
        {
            using var st = new Store(Path.Combine(tmp, "demo.db"));
            return Advisor.WriteKnowledgeExport(Path.Combine(tmp, "knowledge-out"), st, new List<string>(), "", DateTime.Now).prompts > 0;
        });

        // ---- 한/영 · 표시 경로 · crash.log ----
        Try("xaml-en-coverage", () =>
        {
            using var st = typeof(SelfTest).Assembly.GetManifestResourceStream("xaml-source")!;
            var xaml = new StreamReader(st).ReadToEnd();
            var missing = System.Text.RegularExpressions.Regex.Matches(xaml, "(?:Title|Text|Content|Header|ToolTip|AutomationProperties\\.Name)=\"([^\"]*)\"")
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
                .Where(v => v.Any(c => c is >= '가' and <= '힣') && !L.XamlKeys.Contains(v.Trim())).Distinct().ToList();
            if (missing.Count > 0) lines.Add("  missing: " + string.Join(" | ", missing.Take(5)));
            return missing.Count == 0;
        });
        Try("loc-switch", () => { L.Init("en"); var en = Advisor.TaskLabel("code"); L.Init("ko"); return en == "Code change" && Advisor.TaskLabel("code") == "코드 수정"; });
        Try("display-path-hides-user", () => !Store.DisplayPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "x", "y.db"))
            .Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) || Environment.UserName.Length == 0);
        Try("crash-log", () =>
        {
            CrashLog.Write("selftest", new InvalidOperationException(@"boom at C:\Users\someone\file.cs"));
            var t = File.ReadAllText(CrashLog.PathOf);
            return t.Contains("boom") && !t.Contains(@"C:\Users\someone");
        });

        L.Init(wasEn ? "en" : "ko");
        int pass = lines.Count(l => l.StartsWith("PASS"));
        lines.Insert(0, $"Prompt Advisor v{App.Version} selftest: {pass}/{lines.Count} PASS ({DateTime.Now:yyyy-MM-dd HH:mm})");
        File.WriteAllText(Path.Combine(Store.DataDir, "selftest-result.txt"), string.Join("\n", lines));
        return fail;
    }
}
