using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PromptAdvisor;

/// <summary>이력 · 통계 · 가져오기 · 백업 · 설정 · Ollama.</summary>
public partial class MainWindow
{
    // ---------- 이력 ----------
    private static Border KpiCard(string title, string value, string sub) => new()
    {
        CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 6), MinWidth = 170,
        BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        Background = new SolidColorBrush(Color.FromArgb(14, 128, 128, 128)),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = value, FontSize = 21, FontWeight = FontWeights.Bold },
                new TextBlock { Text = sub, FontSize = 12, Opacity = 0.9 },
            },
        },
    };

    private void RefreshStats()
    {
        var (total, sessions, inTok, outTok, bySource) = _store.Stats();
        var all = _store.All();
        long waste = all.Where(r => r.Label == -1 || (r.Label == 0 && r.AutoLabel == "bad")).Sum(r => r.TotalTok);
        double failRate = all.Count > 0 ? 100.0 * all.Count(r => r.Fail > 0) / all.Count : 0;
        double avgTurn = all.Count > 0 ? all.Average(r => r.SegTurns) : 0;
        var calibRows = _store.CalibrationRows();
        string accuracy = calibRows.Count > 0 ? L.T($" · 예측 평균 오차 ±{Advisor.PredictionErrorPct(calibRows):F0}% (실측 {calibRows.Count}건)", $" · prediction error ±{Advisor.PredictionErrorPct(calibRows):F0}% (n={calibRows.Count})") : "";
        StatsText.Text = L.T($"총 {total:N0}건 · 세션 {sessions:N0} · 입력 {inTok:N0} tok · 출력 {outTok:N0} tok · 평균 턴 {avgTurn:F1} · 정정 신호 비율 {failRate:F0}%",
                             $"{total:N0} prompts · {sessions:N0} sessions · in {inTok:N0} tok · out {outTok:N0} tok · avg {avgTurn:F1} turns · {failRate:F0}% needed corrections")
            + L.T(" · 출처: ", " · sources: ") + string.Join(", ", bySource.Select(kv => $"{new PromptRow { Source = kv.Key }.DisplaySource} {kv.Value}"))
            + (waste > 0 ? L.T($" · 최악 라벨 누적 {waste:N0} tok", $" · worst-labeled {waste:N0} tok") : "") + accuracy;
        BottomStatus.Text = L.T($"DB {total:N0}건 · 세션 {sessions:N0}개 · {Store.DisplayPath(Store.DbPath)}", $"DB {total:N0} prompts · {sessions:N0} sessions · {Store.DisplayPath(Store.DbPath)}");

        StatsCards.Children.Clear();
        StatsCards.Children.Add(KpiCard(L.T("총 프롬프트", "Prompts"), $"{total:N0}", L.T($"세션 {sessions:N0}개", $"{sessions:N0} sessions")));
        StatsCards.Children.Add(KpiCard(L.T("누적 토큰", "Total tokens"), $"{(inTok + outTok) / 1_000_000.0:F1}M", L.T($"입력 {inTok / 1_000_000.0:F1}M · 출력 {outTok / 1_000_000.0:F1}M", $"in {inTok / 1_000_000.0:F1}M · out {outTok / 1_000_000.0:F1}M")));
        StatsCards.Children.Add(KpiCard(L.T("정정 신호 비율", "Needed corrections"), $"{failRate:F0}%", L.T($"평균 턴 {avgTurn:F1}", $"avg {avgTurn:F1} turns")));
        StatsCards.Children.Add(KpiCard(L.T("예측 정확도", "Prediction accuracy"), calibRows.Count > 0 ? $"±{Advisor.PredictionErrorPct(calibRows):F0}%" : L.T("표본 없음", "no samples"),
            calibRows.Count > 0
                ? L.T($"실측 {calibRows.Count}건 · 미매칭 예측 {_store.PendingPredictionCount()}건", $"{calibRows.Count} measured · {_store.PendingPredictionCount()} pending")
                : L.T("분석 → 실제 전송 → 가져오기 후 쌓임", "builds up after analyze → send → import")));

        SourceChartPanel.Children.Clear();
        var bySrcTok = all.GroupBy(r => r.Source)
            .Select(g => (Label: new PromptRow { Source = g.Key }.DisplaySource, Value: (double)g.Sum(x => x.TotalTok), ValueText: L.T($"{g.Sum(x => x.TotalTok) / 1000.0:F0}k tok · {g.Count()}건", $"{g.Sum(x => x.TotalTok) / 1000.0:F0}k tok · {g.Count()}")))
            .OrderByDescending(x => x.Value).ToList();
        if (bySrcTok.Count > 0) SourceChartPanel.Children.Add(Charts.BarChart(bySrcTok, 980, -1));
    }

    private void BuildProjectFilterCombo()
    {
        var currentTag = (ProjectFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        ProjectFilterCombo.Items.Clear();
        ProjectFilterCombo.Items.Add(new ComboBoxItem { Content = L.T("전체 프로젝트", "All projects"), Tag = "" });
        foreach (var (proj, cnt) in _store.DistinctProjects())
            ProjectFilterCombo.Items.Add(new ComboBoxItem { Content = $"{proj} ({cnt:N0})", Tag = proj });
        ProjectFilterCombo.SelectedIndex = 0;
        foreach (ComboBoxItem item in ProjectFilterCombo.Items)
            if ((string)item.Tag == currentTag) { ProjectFilterCombo.SelectedItem = item; break; }
    }

    private void RefreshHistory()
    {
        var tag = (FilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var projFilter = (ProjectFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var allRows = _store.All();
        _medianTotal = allRows.Count > 0 ? allRows.Select(r => (double)r.TotalTok).OrderBy(x => x).ElementAt(allRows.Count / 2) : 0;
        HistoryGrid.ItemsSource = _store.Query(tag, 500, SearchBox?.Text ?? "", projFilter);
        TemplateCombo.ItemsSource = _store.Query("fav", 200);
        RefreshStats();
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { AddStatus(L.T("먼저 이력에서 행을 선택하세요", "Select a row first")); return; }
        _store.SetFavorite(Selected.Id, !Selected.Favorite);
        if (!Selected.Favorite) _store.SaveTemplateVersion(Selected.Id, Selected.Text, L.T("즐겨찾기 등록", "added to favorites"));
        AddStatus(Selected.Favorite ? L.T("즐겨찾기 해제 — 템플릿에서 빠집니다", "Removed from templates") : L.T("즐겨찾기 — 분석 탭 템플릿 목록에서 바로 불러올 수 있습니다", "Added — load it from the Templates list on the Analyze tab"));
        RefreshHistory();
    }

    private void OnToggleRepro(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { AddStatus(L.T("먼저 이력에서 행을 선택하세요", "Select a row first")); return; }
        bool on = !Selected.Repro;
        _store.SetRepro(Selected.Id, on);
        AddStatus(on ? L.T("재현 세트에 넣었습니다 — 검증 탭에서 규칙을 바꿀 때 이 사례로 전/후를 비교합니다", "Added to the repro set — used for before/after comparison on the Verify tab")
                     : L.T("재현 세트에서 뺐습니다", "Removed from the repro set"));
        RefreshHistory();
    }

    private void OnLoadTemplate(object sender, RoutedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is not PromptRow t) { AddStatus(L.T("템플릿이 없습니다 — 이력에서 [즐겨찾기]를 먼저 누르세요", "No templates — mark a row as Favorite in History first")); return; }
        InputBox.Text = t.Text;
        _loadedTemplateId = t.Id;
        _loadedTemplateText = t.Text;
        InputBox.Focus();
        AddStatus(L.T($"템플릿을 입력창에 넣었습니다 (버전 {_store.TemplateVersionCount(t.Id)}개) — 고쳐서 분석/복사하면 새 버전으로 저장됩니다", $"Template loaded ({_store.TemplateVersionCount(t.Id)} versions) — edits are saved as a new version when you analyze or copy"));
    }

    /// <summary>불러온 템플릿을 고쳐 실제로 쓴 경우에만 새 버전으로 기록.</summary>
    private void SaveTemplateVersionIfEdited(string text)
    {
        if (_loadedTemplateId <= 0 || text.Trim() == _loadedTemplateText.Trim()) return;
        if (_store.SaveTemplateVersion(_loadedTemplateId, text, L.T("템플릿 편집본", "edited template")))
            AddStatus(L.T($"템플릿 새 버전 저장 (총 {_store.TemplateVersionCount(_loadedTemplateId)}개)", $"New template version saved ({_store.TemplateVersionCount(_loadedTemplateId)} total)"));
    }

    private void OnShowTemplateVersions(object sender, RoutedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is not PromptRow t) { AddStatus(L.T("먼저 템플릿을 고르세요", "Pick a template first")); return; }
        var versions = _store.TemplateVersions(t.Id);
        if (versions.Count == 0) { AddChat(L.T("템플릿 버전", "Template versions"), L.T("아직 기록된 버전이 없습니다.", "No versions yet.")); return; }
        var sb = new System.Text.StringBuilder(L.T($"템플릿 버전 {versions.Count}개 (원본: {t.Preview})\n", $"{versions.Count} versions (original: {t.Preview})\n"));
        foreach (var v in versions)
            sb.AppendLine($"\n--- v{v.No} · {v.CreatedAt} · {v.Note} ---\n{(v.Text.Length > 300 ? v.Text[..300] + "…" : v.Text)}");
        AddChat(L.T("템플릿 버전", "Template versions"), sb.ToString());
    }

    private void OnWeeklyReport(object sender, RoutedEventArgs e)
    {
        var report = Advisor.WeeklyReport(_store.All(), DateTime.Now);
        MainTabs.SelectedIndex = 0;
        AddChat(L.T("주간 리포트", "Weekly report"), report);
    }

    private void OnBackupDb(object sender, RoutedEventArgs e)
    {
        try
        {
            var dst = Path.Combine(Store.DataDir, $"advisor-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            _store.BackupTo(dst);
            SettingsStatus.Text = L.T("백업 완료: ", "Backup saved: ") + Store.DisplayPath(dst);
            AddStatus(SettingsStatus.Text);
        }
        catch (Exception ex) { _store.LogError(L.T("백업", "Backup"), ex.Message); SettingsStatus.Text = L.T("백업 실패: ", "Backup failed: ") + ex.Message; }
    }

    /// <summary>백업 .db 로 되돌린다. 지금 DB 를 먼저 백업하고, 고른 파일이 이 앱의 DB 인지 확인한 뒤 다음 실행 때 교체한다.</summary>
    private void OnRestoreDb(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SQLite DB|*.db", InitialDirectory = Store.DataDir };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using (var test = new Store(dlg.FileName)) _ = test.Stats(); // 스키마 확인 (더 새 버전이면 예외)
            if (MessageBox.Show(L.T("지금 DB 를 백업한 뒤 고른 파일로 바꾸고 앱을 다시 시작합니다. 계속할까요?", "The current DB will be backed up, replaced with the chosen file, and the app restarted. Continue?"),
                    "Prompt Advisor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _store.BackupTo(Path.Combine(Store.DataDir, $"advisor-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
            File.Copy(dlg.FileName, Store.DbPath + ".restore", overwrite: true);
            Restart();
        }
        catch (Exception ex)
        {
            _store.LogError(L.T("복원", "Restore"), ex.Message);
            SettingsStatus.Text = L.T("복원 실패 (이 앱의 DB 가 아니거나 손상됨): ", "Restore failed (not a Prompt Advisor DB or damaged): ") + ex.Message;
        }
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Store.DataDir) { UseShellExecute = true });
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (IsLoaded && HistoryGrid != null) RefreshHistory(); }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshHistory(); }
    private void OnRelabel(object sender, RoutedEventArgs e) { Advisor.ComputeLabels(_store); RefreshHistory(); AddStatus(L.T("자동 라벨 재계산 완료", "Labels recomputed")); }

    private PromptRow? Selected => HistoryGrid.SelectedItem as PromptRow;

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected == null) return;
        PreviewBox.Text = Selected.Text;
        PreviewMeta.Text = L.T($"{Selected.DisplaySource} · 프로젝트 {Selected.DisplayProject} · {Selected.DisplayTime} · 입력 {Selected.InTok:N0}/출력 {Selected.OutTok:N0} tok · 턴 {Selected.SegTurns} · {Selected.EffectiveLabel}",
                               $"{Selected.DisplaySource} · project {Selected.DisplayProject} · {Selected.DisplayTime} · in {Selected.InTok:N0}/out {Selected.OutTok:N0} tok · {Selected.SegTurns} turns · {Selected.EffectiveLabel}");
        ScoreDetail.Text = Advisor.ScoreBreakdown(Selected, _medianTotal);
    }

    private void OnCopySelected(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { AddStatus(L.T("먼저 이력에서 행을 선택하세요", "Select a row first")); return; }
        Clipboard.SetText(Selected.Text);
        AddStatus(L.T($"복사 완료 ({Selected.Text.Length:N0}자)", $"Copied ({Selected.Text.Length:N0} chars)"));
        if (sender is Button b) Flash(b, L.T("복사됨", "Copied"));
    }

    private void OnSendSelectedToAnalyze(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { AddStatus(L.T("먼저 이력에서 행을 선택하세요", "Select a row first")); return; }
        InputBox.Text = Selected.Text;
        MainTabs.SelectedIndex = 0;
        InputBox.Focus();
        AddStatus(L.T("분석 탭으로 보냈습니다 — [분석]으로 다시 평가하세요", "Sent to Analyze — press [Analyze]"));
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e) => OnSendSelectedToAnalyze(sender, e);

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var tag = (FilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var projFilter = (ProjectFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var rows = _store.Query(tag, 1_000_000, SearchBox?.Text ?? "", projFilter);
        var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "prompt-advisor-history.csv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, Importers.ToCsv(rows), new System.Text.UTF8Encoding(true));
        SettingsStatus.Text = L.T($"CSV 내보내기: {rows.Count:N0}건", $"CSV export: {rows.Count:N0} rows");
        AddStatus(SettingsStatus.Text);
    }

    /// <summary>실제로 보낼 본문을 예측으로 등록해, 나중에 가져오기 때 실측과 매칭돼 보정 표본이 쌓이게 한다.</summary>
    private bool RegisterSentPrompt(string text)
    {
        SaveTemplateVersionIfEdited(text);
        if (!_sentPromptHashes.Add(Store.HashNorm(text))) return false;
        var task = _last?.TaskType ?? Advisor.Classify(text);
        _store.SavePrediction(text, task, Advisor.EstimateInputTokens(text), _last?.EstOutputTokens ?? Advisor.BaseOutputForTask(task), _last?.Models.FirstOrDefault()?.Model.Name ?? "");
        return true;
    }

    private void OnCopyPromptOnlyInput(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) { AddStatus(L.T("복사할 프롬프트가 입력창에 없습니다", "The input box is empty")); return; }
        Clipboard.SetText(text);
        bool sent = RegisterSentPrompt(text);
        AddStatus(L.T($"프롬프트만 복사 ({text.Length:N0}자) — 원하는 도구에 Ctrl+V", $"Prompt copied ({text.Length:N0} chars) — paste with Ctrl+V") + (sent ? L.T(" · 전송본으로 등록", " · registered as sent") : ""));
        if (sender is Button b) Flash(b, L.T("복사됨", "Copied"));
    }

    private void OnCopyAnalysis(object sender, RoutedEventArgs e)
    {
        if (_last == null) { AddStatus(L.T("먼저 프롬프트를 분석하세요", "Analyze a prompt first")); return; }
        var lines = new List<string>
        {
            L.T("[Prompt Advisor 분석] 작업 유형: ", "[Prompt Advisor] Task: ") + Advisor.TaskLabel(_last.TaskType),
            L.T($"예상 토큰: 입력 ~{_last.EstInputTokens:N0} / 출력 ~{_last.EstOutputTokens:N0}", $"Expected tokens: in ~{_last.EstInputTokens:N0} / out ~{_last.EstOutputTokens:N0}"),
        };
        if (_last.Context is { } c)
        {
            if (!string.IsNullOrWhiteSpace(c.ProjectName)) lines.Add(L.T("프로젝트: ", "Project: ") + c.ProjectName);
            if (!string.IsNullOrWhiteSpace(c.SessionGoal)) lines.Add(L.T("작업 목적: ", "Goal: ") + c.SessionGoal);
            if (c.SelectedFiles.Count > 0) lines.Add(L.T("대상 파일: ", "Files: ") + string.Join(", ", c.SelectedFiles));
        }
        if (_last.Lints.Count > 0) lines.Add(L.T("낭비 요소: ", "Waste: ") + string.Join("; ", _last.Lints.Select(l => $"{l.Rule} (~{l.WasteTokens:N0} tok)")));
        foreach (var r in _last.Models)
            lines.Add(L.T($"추천 {r.Rank}순위: ", $"#{r.Rank}: ") + $"{r.Model.Name} ({r.Model.Plan}) {r.Fit} ~{r.EstTotalTokens:N0} tok" + (r.Cost > 0 ? $" ~${r.Cost:F3}" : "") + $" — {r.Reason}");
        Clipboard.SetText(string.Join("\n", lines));
        AddStatus(L.T("분석 요약 복사 완료", "Analysis summary copied"));
    }

    private void OnThumbUp(object sender, RoutedEventArgs e) { if (Selected != null) { _store.SetLabel(Selected.Id, 1); RefreshHistory(); } }
    private void OnThumbDown(object sender, RoutedEventArgs e) { if (Selected != null) { _store.SetLabel(Selected.Id, -1); RefreshHistory(); } }
    private void OnThumbClear(object sender, RoutedEventArgs e) { if (Selected != null) { _store.SetLabel(Selected.Id, 0); RefreshHistory(); } }

    private void AddStatus(string t) => StatusText.Text = t;

    // ---------- 가져오기 ----------
    private async Task RunImport(Func<IProgress<string>, ImportReport> job, string name)
    {
        ImportStatus.Text = L.T($"{name} 가져오는 중…", $"Importing {name}…");
        var progress = new Progress<string>(s => ImportStatus.Text = s);
        try
        {
            var rep = await Task.Run(() => job(progress));
            int inserted = _store.InsertMany(rep.Rows);
            await Task.Run(() => Advisor.ComputeLabels(_store));
            int matched = _store.MatchPredictions();
            int rw = _store.MatchRewrites();
            RuleImpact.Invalidate();
            ImportStatus.Text = L.T($"{name}: 추출 {rep.Rows.Count:N0}건, 새로 저장 {inserted:N0}건 (중복 제외). 자동 라벨 재계산 완료.", $"{name}: {rep.Rows.Count:N0} extracted, {inserted:N0} new (duplicates skipped). Labels recomputed.")
                + (matched > 0 ? L.T($" 예측-실측 매칭 {matched}건.", $" {matched} predictions matched.") : "")
                + (rw > 0 ? L.T($" 보낸 개선본 {rw}건이 실제 기록과 이어졌습니다.", $" {rw} sent rewrites matched to real records.") : "")
                + (rep.Warnings.Count > 0 ? "\n" + string.Join("\n", rep.Warnings) : "");
            if (rep.FormatChanged) _store.LogError(L.T($"가져오기 형식 변경: {name}", $"Import format changed: {name}"), rep.Warnings.FirstOrDefault() ?? "");
            BuildProjectCombo(App.Settings.ActiveProjectDir, App.Settings.ActiveProjectName);
            BuildProjectFilterCombo();
            RefreshHistory();
            RefreshOnboarding();
            RefreshVerifyTab();
            RefreshErrors();
            // 새로 들어온 것은 Ollama 가 켜져 있을 때만 최대 100건 자동 임베딩
            if (inserted > 0 && await _ollama.IsAvailable())
            {
                var (emb, _) = await EmbedMissingAsync(100);
                if (emb > 0) ImportStatus.Text += L.T($" 임베딩 {emb:N0}건 완료.", $" {emb:N0} embedded.");
            }
        }
        catch (Exception ex) { _store.LogError(L.T($"가져오기: {name}", $"Import: {name}"), ex.Message); RefreshErrors(); ImportStatus.Text = L.T($"{name} 실패: ", $"{name} failed: ") + ex.Message; }
    }

    private async void OnImportClaude(object sender, RoutedEventArgs e) => await RunImport(p => Importers.ClaudeCode(p), "Claude Code");
    private async void OnImportCursor(object sender, RoutedEventArgs e) => await RunImport(p => Importers.Cursor(p), "Cursor");
    private async void OnImportAntigravity(object sender, RoutedEventArgs e) => await RunImport(_ => Importers.Antigravity(), "Antigravity");

    private async void OnImportZip(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Zip|*.zip" };
        if (dlg.ShowDialog() != true) return;
        await RunImport(_ => Importers.ClaudeExportZip(dlg.FileName), "claude.ai");
    }

    private async void OnImportCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        await RunImport(_ => Importers.Csv(dlg.FileName), "CSV");
    }

    /// <summary>예전 개인판 DB 의 프롬프트를 합친다 (버튼을 누를 때만 읽음).</summary>
    private async void OnImportLegacy(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Store.LegacyDbPath)) { ImportStatus.Text = L.T("이전 버전 데이터가 없습니다: ", "No previous-version data at: ") + Store.DisplayPath(Store.LegacyDbPath); return; }
        await RunImport(_ =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"pa-legacy-{Environment.ProcessId}.db");
            File.Copy(Store.LegacyDbPath, tmp, overwrite: true);
            try
            {
                using var old = new Store(tmp);
                return new ImportReport { Name = L.T("이전 버전", "previous version"), Rows = old.All() };
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(tmp); } catch { } }
        }, L.T("이전 버전", "Previous version"));
    }

    private void OnPasteSave(object sender, RoutedEventArgs e)
    {
        if (PasteBox.Text.Trim().Length == 0) return;
        var source = (PasteSource.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "paste";
        _store.InsertMany(new[] { Importers.Paste(source, PasteBox.Text) });
        PasteBox.Clear();
        ImportStatus.Text = L.T("붙여넣기 저장 완료.", "Saved.");
        RefreshHistory();
    }

    // ---------- 오류 · 설정 · 모델 지표 ----------
    private void RefreshErrors()
    {
        ErrorList.ItemsSource = _store.RecentErrors();
        ErrorCountText.Text = L.T($"누적 오류 {_store.ErrorCount()}건 (가져오기·분석·생성·임베딩 단계별) · 처리되지 않은 예외는 crash.log", $"{_store.ErrorCount()} errors (import/analyze/rewrite/embed) · unhandled exceptions go to crash.log");
    }

    private void OnRefreshErrors(object sender, RoutedEventArgs e) => RefreshErrors();

    private void OnClearErrors(object sender, RoutedEventArgs e)
    {
        _store.ClearErrors();
        RefreshErrors();
        AddStatus(L.T("오류 기록을 지웠습니다", "Error log cleared"));
    }

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        App.Settings.Save();
        var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "prompt-advisor-settings.json" };
        if (dlg.ShowDialog() != true) return;
        File.Copy(Path.Combine(Store.DataDir, "settings.json"), dlg.FileName, overwrite: true);
        SettingsStatus.Text = L.T("설정 내보냄: ", "Settings exported: ") + Path.GetFileName(dlg.FileName);
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _ = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(dlg.FileName)) ?? throw new InvalidOperationException(L.T("설정 형식이 아닙니다", "Not a settings file"));
            File.Copy(dlg.FileName, Path.Combine(Store.DataDir, "settings.json"), overwrite: true);
            SettingsStatus.Text = L.T("설정 가져오기 완료 — 앱을 다시 열면 적용됩니다", "Settings imported — restart the app to apply");
        }
        catch (Exception ex)
        {
            _store.LogError(L.T("설정 가져오기", "Import settings"), ex.Message);
            SettingsStatus.Text = L.T("설정 가져오기 실패: ", "Import failed: ") + ex.Message;
            RefreshErrors();
        }
    }

    private void RefreshModelEval()
    {
        var byModel = _store.CalibrationRowsByModel();
        ModelEvalList.ItemsSource = Advisor.LoadModels().Select(m =>
        {
            var mine = byModel.Where(r => r.model == m.Name).ToList();
            return mine.Count > 0
                ? L.T($"{m.Name} · 표본 {mine.Count}건 · 예측 오차 ±{Advisor.PredictionErrorPct(mine.Select(r => (task: "", r.estOut, r.actOut))):F0}% · outputFactor {m.OutputFactor:F2}",
                      $"{m.Name} · n={mine.Count} · error ±{Advisor.PredictionErrorPct(mine.Select(r => (task: "", r.estOut, r.actOut))):F0}% · outputFactor {m.OutputFactor:F2}")
                : L.T($"{m.Name} · 표본 없음 · outputFactor {m.OutputFactor:F2}", $"{m.Name} · no samples · outputFactor {m.OutputFactor:F2}");
        }).ToList();
        ModelMetricStatus.Text = App.Settings.LastModelMetricCheck.Length > 0
            ? L.T($"마지막 자동 점검: {App.Settings.LastModelMetricCheck} (7일마다, 표본 3건 이상인 모델만 보정)", $"Last check: {App.Settings.LastModelMetricCheck} (every 7 days, models with 3+ samples)")
            : L.T("자동 점검 전 (7일 간격)", "Not checked yet (every 7 days)");
    }

    /// <summary>7일마다 실측 예측 오차로 모델 outputFactor 를 자동 보정 (표본 3건 미만이면 그대로).</summary>
    private void AutoModelMetricCheck()
    {
        try
        {
            bool due = App.Settings.LastModelMetricCheck.Length == 0
                || (DateTime.TryParse(App.Settings.LastModelMetricCheck, out var d) && (DateTime.Now - d).TotalDays >= 7);
            if (!due) { RefreshModelEval(); return; }
            var (updated, changes) = Advisor.AutoAdjustFactors(Advisor.LoadModels(), _store.CalibrationRowsByModel());
            if (changes.Count > 0)
            {
                Advisor.SaveModels(updated);
                _store.LogError(L.T("모델 지표 자동 보정", "Model factor auto-adjust"), string.Join(" / ", changes));
                BuildModelCombo(App.Settings.SelectedModel);
            }
            App.Settings.LastModelMetricCheck = DateTime.Now.ToString("yyyy-MM-dd");
            App.Settings.Save();
            RefreshModelEval();
            RefreshErrors();
        }
        catch (Exception ex) { _store.LogError(L.T("모델 지표 점검", "Model factor check"), ex.Message); }
    }

    private void OnOpenLeaderboard(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://artificialanalysis.ai/leaderboards/models") { UseShellExecute = true });

    private void OnResetModels(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(L.T("models.json 을 앱 기본값으로 되돌립니다. 직접 편집한 내용과 자동 보정된 outputFactor 는 사라집니다. 계속할까요?", "Reset models.json to the bundled defaults? Your edits and auto-adjusted factors will be lost."),
                L.T("모델표 초기화", "Reset model table"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "models.json"), Path.Combine(Store.DataDir, "models.json"), overwrite: true);
        BuildModelCombo("");
        App.Settings.SelectedModel = "";
        RefreshModelEval();
        AddStatus(L.T("모델표를 기본값으로 초기화했습니다", "Model table reset"));
    }

    // ---------- 지식 내보내기 ----------
    private void OnPickKnowledgeDir(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("지식을 내보낼 저장소 폴더 (AGENTS.md 가 있는 곳)", "Repository folder to export to (where AGENTS.md lives)") };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        App.Settings.KnowledgeDir = dlg.SelectedPath;
        App.Settings.Save();
        KnowledgeDirText.Text = L.T("대상: ", "Target: ") + Store.DisplayPath(dlg.SelectedPath);
    }

    private void OnExportKnowledge(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Advisor.FindRepoDir(App.Settings.KnowledgeDir);
            if (dir == null) { AddStatus(L.T("대상 폴더가 없습니다. [대상 폴더 선택]으로 먼저 지정하세요", "No target folder. Use [Choose folder] first")); return; }
            var (count, cards) = Advisor.WriteKnowledgeExport(dir, _store, App.Settings.FavoriteModels, App.Settings.SelectedModel, DateTime.Now);
            KnowledgeDirText.Text = L.T("대상: ", "Target: ") + Store.DisplayPath(dir);
            AddChat(L.T("지식 내보내기", "Knowledge export"), L.T($"현황과 지식 카드를 갱신했습니다.\n- docs/STATUS.md (프롬프트 {count:N0}건 기준)\n", $"Updated status and knowledge cards.\n- docs/STATUS.md ({count:N0} prompts)\n")
                + string.Join("\n", cards.Select(c => $"- docs/knowledge/{c.File}"))
                + L.T("\n\n원문 · 프로젝트 이름은 넣지 않았습니다. 내용이 맞으면 git 에 커밋하세요.", "\n\nNo raw prompts or project names are included. Commit them if they look right."));
        }
        catch (Exception ex) { _store.LogError(L.T("지식 내보내기", "Knowledge export"), ex.Message); RefreshErrors(); AddStatus(L.T("지식 내보내기 실패: ", "Export failed: ") + ex.Message); }
    }

    private async void OnSystemCheck(object sender, RoutedEventArgs e)
    {
        long dbBytes = File.Exists(Store.DbPath) ? new FileInfo(Store.DbPath).Length : 0;
        int attCount = 0; long attBytes = 0;
        if (Directory.Exists(AttachDir)) { var files = Directory.GetFiles(AttachDir); attCount = files.Length; attBytes = files.Sum(f => new FileInfo(f).Length); }
        var (total, sessions, _, _, _) = _store.Stats();
        string ollamaModels = L.T("미연결", "not connected");
        try
        {
            if (await _ollama.IsAvailable())
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"{_ollama.BaseUrl}/api/tags"));
                ollamaModels = string.Join(", ", doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => $"{m.GetProperty("name").GetString()} ({m.GetProperty("size").GetInt64() / 1_000_000_000.0:F1}GB)"));
            }
        }
        catch { }
        SystemCheckText.Text = L.T($"DB {dbBytes / 1_000_000.0:F1}MB (스키마 v{Store.SchemaVersion}) · 프롬프트 {total:N0}건/세션 {sessions:N0} · 첨부 {attCount}장 {attBytes / 1_000_000.0:F1}MB · 오류 {_store.ErrorCount()}건 · 예측 실측 {_store.CalibrationRows().Count}건\n로컬 모델: {ollamaModels}",
                                   $"DB {dbBytes / 1_000_000.0:F1} MB (schema v{Store.SchemaVersion}) · {total:N0} prompts/{sessions:N0} sessions · {attCount} attachments {attBytes / 1_000_000.0:F1} MB · {_store.ErrorCount()} errors · {_store.CalibrationRows().Count} measured predictions\nLocal models: {ollamaModels}");
    }

    // ---------- Ollama / 설정 ----------
    private async Task RefreshOllamaStatus()
    {
        bool ok = await _ollama.IsAvailable();
        var installed = ok ? await _ollama.InstalledModels() : new List<string>();
        var missing = new[] { _ollama.GenModel, _ollama.EmbedModel }.Where(m => !Ollama.Has(installed, m)).ToList();
        OllamaStatus.Text = ok && missing.Count > 0
            ? L.T($"연결됨 · 모델 없음: {string.Join(", ", missing)} — 받기: {string.Join(" · ", missing.Select(Ollama.InstallHint))}", $"Connected · missing models: {string.Join(", ", missing)} — get them: {string.Join(" · ", missing.Select(Ollama.InstallHint))}")
            : ok
            ? L.T($"연결됨 (생성 {_ollama.GenModel}, 임베딩 {_ollama.EmbedModel}) · 미임베딩 {_store.CountUnembedded():N0}건", $"Connected (generate {_ollama.GenModel}, embed {_ollama.EmbedModel}) · {_store.CountUnembedded():N0} not embedded")
            : L.T($"Ollama 미연결 — 린트·통계·규칙 개선안은 그대로 동작합니다. 설치: winget install Ollama.Ollama 후 {Ollama.InstallHint(_ollama.GenModel)} · ollama pull bge-m3",
                  $"Ollama not connected — lint, stats and rule-based rewrites still work. Install: winget install Ollama.Ollama, then {Ollama.InstallHint(_ollama.GenModel)} · ollama pull bge-m3");
    }

    private async void OnCheckOllama(object sender, RoutedEventArgs e)
    {
        ApplyOllamaSettings();
        OllamaStatus.Text = L.T("연결 확인 중… (꺼져 있으면 자동으로 띄웁니다)", "Checking… (starts Ollama if installed)");
        await _ollama.EnsureAvailable();
        await RefreshOllamaStatus();
    }

    /// <summary>미임베딩 프롬프트를 최대 max건까지 임베딩. 실패 시 중단하고 (완료건수, 실패여부).</summary>
    private async Task<(int done, bool failed)> EmbedMissingAsync(int max)
    {
        int done = 0;
        while (done < max && _store.Unembedded(Math.Min(50, max - done)) is { Count: > 0 } batch)
        {
            foreach (var row in batch)
            {
                var vec = await _ollama.Embed(Advisor.MaskSensitive(row.Text));
                if (vec != null) { _store.SetEmbedding(row.Id, vec); done++; }
                else { _store.LogError(L.T("임베딩", "Embedding"), L.T("벡터 생성 실패 — 모델이 있는지 확인", "No vector — check that the model is pulled")); RefreshErrors(); return (done, true); }
            }
            OllamaStatus.Text = L.T($"임베딩 중… {done:N0}건", $"Embedding… {done:N0}");
        }
        return (done, false);
    }

    private async void OnEmbedAll(object sender, RoutedEventArgs e)
    {
        ApplyOllamaSettings();
        if (!await _ollama.IsAvailable()) { await RefreshOllamaStatus(); return; }
        EmbedBtn.IsEnabled = false;
        try
        {
            var (done, failed) = await EmbedMissingAsync(int.MaxValue);
            OllamaStatus.Text = failed ? L.T("임베딩 실패 — ollama pull bge-m3 로 모델을 받았는지 확인하세요", "Embedding failed — run ollama pull bge-m3") : L.T($"임베딩 완료 ({done:N0}건)", $"Embedded {done:N0}");
            RefreshOnboarding();
        }
        finally { EmbedBtn.IsEnabled = true; }
    }

    private void ApplyOllamaSettings()
    {
        _ollama.BaseUrl = OllamaUrlBox.Text.Trim();
        _ollama.GenModel = GenModelBox.Text.Trim();
        _ollama.EmbedModel = EmbedModelBox.Text.Trim();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if ((ThemeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag) { App.Settings.Theme = tag; App.ApplyTheme(); }
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        ApplyOllamaSettings();
        App.Settings.OllamaUrl = _ollama.BaseUrl;
        App.Settings.GenModel = _ollama.GenModel;
        App.Settings.EmbedModel = _ollama.EmbedModel;
        App.Settings.AutoImport = AutoImportBox.IsChecked == true;
        if ((ThemeCombo.SelectedItem as ComboBoxItem)?.Tag is string tag) App.Settings.Theme = tag;
        string oldLang = App.Settings.Language;
        if ((LangCombo.SelectedItem as ComboBoxItem)?.Tag is string lang) App.Settings.Language = lang;
        if ((RecommendCountCombo.SelectedItem as ComboBoxItem)?.Tag is string rc && int.TryParse(rc, out var rci)) App.Settings.RecommendCount = Math.Clamp(rci, 1, 5);
        App.Settings.Save();
        SettingsStatus.Text = L.T("설정 저장 완료", "Settings saved") + (oldLang != App.Settings.Language ? L.T(" — 언어는 앱을 다시 열면 바뀝니다", " — restart to change the language") : "");
        AddStatus(SettingsStatus.Text);
    }

    private void OnOpenModels(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Store.DataDir, "models.json");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
