using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PromptAdvisor;

/// <summary>
/// `PromptAdvisor.exe --shots [폴더] [--lang en]` — 합성 예시 DB 로 화면을 순서대로 띄우고 PNG 로 저장한다 (매뉴얼 · 보고서용).
/// 예시 모드에서만 돈다 (Program 이 --shots 면 예시 모드를 강제). 이 PC 의 실제 기록 · 폴더 · 스킬은 읽지 않는다.
/// </summary>
public partial class MainWindow
{
    private const double ShotScale = 1.5;

    private async Task Settle(int ms = 500)
    {
        await Task.Delay(ms);
        await Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SaveShot(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        int w = (int)Math.Ceiling(el.ActualWidth * ShotScale), h = (int)Math.Ceiling(el.ActualHeight * ShotScale);
        var bg = TryFindResource("ApplicationBackgroundBrush") as Brush
                 ?? (App.Current.ThemeMode == ThemeMode.Dark ? new SolidColorBrush(Color.FromRgb(32, 32, 32)) : new SolidColorBrush(Color.FromRgb(243, 243, 243)));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
            dc.DrawRectangle(bg, null, rect);
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
        }
        var rtb = new RenderTargetBitmap(w, h, 96 * ShotScale, 96 * ShotScale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public async Task CaptureShotsAsync(string dir)
    {
        if (!App.DemoMode) return;
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, (L.En ? "en_" : "") + name + ".png");
        var root = (FrameworkElement)Content;
        WindowState = WindowState.Normal;
        Width = 1380; Height = 880;
        Left = 0; Top = 0;
        await Settle(1200);

        // 1) 분석: 짧은 장비 요청 → 린트(장비 5요소 + 내 기록 수치) · 예측 · 모델
        MainTabs.SelectedIndex = 0;
        InputBox.Text = "서보 알람 자꾸 떠요 고쳐줘";
        if (L.En) InputBox.Text = "servo alarm keeps coming back, please fix";
        await AnalyzeCurrentAsync();
        await Settle();
        SaveShot(root, P("01_analyze"));

        // 2) 개선안: 규칙안(채택) + LLM 안(Shadow) 나란히, 개선 전/후 카드
        await RewriteCurrentAsync();
        await Settle(800);
        if (ChatPanel.Children.Count > 0 && ChatPanel.Children[^1] is FrameworkElement lastBubble)
            ChatScroll.ScrollToVerticalOffset(lastBubble.TransformToAncestor(ChatPanel).Transform(new Point(0, 0)).Y);
        BeforeAfterPanel.BringIntoView();
        await Settle();
        SaveShot(root, P("02_rewrite"));

        // 3) 이력
        MainTabs.SelectedIndex = 1;
        await Settle();
        if (HistoryGrid.Items.Count > 3) HistoryGrid.SelectedIndex = 3;
        await Settle();
        SaveShot(root, P("03_history"));

        // 4) 검증: 재현 → 비교 → Shadow → 전환 · 롤백 → 개선 추적
        MainTabs.SelectedIndex = 2;
        VerifyTabs.SelectedIndex = 0;
        await Settle();
        OnRunRepro(this, new RoutedEventArgs());
        await Settle();
        SaveShot(root, P("04_verify_repro"));

        VerifyTabs.SelectedIndex = 1;
        CandEquipment.IsChecked = false;
        await Settle();
        OnCompare(this, new RoutedEventArgs());
        await Settle();
        SaveShot(root, P("05_verify_compare"));

        VerifyTabs.SelectedIndex = 2;
        await Settle();
        SaveShot(root, P("06_verify_shadow"));

        VerifyTabs.SelectedIndex = 3;
        OnApplyCandidate(this, new RoutedEventArgs()); // 장비 린트 끔으로 전환 → 이력에 남음
        await Settle();
        OnRollback(this, new RoutedEventArgs());      // 바로 되돌림
        await Settle();
        SaveShot(root, P("07_verify_switch"));

        VerifyTabs.SelectedIndex = 4;
        await Settle();
        SaveShot(root, P("08_verify_tracking"));

        // 5) 가져오기 · 설정: 잘못된 CSV 를 가져와 줄 번호 · 이유 표시
        MainTabs.SelectedIndex = 3;
        var bad = Path.Combine(Store.DataDir, "sample-bad.csv");
        File.WriteAllText(bad, L.T("text,project,in_tok,turns,created_at\n로더 Z축 원점 복귀 후 상태 표시 오류,wafer-handler-ui,12000,3,2026-11-02\n,wafer-handler-ui,5000,2,2026-11-02\n알람 이력 조회 느림,wafer-handler-ui,abc,2,2026-11-03\n칸이 모자란 줄\n",
                                   "text,project,in_tok,turns,created_at\nLoader Z-axis status wrong after homing,wafer-handler-ui,12000,3,2026-11-02\n,wafer-handler-ui,5000,2,2026-11-02\nAlarm history query is slow,wafer-handler-ui,abc,2,2026-11-03\nrow with too few cells\n"),
            new System.Text.UTF8Encoding(true));
        await RunImport(_ => Importers.Csv(bad), "CSV");
        await Settle();
        SaveShot(root, P("09_import_csv_errors"));

        // 6) 도움말
        MainTabs.SelectedIndex = 4;
        await Settle();
        SaveShot(root, P("10_help"));

        // 7) 다크 테마 (분석 탭)
        App.Settings.Theme = "Dark";
        App.ApplyTheme();
        MainTabs.SelectedIndex = 0;
        await Settle(1000);
        SaveShot(root, P("11_dark"));
        App.Settings.Theme = "System";
        App.ApplyTheme();
        await Settle();

        File.WriteAllText(Path.Combine(dir, (L.En ? "en_" : "") + "shots-done.txt"), DateTime.Now.ToString("s"));
    }

    /// <summary>`--shadow-eval`: 예시 10건을 화면과 같은 경로(RewriteCurrentAsync)로 돌려 실제 LLM Shadow 기록만 모아 요약한다.</summary>
    public async Task RunShadowEvalAsync(string path)
    {
        if (!App.DemoMode) return;
        var lines = new List<string> { $"model={_ollama.GenModel} at={DateTime.Now:s}", "i\ton_topic\tjudge\trule_lints\tllm_lints\tsec\tprompt" };
        var added = new List<Store.ShadowRow>();
        int i = 0;
        foreach (var p in SampleData.ExampleSet)
        {
            int before = _store.ShadowRows().Count;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            InputBox.Text = p;
            await RewriteCurrentAsync();
            var rows = _store.ShadowRows();
            string first = p.Split('\n')[0];
            if (rows.Count > before)
            {
                var r = rows[0];
                added.Add(r);
                lines.Add($"{++i}\t{r.OnTopic}\t{r.Judge}\t{r.RuleLints}\t{r.LlmLints}\t{sw.Elapsed.TotalSeconds:F0}\t{(first.Length > 40 ? first[..40] : first)}");
            }
            else lines.Add($"{++i}\t-\t-\t-\t-\t{sw.Elapsed.TotalSeconds:F0}\t(no shadow) {first}");
        }
        var s = Verification.Shadow(added);
        lines.Add($"n={s.N} on_topic={s.OnTopicRate:F2} avg_judge={s.AvgJudge:F1} llm_not_worse={s.LlmNotWorse} can_switch={s.CanSwitch} reason={s.Reason}");
        File.WriteAllText(path, string.Join("\n", lines));
    }
}
