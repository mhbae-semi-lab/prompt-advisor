namespace PromptAdvisor;

/// <summary>
/// 현장 검증 절차 (재현 → 비교 → Shadow → 전환 · 롤백) 의 계산부. 화면은 MainWindow.Verify.cs.
/// </summary>
public static class Verification
{
    public sealed record SetResult(string Text, string Task, List<string> RuleIds, long EstOut)
    {
        public string Preview => Text.Length > 70 ? Text[..70].Replace("\n", " ") + "…" : Text.Replace("\n", " ");
        public string RuleText => string.Join(", ", RuleIds.Select(Advisor.RuleName));
    }

    public sealed record CompareRow(string Preview, int Before, int After, string Added, string Removed)
    {
        public bool Changed => Added.Length > 0 || Removed.Length > 0;
        public string ChangeText => !Changed ? "" : string.Join("  ", new[] { Added.Length > 0 ? "+" + Added : "", Removed.Length > 0 ? "−" + Removed : "" }.Where(x => x.Length > 0));
    }

    /// <summary>재현: 세트의 각 프롬프트를 지금 규칙으로 돌린 결과.</summary>
    public static List<SetResult> Run(IEnumerable<string> prompts, RuleConfig cfg)
        => prompts.Select(p =>
        {
            var task = Advisor.Classify(p);
            return new SetResult(p, task, Advisor.Lint(p, null, cfg).Select(l => l.Id).ToList(), Advisor.BaseOutputForTask(task));
        }).ToList();

    /// <summary>비교: 같은 세트를 두 설정으로 돌려 경고가 늘거나 준 항목만 표시한다.</summary>
    public static List<CompareRow> Compare(IEnumerable<string> prompts, RuleConfig before, RuleConfig after)
        => prompts.Select(p =>
        {
            var a = Advisor.Lint(p, null, before).Select(l => l.Id).ToHashSet();
            var b = Advisor.Lint(p, null, after).Select(l => l.Id).ToHashSet();
            var prev = p.Length > 60 ? p[..60].Replace("\n", " ") + "…" : p.Replace("\n", " ");
            return new CompareRow(prev, a.Count, b.Count, string.Join(", ", b.Except(a).Select(Advisor.RuleName)), string.Join(", ", a.Except(b).Select(Advisor.RuleName)));
        }).ToList();

    public sealed record ShadowSummary(int N, double OnTopicRate, double AvgJudge, int LlmNotWorse, bool CanSwitch, string Reason);

    public const int ShadowMinSamples = 10;
    public const double ShadowMinOnTopic = 0.9;
    public const double ShadowMinJudge = 70;

    /// <summary>Shadow: LLM 개선안 기록이 기준(표본 10건 · 주제 일치 90% · 평균 심사 70점)을 넘으면 전환 가능.</summary>
    public static ShadowSummary Shadow(IReadOnlyList<Store.ShadowRow> rows)
    {
        int n = rows.Count;
        if (n == 0) return new(0, 0, 0, 0, false, L.T("Shadow 기록이 없습니다. 개선안을 만들면 LLM안이 옆 칸에 쌓입니다.", "No shadow records yet. Generate rewrites and the LLM version is logged alongside."));
        double onTopic = rows.Count(r => r.OnTopic) / (double)n;
        var judged = rows.Where(r => r.Judge >= 0).ToList();
        double judge = judged.Count > 0 ? judged.Average(r => r.Judge) : 0;
        int notWorse = rows.Count(r => r.OnTopic && r.LlmLints <= r.RuleLints);
        var why = new List<string>();
        if (n < ShadowMinSamples) why.Add(L.T($"표본 {n}/{ShadowMinSamples}건", $"{n}/{ShadowMinSamples} samples"));
        if (onTopic < ShadowMinOnTopic) why.Add(L.T($"주제 일치 {onTopic:P0} < {ShadowMinOnTopic:P0}", $"on-topic {onTopic:P0} < {ShadowMinOnTopic:P0}"));
        if (judge < ShadowMinJudge) why.Add(L.T($"평균 심사 {judge:F0} < {ShadowMinJudge:F0}점", $"avg review {judge:F0} < {ShadowMinJudge:F0}"));
        bool ok = why.Count == 0;
        return new(n, onTopic, judge, notWorse, ok, ok
            ? L.T("기준 통과 — LLM안을 먼저 채택하도록 전환할 수 있습니다 (검증 실패 시 규칙안으로 대체).", "Criteria met — you can switch to LLM-first (falls back to rules when checks fail).")
            : L.T("전환 불가: ", "Cannot switch yet: ") + string.Join(" · ", why));
    }

    public sealed record RewriteSummary(int Sent, int Matched, double ImprovedTurns, double ImprovedFail, int BaselineN, double BaselineTurns, double BaselineFail);

    /// <summary>
    /// 개선 뒤 실제 턴: 보낸 개선본 중 실제 기록과 이어진 것의 평균 턴 · 정정과,
    /// 같은 경고(개선 전 경고 중 하나라도)가 떴던 다른 기존 요청의 평균을 비교한다.
    /// </summary>
    public static RewriteSummary Rewrites(Store store, RuleConfig cfg)
    {
        var rws = store.Rewrites();
        var matched = rws.Where(r => r.ActualTurns != null).ToList();
        var improvedIds = matched.Select(r => r.PromptId!.Value).ToHashSet();
        var before = matched.SelectMany(r => r.RulesBefore.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
        var baseline = store.All().Where(p => !improvedIds.Contains(p.Id))
            .Where(p => Advisor.LintRewrite(p.Text, cfg).Any(l => before.Contains(l.Id))).ToList();
        return new(rws.Count, matched.Count,
            matched.Count > 0 ? matched.Average(r => r.ActualTurns!.Value) : 0,
            matched.Count > 0 ? matched.Average(r => r.ActualFail!.Value) : 0,
            baseline.Count,
            baseline.Count > 0 ? baseline.Average(p => p.SegTurns) : 0,
            baseline.Count > 0 ? baseline.Average(p => p.Fail) : 0);
    }

    /// <summary>보고서용 측정 요약 (규칙별 학습 수치 · 개선 추적 · Shadow · 예측 보정).</summary>
    public static string MeasureReport(Store store, RuleConfig cfg)
    {
        var sb = new System.Text.StringBuilder();
        var all = store.All();
        sb.AppendLine($"prompts={all.Count} projects={all.Select(p => p.Project).Distinct().Count()} sources={string.Join(",", all.GroupBy(p => p.Source).Select(g => $"{g.Key}:{g.Count()}"))}");
        sb.AppendLine("rule	with_n	without_n	with_fail	without_fail	with_turns	without_turns	extra_tok	enough");
        foreach (var s in RuleImpact.Compute(all, cfg).Values.OrderByDescending(x => x.WithN))
            sb.AppendLine($"{s.RuleId}	{s.WithN}	{s.WithoutN}	{s.WithFail:F2}	{s.WithoutFail:F2}	{s.WithTurns:F2}	{s.WithoutTurns:F2}	{s.ExtraTokens}	{s.Enough}");
        var rw = Rewrites(store, cfg);
        sb.AppendLine($"rewrites sent={rw.Sent} matched={rw.Matched} improved_turns={rw.ImprovedTurns:F2} improved_fail={rw.ImprovedFail:F2} baseline_n={rw.BaselineN} baseline_turns={rw.BaselineTurns:F2} baseline_fail={rw.BaselineFail:F2}");
        var before = store.Rewrites().Sum(r => r.RulesBefore.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
        var after = store.Rewrites().Sum(r => r.RulesAfter.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
        sb.AppendLine($"rewrite_warnings before={before} after={after}");
        var sh = Shadow(store.ShadowRows());
        sb.AppendLine($"shadow n={sh.N} on_topic={sh.OnTopicRate:F2} avg_judge={sh.AvgJudge:F1} llm_not_worse={sh.LlmNotWorse} can_switch={sh.CanSwitch}");
        var calib = store.CalibrationRows();
        var (factor, n) = Advisor.Calibration(calib, "code");
        sb.AppendLine($"calibration samples={calib.Count} error_pct={Advisor.PredictionErrorPct(calib):F1} code_factor={factor:F2} (n={n})");
        var eq = all.Where(p => Equipment.IsEquipmentRequest(p.Text)).ToList();
        sb.AppendLine($"equipment_requests={eq.Count} missing_any={eq.Count(p => Equipment.Missing(p.Text).Count > 0)}");
        return sb.ToString();
    }

    public static string Describe(RewriteSummary s) => s.Matched == 0
        ? L.T($"보낸 개선본 {s.Sent}건 · 실제 기록과 이어진 것 0건 (보낸 뒤 [가져오기]를 하면 이어집니다)",
              $"{s.Sent} rewrites sent · 0 matched to real records yet (import after sending)")
        : L.T($"개선본 {s.Matched}건 평균 {s.ImprovedTurns:F1}턴 · 정정 {s.ImprovedFail:F1}회 ↔ 같은 경고가 있던 기존 요청 {s.BaselineN}건 평균 {s.BaselineTurns:F1}턴 · 정정 {s.BaselineFail:F1}회",
              $"{s.Matched} rewrites averaged {s.ImprovedTurns:F1} turns · {s.ImprovedFail:F1} corrections ↔ {s.BaselineN} earlier requests with the same warnings averaged {s.BaselineTurns:F1} turns · {s.BaselineFail:F1}");
}
