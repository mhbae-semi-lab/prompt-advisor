using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PromptAdvisor;

/// <summary>검증 탭(재현 → 비교 → Shadow → 전환 · 롤백 · 개선 추적), 도움말, 처음 안내, 예시 데이터, 업데이트.</summary>
public partial class MainWindow
{
    private List<string> _reproSet = new();

    private sealed record ConfigView(long Id, string CreatedAt, string Note, string Summary, RuleConfig Config);
    private sealed record RewriteView(string When, string Original, string Kind, string Lints, string Turns, string Fail);

    private void InitVerifyTab()
    {
        var c = Advisor.Rules;
        CandEquipment.IsChecked = c.EquipmentLint;
        CandFormat.IsChecked = c.FormatLint;
        CandVague.Text = c.VagueMaxChars.ToString();
        CandPaste.Text = c.LargePasteChars.ToString();
        CandLog.Text = c.LogLinesMin.ToString();
        if (_store.ConfigHistory().Count == 0)
            _store.SaveConfigHistory(JsonSerializer.Serialize(c), L.T("처음 설정", "Initial settings"));
        RefreshVerifyTab();
    }

    private void RefreshVerifyTab()
    {
        CurrentConfigText.Text = Advisor.Rules.Describe();
        var shadow = Verification.Shadow(_store.ShadowRows());
        ShadowGrid.ItemsSource = _store.ShadowRows();
        ShadowSummaryText.Text = shadow.N == 0 ? shadow.Reason
            : L.T($"Shadow {shadow.N}건 · 주제 일치 {shadow.OnTopicRate:P0} · 평균 심사 {shadow.AvgJudge:F0}점 · LLM 안 경고가 규칙안 이하 {shadow.LlmNotWorse}건 — ",
                  $"{shadow.N} shadow runs · on-topic {shadow.OnTopicRate:P0} · avg review {shadow.AvgJudge:F0} · LLM warnings ≤ rule in {shadow.LlmNotWorse} — ") + shadow.Reason;
        LlmSwitchBtn.IsEnabled = shadow.CanSwitch && !Advisor.Rules.LlmPrimary;
        LlmSwitchBtn.ToolTip = shadow.Reason;
        SwitchStatusText.Text = (Advisor.Rules.LlmPrimary ? L.T("지금: LLM 안 우선 (검증 실패 시 규칙안). ", "Now: LLM first (rules when checks fail). ") : L.T("지금: 규칙안 우선. ", "Now: rules first. "))
            + L.T($"LLM 전환 기준: 표본 {Verification.ShadowMinSamples}건 이상 · 주제 일치 {Verification.ShadowMinOnTopic:P0} 이상 · 평균 심사 {Verification.ShadowMinJudge:F0}점 이상.",
                  $"Switch criteria: {Verification.ShadowMinSamples}+ samples · on-topic ≥ {Verification.ShadowMinOnTopic:P0} · avg review ≥ {Verification.ShadowMinJudge:F0}.");
        ConfigHistoryGrid.ItemsSource = _store.ConfigHistory().Select(h =>
        {
            RuleConfig cfg;
            try { cfg = JsonSerializer.Deserialize<RuleConfig>(h.Json) ?? new(); } catch { cfg = new(); }
            return new ConfigView(h.Id, h.CreatedAt.Replace('T', ' '), h.Note, cfg.Describe(), cfg);
        }).ToList();

        var rs = Verification.Rewrites(_store, Advisor.Rules);
        RewriteSummaryText.Text = Verification.Describe(rs);
        RewriteGrid.ItemsSource = _store.Rewrites().Select(r =>
        {
            int before = r.RulesBefore.Split(',', StringSplitOptions.RemoveEmptyEntries).Length, after = r.RulesAfter.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
            string when = DateTime.TryParse(r.CreatedAt, out var d) ? d.ToLocalTime().ToString("MM-dd HH:mm") : r.CreatedAt;
            string orig = r.Original.Length > 60 ? r.Original[..60].Replace("\n", " ") + "…" : r.Original.Replace("\n", " ");
            return new RewriteView(when, orig, r.Kind == "llm" ? "LLM" : L.T("규칙", "rule"), $"{before} → {after}",
                r.ActualTurns?.ToString() ?? L.T("대기", "pending"), r.ActualFail?.ToString() ?? "-");
        }).ToList();
    }

    private List<string> CurrentReproSet()
    {
        var tag = (ReproSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "example";
        return tag == "mine" ? _store.Query("repro", 500).Select(r => r.Text).ToList() : SampleData.ExampleSet.ToList();
    }

    private void OnRunRepro(object sender, RoutedEventArgs e)
    {
        _reproSet = CurrentReproSet();
        if (_reproSet.Count == 0)
        {
            ReproSummary.Text = L.T("내 재현 세트가 비었습니다. 이력 탭에서 사례를 고르고 [재현 세트]를 누르세요.", "Your repro set is empty. Pick cases on the History tab and press [Repro set].");
            ReproGrid.ItemsSource = null;
            return;
        }
        var res = Verification.Run(_reproSet, Advisor.Rules);
        ReproGrid.ItemsSource = res;
        ReproSummary.Text = L.T($"{res.Count}건 · 경고 합계 {res.Sum(r => r.RuleIds.Count)}개 · 장비 정보가 빠진 사례 {res.Count(r => r.RuleIds.Any(id => id.StartsWith("eq-")))}건 (지금 설정: {Advisor.Rules.Describe()})",
                                $"{res.Count} cases · {res.Sum(r => r.RuleIds.Count)} warnings · {res.Count(r => r.RuleIds.Any(id => id.StartsWith("eq-")))} missing equipment info (current: {Advisor.Rules.Describe()})");
    }

    private void OnReproToAnalyze(object sender, RoutedEventArgs e)
    {
        if (ReproGrid.SelectedItem is not Verification.SetResult r) { AddStatus(L.T("재현 결과에서 행을 고르세요", "Pick a row in the repro results")); return; }
        InputBox.Text = r.Text;
        MainTabs.SelectedIndex = 0;
        OnAnalyze(this, new RoutedEventArgs());
    }

    private RuleConfig? CandidateConfig()
    {
        if (!int.TryParse(CandVague.Text, out var vague) || vague is < 10 or > 500
            || !int.TryParse(CandPaste.Text, out var paste) || paste is < 500 or > 200_000
            || !int.TryParse(CandLog.Text, out var log) || log is < 1 or > 200)
        {
            CompareSummary.Text = L.T("숫자 칸을 확인하세요: 범위 불명확 10~500, 대량 붙여넣기 500~200000, 로그 줄 수 1~200", "Check the numbers: vague 10–500, large paste 500–200000, log lines 1–200");
            return null;
        }
        return Advisor.Rules with { EquipmentLint = CandEquipment.IsChecked == true, FormatLint = CandFormat.IsChecked == true, VagueMaxChars = vague, LargePasteChars = paste, LogLinesMin = log };
    }

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        var cand = CandidateConfig();
        if (cand == null) return;
        var set = _reproSet.Count > 0 ? _reproSet : CurrentReproSet();
        var rows = Verification.Compare(set, Advisor.Rules, cand);
        CompareGrid.ItemsSource = rows;
        CompareSummary.Text = cand == Advisor.Rules
            ? L.T("바꿀 설정이 지금과 같습니다. 위 칸을 바꾼 뒤 비교하세요.", "The candidate equals the current settings. Change something above first.")
            : L.T($"{set.Count}건 중 {rows.Count(r => r.Changed)}건이 바뀝니다 (경고 {rows.Sum(r => r.Before)} → {rows.Sum(r => r.After)}). 바꿀 설정: {cand.Describe()}",
                  $"{rows.Count(r => r.Changed)} of {set.Count} cases change (warnings {rows.Sum(r => r.Before)} → {rows.Sum(r => r.After)}). Candidate: {cand.Describe()}");
    }

    private void ApplyRules(RuleConfig cfg, string note)
    {
        Advisor.Rules = cfg;
        App.Settings.Rules = cfg;
        App.Settings.Save();
        _store.SaveConfigHistory(JsonSerializer.Serialize(cfg), note);
        RuleImpact.Invalidate();
        RefreshVerifyTab();
        AddStatus(L.T("규칙 설정을 바꿨습니다: ", "Rules changed: ") + cfg.Describe());
    }

    private void OnApplyCandidate(object sender, RoutedEventArgs e)
    {
        var cand = CandidateConfig();
        if (cand == null || cand == Advisor.Rules) { SwitchStatusText.Text = L.T("바꿀 설정이 지금과 같거나 잘못됐습니다 ([2 비교]에서 확인).", "The candidate is the same or invalid (see [2 Compare])."); return; }
        ApplyRules(cand, L.T("비교 후 전환", "switched after compare"));
    }

    private void OnSwitchLlm(object sender, RoutedEventArgs e)
    {
        var s = Verification.Shadow(_store.ShadowRows());
        if (!s.CanSwitch) { SwitchStatusText.Text = s.Reason; return; }
        ApplyRules(Advisor.Rules with { LlmPrimary = true }, L.T("Shadow 기준 통과 → LLM 안 우선", "shadow criteria met → LLM first"));
    }

    /// <summary>롤백: 지금 바로 앞 설정으로 되돌린다 (이력은 지우지 않고 '되돌림' 항목을 새로 남김).</summary>
    private void OnRollback(object sender, RoutedEventArgs e)
    {
        var hist = _store.ConfigHistory();
        var prev = hist.Skip(1).Select(h => { try { return JsonSerializer.Deserialize<RuleConfig>(h.Json); } catch { return null; } })
                       .FirstOrDefault(c => c != null && c != Advisor.Rules);
        if (prev == null) { SwitchStatusText.Text = L.T("되돌릴 이전 설정이 없습니다.", "No previous settings to roll back to."); return; }
        ApplyRules(prev, L.T("되돌림", "rolled back"));
        CandEquipment.IsChecked = prev.EquipmentLint; CandFormat.IsChecked = prev.FormatLint;
        CandVague.Text = prev.VagueMaxChars.ToString(); CandPaste.Text = prev.LargePasteChars.ToString(); CandLog.Text = prev.LogLinesMin.ToString();
    }

    // ---------- 예시 데이터 · 재시작 ----------
    private void Restart(params string[] args)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var keep = Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--demo").ToList();
        // --data 값은 그대로 넘긴다 (예시 ↔ 내 데이터 전환 시에도 같은 기준 폴더)
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in keep.Concat(args)) psi.ArgumentList.Add(a);
        _exitRequested = true;
        Process.Start(psi);
        Application.Current.Shutdown();
    }

    private void OnOpenDemo(object sender, RoutedEventArgs e) => Restart("--demo");
    private void OnLeaveDemo(object sender, RoutedEventArgs e) => Restart();

    // ---------- 업데이트 ----------
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = L.T("확인 중…", "Checking…");
        var r = await Updater.CheckAsync(apply: false, null);
        UpdateStatus.Text = r;
        if (r.Contains("v") && (r.Contains("있습니다") || r.Contains("available"))
            && MessageBox.Show(r + L.T("\n\n지금 받아서 적용하고 다시 시작할까요?", "\n\nDownload, apply and restart now?"), "Prompt Advisor", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _exitRequested = true;
            UpdateStatus.Text = await Updater.CheckAsync(apply: true, null);
        }
    }

    private void OnUpdateOnStartChanged(object sender, RoutedEventArgs e)
    {
        App.Settings.UpdateCheckOnStart = UpdateOnStartBox.IsChecked == true;
        App.Settings.Save();
    }

    // ---------- 처음 안내 ----------
    private void OnShowWelcome(object sender, RoutedEventArgs e) => ShowWelcome();

    private async void ShowWelcome()
    {
        bool ollama = await _ollama.IsAvailable();
        var w = new Window
        {
            Title = L.T("Prompt Advisor 시작하기", "Getting started with Prompt Advisor"), Width = 620, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = L.T("보내기 전에 낭비를 막는 프롬프트 점검기", "Check prompts before you send them"), FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), LineHeight = 20,
            Text = L.T(
                "1. [가져오기·설정] 탭에서 이 PC 의 Claude Code · Cursor 기록을 가져옵니다. 기록은 이 PC 의 데이터 폴더에만 저장되고 밖으로 나가지 않습니다.\n" +
                "2. 새 프롬프트를 분석하면, 내 기록에서 같은 경고가 떴던 요청이 정정 · 턴을 얼마나 더 썼는지 보여줍니다. 장비 SW 요청이면 대상 장비 · 버전 · 로그 · 재현 조건 · 기대 결과가 빠졌는지 봅니다.\n" +
                "3. [개선 프롬프트 생성] 은 규칙안을 바로 만들고, Ollama 가 있으면 로컬 LLM 안을 옆에 나란히 보여줍니다.\n" +
                "4. 기록이 아직 없으면 [예시 데이터로 둘러보기] 로 합성 예시(가상 프로젝트 3개)를 먼저 볼 수 있습니다.",
                "1. Import this PC's Claude Code / Cursor history on the [Import & Settings] tab. It is stored only in this PC's data folder and never leaves it.\n" +
                "2. When you analyze a new prompt, it shows how many more corrections and turns past requests with the same warning took. For equipment software requests it checks target equipment, version, log, repro conditions and expected result.\n" +
                "3. [Generate rewrite] makes a rule-based version at once and, if Ollama is installed, shows a local-LLM version next to it.\n" +
                "4. No history yet? [Try sample data] opens a synthetic example (3 fictional projects)."),
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
            Text = ollama ? L.T("Ollama: 연결됨 — 임베딩 · LLM 개선안을 쓸 수 있습니다.", "Ollama: connected — embeddings and LLM rewrites are available.")
                          : L.T("Ollama: 없음 — 그래도 린트 · 통계 · 규칙 개선안은 모두 동작합니다. 쓰려면: winget install Ollama.Ollama → ollama pull exaone3.5:2.4b · ollama pull bge-m3",
                                "Ollama: not found — lint, stats and rule-based rewrites all work without it. To add it: winget install Ollama.Ollama → ollama pull exaone3.5:2.4b · ollama pull bge-m3"),
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button B(string glyph, string text, Action act)
        {
            var b = IconButton(glyph, text);
            b.FontSize = 13; b.Padding = new Thickness(12, 5, 12, 5); b.Margin = new Thickness(6, 0, 0, 0);
            b.Click += (_, _) => { App.Settings.FirstRunDone = true; App.Settings.Save(); w.Close(); act(); };
            return b;
        }
        buttons.Children.Add(B("", L.T("예시 데이터로 둘러보기", "Try sample data"), () => Restart("--demo")));
        buttons.Children.Add(B("", L.T("내 기록 가져오기", "Import my history"), () => MainTabs.SelectedIndex = 3));
        var close = B("", L.T("닫기", "Close"), () => { });
        close.IsCancel = true;
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        w.Content = panel;
        L.Apply(w);
        w.ShowDialog();
    }

    // ---------- 도움말 ----------
    private static string HelpContent() => L.T(
        """
        개인정보 · 데이터
        - 모든 기록은 이 PC 의 데이터 폴더(%LOCALAPPDATA%\prompt-advisor-data)에만 저장됩니다. 서버 · 계정 · 원격 수집이 없습니다.
        - 가져오기는 이 PC 의 Claude Code(~/.claude/projects) · Cursor(state.vscdb) 기록을 읽기 전용으로 읽습니다.
        - LLM 은 로컬 Ollama 만 씁니다. 보내기 전에 경로 · 메일 · API 키 · 토큰 · IP · 전화번호를 가리고, 개선안 말풍선의 [LLM 에 보낸 내용]에서 그대로 확인할 수 있습니다. Ollama 주소가 이 PC 가 아니면 보낼 때마다 묻습니다.
        - API 키를 쓰지 않습니다. 오류는 이 PC 의 오류 기록과 crash.log 에만 남습니다.
        - 앱을 제거해도 데이터 폴더는 남습니다. 지우려면 [데이터 폴더 열기] 후 폴더를 삭제하세요.

        내 데이터 형식
        - Claude Code: 대화 jsonl. 형식이 바뀌어 읽지 못하는 줄이 많으면 "형식이 바뀐 것 같다"고 알리고 몇 번째 줄인지 보여줍니다.
        - Cursor: state.vscdb 의 cursorDiskKV 표 (토큰 수는 없음, 턴 · 정정만).
        - CSV: 머리줄 source,session,project,created_at,in_tok,out_tok,turns,fail,label,text (text 만 필수). 잘못된 줄은 줄 번호와 이유를 보여주고 건너뜁니다. [CSV 내보내기]와 같은 형식이라 백업 · 다른 도구 기록 옮기기에 씁니다.

        자주 묻는 질문
        - 예측이 실제와 다릅니다 → 분석한 프롬프트를 실제로 보내고 나중에 [가져오기]를 하면 예측과 실측이 이어져 보정됩니다. 표본이 3건 미만이면 보정하지 않습니다.
        - "내 기록:" 문장이 안 나옵니다 → 그 경고가 뜬 요청과 안 뜬 요청이 각각 3건 이상 있어야 나옵니다.
        - 장비 SW 린트가 일반 요청에도 뜹니다 → 검증 탭 [2 비교]에서 끄고 같은 세트로 전/후를 본 뒤 [4 전환]에서 적용하세요. [이전 설정으로 되돌리기]로 언제든 돌아갑니다.
        - 개선안이 원래 주제를 벗어났습니다 → LLM 안은 Shadow 로만 보여주고, 주제가 벗어나면 기록에 남긴 뒤 규칙안을 씁니다.
        - SmartScreen 경고 → 서명하지 않은 설치 파일이라 "추가 정보 → 실행"을 눌러야 합니다.

        단축키
        - Ctrl+Enter 분석 · Ctrl+K 입력창 · Ctrl+F 이력 검색 · Ctrl+1~5 탭 이동 · Ctrl+Shift+P 어디서든 창 불러오기 (트레이 상주)

        라이선스 · 출처
        - MIT License. Microsoft.Data.Sqlite (MIT), Velopack (MIT). 모델 가격표의 원천은 각 제공사 · Artificial Analysis 공개 자료이며 참고용입니다.
        - 이 앱은 AI 코딩 도구(Claude Code)의 도움을 받아 만들었습니다.
        """,
        """
        Privacy and data
        - Everything is stored only in this PC's data folder (%LOCALAPPDATA%\prompt-advisor-data). No server, account or telemetry.
        - Import reads this PC's Claude Code (~/.claude/projects) and Cursor (state.vscdb) history read-only.
        - Only a local Ollama is used as LLM. Paths, emails, API keys, tokens, IPs and phone numbers are masked before sending; see [What was sent to the LLM] in the rewrite bubble. If the Ollama URL is not this PC, you are asked every time.
        - No API keys are used. Errors stay in this PC's error log and crash.log.
        - Uninstalling keeps the data folder. To remove it, use [Open data folder] and delete the folder.

        Your data formats
        - Claude Code: conversation jsonl. If many lines cannot be read, the app says the format seems to have changed and shows the line numbers.
        - Cursor: the cursorDiskKV table in state.vscdb (no token counts, only turns and corrections).
        - CSV: header source,session,project,created_at,in_tok,out_tok,turns,fail,label,text (only text is required). Bad lines are skipped with line number and reason. Same format as [Export CSV], so it works for backups and for moving history from other tools.

        FAQ
        - Predictions are off → send the analyzed prompt and [Import] later; predictions are matched with actual usage and calibrated. With fewer than 3 samples no calibration is applied.
        - No "Your history:" line → it needs at least 3 requests with and 3 without that warning.
        - Equipment lint fires on general requests → turn it off in Verify [2 Compare], check before/after on the same set, apply in [4 Switch]. [Roll back] restores the previous settings any time.
        - The rewrite drifted off topic → the LLM version is only shown as a shadow; when it drifts, it is logged and the rule-based version is used.
        - SmartScreen warning → the installer is unsigned; choose "More info → Run anyway".

        Shortcuts
        - Ctrl+Enter analyze · Ctrl+K input · Ctrl+F search history · Ctrl+1–5 tabs · Ctrl+Shift+P bring the window from anywhere (tray)

        License and sources
        - MIT License. Microsoft.Data.Sqlite (MIT), Velopack (MIT). Model prices come from providers' and Artificial Analysis public data and are for reference only.
        - This app was built with help from an AI coding tool (Claude Code).
        """);
}
