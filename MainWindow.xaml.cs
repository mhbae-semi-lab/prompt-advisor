using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PromptAdvisor;

/// <summary>
/// 메인 창. 이 파일은 시작 · 트레이 · 첨부 · 채팅 · 맥락 · 분석 · 개선안. 이력 · 가져오기 · 설정은 MainWindow.History.cs,
/// 검증 탭 · 도움말 · 예시 데이터 · 업데이트는 MainWindow.Verify.cs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Store _store = App.Store;
    private readonly Ollama _ollama = App.Ollama;
    private Analysis? _last;
    private string _lastPrompt = "";
    private List<SkillInfo>? _skills;
    private readonly Dictionary<string, float[]> _skillVecs = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exitRequested;
    private readonly List<string> _attachments = new(); // 첨부 이미지 파일 경로 (DataDir/attachments에 복사됨)
    private readonly List<string> _sessionPrompts = new(); // 현재 앱 세션 내 연속 입력한 프롬프트 이력
    private bool _suppressContextEvents;
    private double _medianTotal;
    private readonly HashSet<string> _sentPromptHashes = new(); // 실제 전송 등록한 본문 해시 (중복 등록 방지)
    private long _loadedTemplateId;      // 입력창에 불러온 템플릿의 프롬프트 id (0=없음)
    private string _loadedTemplateText = "";

    private static readonly Color Accent = Color.FromRgb(0, 120, 212), Green = Color.FromRgb(16, 124, 16),
        Orange = Color.FromRgb(202, 80, 16), Red = Color.FromRgb(196, 43, 28), Purple = Color.FromRgb(135, 100, 184), Amber = Color.FromRgb(175, 110, 0);

    // 전역 단축키 Ctrl+Shift+P
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int HotkeyId = 9000, WmHotkey = 0x0312;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);
        if (App.DemoMode) return; // 예시 실행은 전역 단축키를 잡지 않는다 (본 앱과 겹치지 않게)
        if (!RegisterHotKey(hwnd, HotkeyId, 0x0002 | 0x0004, 0x50)) // Ctrl+Shift+P
            AddStatus(L.T("전역 단축키 Ctrl+Shift+P 등록 실패 (다른 앱이 사용 중) — 앱 안 단축키만 동작합니다",
                          "Could not register Ctrl+Shift+P (used by another app) — in-app shortcuts still work"));
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId) { ShowFromTray(); handled = true; }
        return IntPtr.Zero;
    }

    /// <summary>어느 앱에서든 Ctrl+Shift+P → 창을 앞으로 + 입력창 포커스.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        MainTabs.SelectedIndex = 0;
        InputBox.Focus();
    }

    private void InitTray()
    {
        if (App.DemoMode) return; // 예시 실행은 닫으면 그냥 끝난다
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PromptAdvisor.exe");
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe),
            Text = L.T("Prompt Advisor — Ctrl+Shift+P 로 호출", "Prompt Advisor — Ctrl+Shift+P to open"),
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(L.T("열기 (Ctrl+Shift+P)", "Open (Ctrl+Shift+P)"), null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(L.T("주간 리포트", "Weekly report"), null, (_, _) => Dispatcher.Invoke(() => { ShowFromTray(); OnWeeklyReport(this, new RoutedEventArgs()); }));
        menu.Items.Add("-");
        menu.Items.Add(L.T("종료", "Exit"), null, (_, _) => { _exitRequested = true; Dispatcher.Invoke(Close); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        Closing += (_, e) =>
        {
            if (_exitRequested) { _tray!.Visible = false; _tray.Dispose(); return; }
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "Prompt Advisor", L.T("트레이에서 계속 실행 중입니다. Ctrl+Shift+P 로 언제든 부르세요.", "Still running in the tray. Press Ctrl+Shift+P any time."), System.Windows.Forms.ToolTipIcon.Info);
        };
    }

    // ---------- 온보딩 체크리스트 ----------
    private void RefreshOnboarding()
    {
        bool hasData = _store.Stats().total > 0;
        bool hasEmb = _store.CountEmbedded() > 0;
        bool hasAnalyzed = App.Settings.HasAnalyzed;
        if (App.DemoMode || (hasData && hasAnalyzed)) { OnboardingCard.Visibility = Visibility.Collapsed; return; }
        OnboardingCard.Visibility = Visibility.Visible;
        string Mark(bool b) => b ? "[v]" : "[ ]";
        int done = (hasData ? 1 : 0) + (hasEmb ? 1 : 0) + (hasAnalyzed ? 1 : 0);
        OnboardTitle.Text = L.T($"시작 체크리스트 ({done}/3)", $"Getting started ({done}/3)");
        OnboardSteps.Text = L.T(
            $"{Mark(hasData)} 1. 데이터 가져오기 — [가져오기·설정] 탭에서 Claude Code · Cursor 버튼 (또는 [예시 데이터로 둘러보기])\n" +
            $"{Mark(hasEmb)} 2. (선택) 임베딩 — Ollama 연결 후 [미임베딩 프롬프트 임베딩]. 없으면 단어 유사도로 비슷한 사례를 찾습니다\n" +
            $"{Mark(hasAnalyzed)} 3. 첫 분석 — 프롬프트를 붙여넣고 Ctrl+Enter",
            $"{Mark(hasData)} 1. Import data — Claude Code / Cursor buttons on the Import & Settings tab (or [Try sample data])\n" +
            $"{Mark(hasEmb)} 2. (optional) Embeddings — connect Ollama, then [Embed prompts]. Without it, similar cases are found by word overlap\n" +
            $"{Mark(hasAnalyzed)} 3. First analysis — paste a prompt and press Ctrl+Enter");
    }

    // ---------- 주간 리포트 스케줄 (월요일 첫 실행 시 + 1시간마다 재확인) ----------
    private void CheckWeeklyReport()
    {
        if (App.DemoMode) return;
        var now = DateTime.Now;
        if (now.DayOfWeek != DayOfWeek.Monday) return;
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        if (DateTime.TryParse(App.Settings.LastWeeklyReport, out var last) && last.Date >= monday) return;
        var report = Advisor.WeeklyReport(_store.All(), now);
        AddChat(L.T("주간 리포트 (자동)", "Weekly report (auto)"), report);
        _tray?.ShowBalloonTip(6000, L.T("Prompt Advisor 주간 리포트", "Prompt Advisor weekly report"),
            L.T("최근 7일 낭비 TOP 5가 준비됐습니다.", "Your top 5 wasteful prompts of the last 7 days are ready."), System.Windows.Forms.ToolTipIcon.Info);
        App.Settings.LastWeeklyReport = now.ToString("yyyy-MM-dd");
        App.Settings.Save();
    }

    public MainWindow()
    {
        InitializeComponent();
        L.Apply(this);
        if (App.DemoMode)
        {
            DemoBanner.Visibility = Visibility.Visible;
            Title += L.T(" — 예시 데이터", " — sample data");
        }
        InputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OnAnalyze(this, e); e.Handled = true; }
            else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    var path = SaveClipboardImage(img);
                    if (path != null) { AddAttachmentFile(path, fromClipboard: true); e.Handled = true; }
                }
            }
        };
        OllamaUrlBox.Text = App.Settings.OllamaUrl;
        GenModelBox.Text = App.Settings.GenModel;
        EmbedModelBox.Text = App.Settings.EmbedModel;
        foreach (ComboBoxItem item in ThemeCombo.Items)
            if ((string)item.Tag == App.Settings.Theme) ThemeCombo.SelectedItem = item;
        foreach (ComboBoxItem item in LangCombo.Items)
            if ((string)item.Tag == App.Settings.Language) LangCombo.SelectedItem = item;
        if (LangCombo.SelectedItem == null) LangCombo.SelectedIndex = 0;
        AutoImportBox.IsChecked = App.Settings.AutoImport;
        UpdateOnStartBox.IsChecked = App.Settings.UpdateCheckOnStart;
        BuildProjectCombo(App.Settings.ActiveProjectDir, App.Settings.ActiveProjectName);
        BuildModelCombo(App.Settings.SelectedModel);
        foreach (ComboBoxItem item in RecommendCountCombo.Items)
            if ((string)item.Tag == App.Settings.RecommendCount.ToString()) RecommendCountCombo.SelectedItem = item;
        if (RecommendCountCombo.SelectedItem == null) RecommendCountCombo.SelectedIndex = 2;
        VersionText.Text = $"Prompt Advisor v{App.Version}";
        var kd = App.DemoMode ? null : Advisor.FindRepoDir(App.Settings.KnowledgeDir);
        KnowledgeDirText.Text = L.T("대상: ", "Target: ") + (kd != null ? Store.DisplayPath(kd) : L.T("미지정 — [대상 폴더 선택]", "not set — [Choose folder]"));
        PreviewKeyDown += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            switch (e.Key)
            {
                case Key.D1: MainTabs.SelectedIndex = 0; e.Handled = true; break;
                case Key.D2: MainTabs.SelectedIndex = 1; e.Handled = true; break;
                case Key.D3: MainTabs.SelectedIndex = 2; e.Handled = true; break;
                case Key.D4: MainTabs.SelectedIndex = 3; e.Handled = true; break;
                case Key.D5: MainTabs.SelectedIndex = 4; e.Handled = true; break;
                case Key.F: MainTabs.SelectedIndex = 1; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; break;
                case Key.K: MainTabs.SelectedIndex = 0; InputBox.Focus(); e.Handled = true; break;
            }
        };
        AddChat(L.T("시스템", "System"), L.T(
            "프롬프트를 붙여넣고 [분석]을 누르세요. 내 과거 기록(토큰 · 턴 · 정정)으로 낭비를 미리 경고하고, 장비 SW 요청이면 대상 장비 · 버전 · 로그 · 재현 조건 · 기대 결과가 빠졌는지 봅니다.",
            "Paste a prompt and press [Analyze]. Warnings are learned from your own history (tokens, turns, corrections); for equipment software requests it checks target equipment, version, log, repro conditions and expected result."));
        RefreshStats();
        BuildProjectFilterCombo();
        RefreshHistory();
        RefreshErrors();
        AutoModelMetricCheck();
        InitTray();
        RefreshOnboarding();
        InitVerifyTab();
        HelpText.Text = HelpContent();
        CheckWeeklyReport();
        var weeklyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        weeklyTimer.Tick += (_, _) => CheckWeeklyReport();
        weeklyTimer.Start();
        Loaded += async (_, _) =>
        {
            await RefreshOllamaStatus();
            if (!App.Settings.FirstRunDone && !App.DemoMode) ShowWelcome();
            if (App.Settings.UpdateCheckOnStart && !App.DemoMode) UpdateStatus.Text = await Updater.CheckAsync(apply: false, null);
        };
        if (!App.DemoMode && (App.Settings.AutoImport || (_store.Stats().total > 0 && _store.DistinctProjects().Count == 0)))
            _ = AutoImportAsync();
    }

    /// <summary>시작 시 증분 가져오기 및 기존 행 프로젝트명 역추적 채움.</summary>
    private async Task AutoImportAsync()
    {
        try
        {
            AddStatus(L.T("자동 가져오기 중…", "Importing new sessions…"));
            var claude = await Task.Run(() => Importers.ClaudeCode());
            var cursor = await Task.Run(() => Importers.Cursor());
            int n = _store.InsertMany(claude.Rows) + _store.InsertMany(cursor.Rows);
            if (n > 0) await Task.Run(() => Advisor.ComputeLabels(_store));
            BuildProjectCombo(App.Settings.ActiveProjectDir, App.Settings.ActiveProjectName);
            BuildProjectFilterCombo();
            RefreshHistory();
            _store.MatchPredictions();
            _store.MatchRewrites();
            var warn = claude.Warnings.Concat(cursor.Warnings).FirstOrDefault(w => claude.FormatChanged || cursor.FormatChanged);
            AddStatus(warn ?? (n > 0 ? L.T($"자동 가져오기: 새 프롬프트 {n:N0}건 추가", $"Auto import: {n:N0} new prompts")
                                     : L.T($"새 기록 없음 (프로젝트 {_store.DistinctProjects().Count}개)", $"No new records ({_store.DistinctProjects().Count} projects)")));
        }
        catch (Exception ex)
        {
            _store.LogError(L.T("자동 가져오기", "Auto import"), ex.Message);
            AddStatus(L.T("자동 가져오기 실패 — 설정 탭에서 수동으로 다시 시도하세요", "Auto import failed — try again from the Settings tab"));
        }
    }

    // ---------- 이미지 첨부 ----------
    private static string AttachDir => Path.Combine(Store.DataDir, "attachments");
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private void OnAttachImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("이미지", "Images") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) AddAttachmentFile(f);
    }

    private void OnInputDragEnter(object sender, DragEventArgs e)
        => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>폴더 안 모든 이미지를 일괄 첨부하고 단계별 처리 리포트를 채팅에 남긴다.</summary>
    private async void OnAttachFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("이미지가 있는 폴더 (하위 폴더 포함, 최대 100장)", "Folder with images (subfolders included, up to 100)") };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string folder = dlg.SelectedPath;
        AddStatus(L.T("폴더 스캔 중…", "Scanning folder…"));
        try
        {
            var found = await Task.Run(() =>
                Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f).Take(101).ToList());
            if (found.Count == 0) { AddChat(L.T("시스템", "System"), L.T("이미지가 없습니다: ", "No images in: ") + Path.GetFileName(folder)); return; }

            bool truncated = found.Count > 100;
            var targets = found.Take(100).ToList();
            int before = _attachments.Count;
            foreach (var f in targets) AddAttachmentFile(f);
            int copied = _attachments.Count - before;

            var infos = targets.Select(f => { var fi = new FileInfo(f); return (Name: fi.Name, Ext: fi.Extension.ToLowerInvariant(), Bytes: fi.Length); }).ToList();
            var calib = _store.CalibrationRows();
            var report = Advisor.ImageBatchReport(Path.GetFileName(folder), infos, copied, calib.Count, calib.Count > 0 ? Advisor.PredictionErrorPct(calib) : 0);
            if (truncated) report += L.T("\n(100장 초과분은 제외했습니다 — 폴더를 나눠 처리하세요)", "\n(Images beyond 100 were skipped — split the folder)");
            AddChat(L.T("이미지 일괄 처리", "Image batch"), report);
            AddStatus(L.T($"{copied}장 첨부 — 예상 ~{copied * 1100:N0} tok. 프롬프트를 쓰고 [분석]하세요", $"{copied} images attached — ~{copied * 1100:N0} tok. Write a prompt and [Analyze]"));
        }
        catch (Exception ex)
        {
            _store.LogError(L.T("폴더 일괄 이미지", "Image batch"), ex.Message);
            RefreshErrors();
            AddStatus(L.T("폴더 처리 실패: ", "Folder failed: ") + ex.Message);
        }
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        int before = _attachments.Count;
        foreach (var f in files)
            if (ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant())) AddAttachmentFile(f);
        if (_attachments.Count == before) AddStatus(L.T("이미지 파일만 첨부할 수 있습니다", "Only image files can be attached"));
    }

    private string? SaveClipboardImage(BitmapSource img)
    {
        try
        {
            Directory.CreateDirectory(AttachDir);
            var path = Path.Combine(AttachDir, $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using var fs = new FileStream(path, FileMode.Create);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            enc.Save(fs);
            return path;
        }
        catch (Exception ex) { _store.LogError(L.T("이미지 붙여넣기", "Paste image"), ex.Message); return null; }
    }

    private void AddAttachmentFile(string src, bool fromClipboard = false)
    {
        try
        {
            Directory.CreateDirectory(AttachDir);
            string dest = src;
            if (!fromClipboard)
            {
                dest = Path.Combine(AttachDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(src)}");
                if (File.Exists(dest)) dest = Path.Combine(AttachDir, $"{Guid.NewGuid():N}-{Path.GetFileName(src)}");
                File.Copy(src, dest);
            }
            _attachments.Add(dest);
            RefreshAttachPanel();
            AddStatus(L.T($"이미지 첨부: {Path.GetFileName(dest)} (총 {_attachments.Count}장, 장당 ~1,100 tok)", $"Image attached: {Path.GetFileName(dest)} ({_attachments.Count} total, ~1,100 tok each)"));
        }
        catch (Exception ex) { _store.LogError(L.T("이미지 첨부", "Attach image"), ex.Message); AddStatus(L.T("이미지 첨부 실패: ", "Attach failed: ") + ex.Message); }
    }

    private void RefreshAttachPanel()
    {
        AttachPanel.Children.Clear();
        foreach (var path in _attachments.ToList())
        {
            var img = new Image { Stretch = Stretch.UniformToFill, Width = 46, Height = 46 };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 96;
                bmp.UriSource = new Uri(path); bmp.EndInit();
                img.Source = bmp;
            }
            catch { }
            var remove = IconButton("", "", L.T("첨부 빼기", "Remove attachment"));
            remove.FontSize = 9; remove.Padding = new Thickness(3, 0, 3, 0);
            remove.HorizontalAlignment = HorizontalAlignment.Right; remove.VerticalAlignment = VerticalAlignment.Top;
            string captured = path;
            remove.Click += (_, _) => { _attachments.Remove(captured); RefreshAttachPanel(); };
            var grid = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 6, 0), ToolTip = Path.GetFileName(path) };
            grid.Children.Add(img);
            grid.Children.Add(remove);
            AttachPanel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = grid,
            });
        }
    }

    // ---------- 공용 UI 조각 ----------
    /// <summary>아이콘(Segoe Fluent Icons) + 글자 버튼. 글자가 없으면 접근성 이름을 따로 단다.</summary>
    private Button IconButton(string glyph, string text, string? accessibleName = null)
    {
        var b = new Button
        {
            Content = text, Tag = glyph, ContentTemplate = (DataTemplate)FindResource("IconText"),
            FontSize = 11, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, accessibleName ?? text);
        if (accessibleName != null) b.ToolTip = accessibleName;
        return b;
    }

    private static void Flash(Button b, string done)
    {
        var old = b.Content;
        b.Content = done;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { b.Content = old; t.Stop(); };
        t.Start();
    }

    // ---------- 채팅 ----------
    // 말풍선은 선택 가능한 읽기전용 TextBox — 드래그 선택+Ctrl+C, 또는 [복사] 버튼으로 바로 복붙 가능
    private TextBox AddChat(string who, string text)
    {
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(tb, who);
        var copyBtn = IconButton("", L.T("복사", "Copy"));
        copyBtn.Margin = new Thickness(10, 0, 0, 0);
        copyBtn.Click += (_, _) => { if (tb.Text.Length == 0) return; Clipboard.SetText(tb.Text); Flash(copyBtn, L.T("복사됨", "Copied")); };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = who, FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, copyBtn },
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 4, 0, 4),
            Background = who == Me ? new SolidColorBrush(Color.FromArgb(28, 0, 120, 215)) : new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
            Child = new StackPanel { Children = { header, tb } },
        };
        ChatPanel.Children.Add(border);
        ChatScroll.ScrollToEnd();
        return tb;
    }

    private static string Me => L.T("나", "Me");

    private void OnClearChat(object sender, RoutedEventArgs e)
    {
        ChatPanel.Children.Clear();
        _attachments.Clear();
        _sessionPrompts.Clear();
        RefreshAttachPanel();
    }

    // ---------- 프로젝트·세션 맥락 선택 ----------
    private void BuildProjectCombo(string selectDir, string selectName)
    {
        if (ProjectSelectCombo == null) return;
        ProjectSelectCombo.Items.Clear();
        // 예시 실행에서는 이 PC 의 실제 폴더를 찾지 않는다 (화면에 실제 경로가 나오지 않게)
        var discovered = App.DemoMode ? new List<(string Name, string Dir)>() : Importers.DiscoverProjectDirs(App.Settings.KnowledgeDir);
        var dbProjects = _store.DistinctProjects();

        var autoDir = discovered.FirstOrDefault().Dir ?? (App.DemoMode ? "" : Advisor.FindRepoDir(App.Settings.KnowledgeDir)) ?? "";
        var autoName = !string.IsNullOrEmpty(autoDir) ? Path.GetFileName(autoDir.TrimEnd('\\', '/')) : (dbProjects.FirstOrDefault().Project ?? "");
        ProjectSelectCombo.Items.Add(new ComboBoxItem
        {
            Content = string.IsNullOrEmpty(autoName) ? L.T("자동 감지 (최근 활성 프로젝트)", "Auto-detect (most recent project)") : L.T($"자동 감지 — {autoName}", $"Auto-detect — {autoName}"),
            Tag = "|",
        });

        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selectDir) && Directory.Exists(selectDir))
        {
            var customName = Path.GetFileName(selectDir.TrimEnd('\\', '/'));
            if (addedNames.Add(customName))
                ProjectSelectCombo.Items.Add(new ComboBoxItem { Content = $"{customName} ({Store.DisplayPath(selectDir)})", Tag = $"{customName}|{selectDir}" });
        }
        foreach (var (name, dir) in discovered)
        {
            if (!addedNames.Add(name)) continue;
            ProjectSelectCombo.Items.Add(new ComboBoxItem { Content = $"{name} ({Store.DisplayPath(dir)})", Tag = $"{name}|{dir}" });
        }
        foreach (var (proj, cnt) in dbProjects)
        {
            if (!addedNames.Add(proj)) continue;
            ProjectSelectCombo.Items.Add(new ComboBoxItem { Content = L.T($"{proj} (이력 {cnt:N0}건, 폴더 미지정)", $"{proj} ({cnt:N0} records, no folder)"), Tag = $"{proj}|" });
        }

        ProjectSelectCombo.SelectedIndex = 0;
        foreach (ComboBoxItem item in ProjectSelectCombo.Items)
        {
            var parts = ((string)item.Tag).Split('|', 2);
            if ((!string.IsNullOrEmpty(selectDir) && parts[1].Equals(selectDir, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(selectName) && parts[0].Equals(selectName, StringComparison.OrdinalIgnoreCase)))
            {
                ProjectSelectCombo.SelectedItem = item;
                break;
            }
        }
    }

    private (string Name, string Dir) SelectedProjectInfo
    {
        get
        {
            var tag = (ProjectSelectCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "|";
            var parts = tag.Split('|', 2);
            if (!string.IsNullOrEmpty(parts[0]) || !string.IsNullOrEmpty(parts[1]))
                return (parts[0], parts[1]);
            if (!App.DemoMode)
            {
                var discovered = Importers.DiscoverProjectDirs(App.Settings.KnowledgeDir);
                if (discovered.Count > 0) return discovered[0];
                var repo = Advisor.FindRepoDir(App.Settings.KnowledgeDir);
                if (repo != null) return (Path.GetFileName(repo.TrimEnd('\\', '/')), repo);
            }
            var topDb = _store.DistinctProjects().FirstOrDefault().Project ?? "";
            return (topDb, "");
        }
    }

    private void OnProjectSelectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var tag = (ProjectSelectCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "|";
        var parts = tag.Split('|', 2);
        App.Settings.ActiveProjectName = parts[0];
        App.Settings.ActiveProjectDir = parts[1];
        App.Settings.Save();
        var (name, dir) = SelectedProjectInfo;
        AddStatus(string.IsNullOrEmpty(name)
            ? L.T("프로젝트 자동 감지로 전환", "Project: auto-detect")
            : L.T($"작업 프로젝트: {name}", $"Project: {name}") + (string.IsNullOrEmpty(dir) ? L.T(" (이력 기준)", " (from history)") : $" ({Store.DisplayPath(dir)})"));
    }

    private void OnPickProjectDir(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("작업 중인 프로젝트 루트 폴더 (AGENTS.md · CLAUDE.md · 소스가 있는 곳)", "Project root folder (with AGENTS.md / CLAUDE.md / sources)") };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var dir = dlg.SelectedPath;
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        App.Settings.ActiveProjectDir = dir;
        App.Settings.ActiveProjectName = name;
        App.Settings.Save();
        BuildProjectCombo(dir, name);
        AddStatus(L.T($"작업 프로젝트 폴더 지정: {name}", $"Project folder set: {name}"));
    }

    private void OnResetSessionContext(object sender, RoutedEventArgs e)
    {
        _sessionPrompts.Clear();
        AddStatus(L.T("직전 대화 이어받기 맥락을 초기화했습니다 — 다음 분석은 새 작업으로 시작합니다", "Conversation context cleared — the next analysis starts a new task"));
    }

    private void OnContextTaskChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressContextEvents || _last == null) return;
        if ((ContextTaskCombo.SelectedItem as ComboBoxItem)?.Tag is not string newTask || newTask == _last.TaskType) return;
        _last.TaskType = newTask;
        (_last.EstOutputTokens, _last.CalibrationFactor, _last.CalibrationSamples) =
            Advisor.EstimateOutputTokens(_last.TaskType, _last.Neighbors, _store.CalibrationRows());
        string pinned = SelectedModelName;
        _last.Models = Advisor.RecommendModels(_last.TaskType, _last.EstInputTokens, _last.EstOutputTokens,
            Advisor.LoadModels(), App.Settings.RecommendCount, pinned.Length == 0 ? null : pinned, _store.CalibrationRowsByModelTask());
        RenderAnalysis(_last);
        AddStatus(L.T($"작업 유형을 '{Advisor.TaskLabel(newTask)}'(으)로 바꿨습니다 — 모델 추천과 개선안에 반영됩니다", $"Task type set to '{Advisor.TaskLabel(newTask)}' — used for recommendations and rewrites"));
    }

    private void OnContextGoalChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressContextEvents || _last?.Context == null) return;
        _last.Context.SessionGoal = ContextGoalBox.Text.Trim();
    }

    private void OnAddContextFile(object sender, RoutedEventArgs e)
    {
        if (_last == null) { AddStatus(L.T("먼저 프롬프트를 분석하세요", "Analyze a prompt first")); return; }
        var file = ContextAddFileBox.Text.Trim();
        if (file.Length == 0) return;
        _last.Context ??= new WorkContext();
        int idx = _last.Context.CandidateFiles.FindIndex(c => c.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _last.Context.CandidateFiles[idx] = (_last.Context.CandidateFiles[idx].File, _last.Context.CandidateFiles[idx].Reason, true);
        else
            _last.Context.CandidateFiles.Insert(0, (file, L.T("직접 추가", "added by you"), true));
        ContextAddFileBox.Clear();
        RenderContextFiles(_last.Context);
        AddStatus(L.T($"대상 파일에 '{file}' 추가 — 개선 프롬프트에 반영됩니다", $"Added '{file}' to target files — used in the rewrite"));
    }

    private void RenderContextFiles(WorkContext ctx)
    {
        ContextFilesPanel.Children.Clear();
        if (ctx.CandidateFiles.Count == 0)
        {
            ContextFilesPanel.Children.Add(new TextBlock
            {
                Text = L.T("추론된 후보 파일이 없습니다 (아래에서 직접 추가하거나 프로젝트 폴더를 지정하세요)", "No candidate files (add one below or choose a project folder)"),
                FontSize = 11.5, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        for (int i = 0; i < ctx.CandidateFiles.Count; i++)
        {
            int capturedIdx = i;
            var item = ctx.CandidateFiles[i];
            var cb = new CheckBox { Content = item.File, IsChecked = item.Selected, Margin = new Thickness(0, 2, 10, 2), FontSize = 12, ToolTip = L.T("추론 근거: ", "Why: ") + item.Reason };
            cb.Checked += (_, _) => { if (_suppressContextEvents) return; var cur = ctx.CandidateFiles[capturedIdx]; ctx.CandidateFiles[capturedIdx] = (cur.File, cur.Reason, true); };
            cb.Unchecked += (_, _) => { if (_suppressContextEvents) return; var cur = ctx.CandidateFiles[capturedIdx]; ctx.CandidateFiles[capturedIdx] = (cur.File, cur.Reason, false); };
            ContextFilesPanel.Children.Add(cb);
        }
    }

    // ---------- 모델 선택 ----------
    private void BuildModelCombo(string selectName)
    {
        ModelSelectCombo.Items.Clear();
        ModelSelectCombo.Items.Add(new ComboBoxItem { Content = L.T("자동 추천 (작업 유형에 맞춰서)", "Auto (by task type)"), Tag = "" });
        var models = Advisor.LoadModels();
        foreach (var m in models.Where(m => App.Settings.FavoriteModels.Contains(m.Name)))
            ModelSelectCombo.Items.Add(new ComboBoxItem { Content = L.T("[즐겨찾기] ", "[fav] ") + $"{m.Tool} — {m.Name}", Tag = m.Name });
        foreach (var m in models.Where(m => !App.Settings.FavoriteModels.Contains(m.Name)))
            ModelSelectCombo.Items.Add(new ComboBoxItem { Content = $"{m.Tool} — {m.Name}", Tag = m.Name });
        ModelSelectCombo.SelectedIndex = 0;
        foreach (ComboBoxItem item in ModelSelectCombo.Items)
            if ((string)item.Tag == selectName) ModelSelectCombo.SelectedItem = item;
        UpdateFavModelBtn();
    }

    private void UpdateFavModelBtn()
    {
        if (FavModelBtn == null) return;
        var name = SelectedModelName;
        bool fav = name.Length > 0 && App.Settings.FavoriteModels.Contains(name);
        FavModelBtn.Tag = fav ? "" : "";
        FavModelBtn.Content = fav ? L.T("즐겨찾기 해제", "Unfavorite") : L.T("즐겨찾기", "Favorite");
    }

    private void OnToggleFavModel(object sender, RoutedEventArgs e)
    {
        var name = SelectedModelName;
        if (name.Length == 0) { AddStatus(L.T("자동 추천 항목은 즐겨찾기할 수 없습니다 — 모델을 먼저 고르세요", "Pick a model first (Auto cannot be a favorite)")); return; }
        if (App.Settings.FavoriteModels.Contains(name)) App.Settings.FavoriteModels.Remove(name);
        else App.Settings.FavoriteModels.Add(name);
        App.Settings.Save();
        BuildModelCombo(name);
        AddStatus(App.Settings.FavoriteModels.Contains(name) ? L.T($"{name} 즐겨찾기 — 목록 맨 위에 표시됩니다", $"{name} added to favorites") : L.T($"{name} 즐겨찾기 해제", $"{name} removed from favorites"));
    }

    private string SelectedModelName => (ModelSelectCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OnModelSelectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Settings.SelectedModel = SelectedModelName;
        App.Settings.Save();
        UpdateFavModelBtn();
        AddStatus(App.Settings.SelectedModel.Length == 0 ? L.T("모델 자동 추천으로 전환", "Model: auto") : L.T($"대상 모델 고정: {App.Settings.SelectedModel}", $"Model pinned: {App.Settings.SelectedModel}"));
    }

    // ---------- 분석 ----------
    private async void OnAnalyze(object sender, RoutedEventArgs e) => await AnalyzeCurrentAsync();

    private async Task AnalyzeCurrentAsync()
    {
        var prompt = InputBox.Text.Trim();
        if (prompt.Length == 0) { AddChat(L.T("시스템", "System"), L.T("프롬프트가 비어 있습니다.", "The prompt is empty.")); return; }
        _lastPrompt = prompt;
        AddChat(Me, prompt + (_attachments.Count > 0 ? L.T($"\n[이미지 {_attachments.Count}장 첨부]", $"\n[{_attachments.Count} images attached]") : ""));
        if (!App.Settings.HasAnalyzed) { App.Settings.HasAnalyzed = true; App.Settings.Save(); }
        AnalyzeBtn.IsEnabled = false;
        AnalyzeProgress.Visibility = Visibility.Visible;
        try
        {
            _last = await AnalyzeAsync(prompt);
            _sessionPrompts.Add(prompt);
            if (_sessionPrompts.Count > 5) _sessionPrompts.RemoveAt(0);
            RenderAnalysis(_last);
            RefreshOnboarding();
            var top = _last.Models.FirstOrDefault();
            _store.SavePrediction(prompt, _last.TaskType, _last.EstInputTokens, _last.EstOutputTokens, top?.Model.Name ?? "");
            _sentPromptHashes.Add(Store.HashNorm(prompt));
            SaveTemplateVersionIfEdited(prompt);
        }
        catch (Exception ex)
        {
            _store.LogError(L.T("분석", "Analyze"), ex.Message);
            RefreshErrors();
            AddChat(L.T("시스템", "System"), L.T("분석 중 오류가 났습니다: ", "Analysis failed: ") + ex.Message);
        }
        finally { AnalyzeBtn.IsEnabled = true; AnalyzeProgress.Visibility = Visibility.Collapsed; _ = RefreshOllamaStatus(); }
    }

    private async Task<Analysis> AnalyzeAsync(string prompt)
    {
        var (projName, projDir) = SelectedProjectInfo;
        var projectHistory = !string.IsNullOrWhiteSpace(projName) ? _store.Query("all", limit: 250, project: projName) : _store.RecentSessionPrompts(projName, 10);
        var sessionCopy = _sessionPrompts.ToList();

        var ctx = await Task.Run(() => Advisor.AnalyzePromptContext(prompt, projDir, projName, projectHistory, sessionCopy));
        string task = Advisor.Classify(prompt, ctx);
        var impact = await Task.Run(() => RuleImpact.For(_store, Advisor.Rules));
        var a = new Analysis
        {
            Context = ctx, TaskType = task,
            Lints = Advisor.Lint(prompt, ctx, null, impact),
            EstInputTokens = Advisor.EstimateInputTokens(prompt),
            ImageCount = _attachments.Count,
        };
        if (a.ImageCount > 0)
        {
            a.EstInputTokens += a.ImageCount * 1100; // 이미지 1장 ≈ 1.1k tok (타일 기반 근사)
            foreach (var path in _attachments)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > 2_000_000)
                        a.Lints.Add(new LintHit("big-image", L.T("큰 이미지 첨부", "Large image"),
                            L.T($"{fi.Name} ({fi.Length / 1_000_000.0:F1}MB) — 해상도를 줄이면 이미지 토큰과 업로드 시간을 아낍니다.", $"{fi.Name} ({fi.Length / 1_000_000.0:F1} MB) — downscale it to save image tokens and upload time."),
                            600, 1));
                }
                catch { }
            }
        }

        string embedQuery = ctx.IsFollowUp && !string.IsNullOrWhiteSpace(ctx.SessionGoal) ? $"{ctx.SessionGoal}\n{prompt}" : prompt;
        float[]? queryVec = null;
        if (_store.CountEmbedded() > 0 && await _ollama.IsAvailable())
        {
            queryVec = await _ollama.Embed(embedQuery);
            if (queryVec != null)
            {
                a.Neighbors = Advisor.Knn(queryVec, McpServer.GetCachedCorpus(_store), 5, ctx.ProjectName);
                // 스킬 추천: 키워드로 후보를 추린 뒤 임베딩 유사도 (예시 실행에서는 이 PC 의 스킬 목록을 읽지 않는다)
                if (!App.DemoMode)
                {
                    _skills ??= Advisor.ScanSkills();
                    foreach (var s in Advisor.PickSkillCandidates(_skills, embedQuery))
                    {
                        if (!_skillVecs.TryGetValue(s.Name, out var sv))
                        {
                            sv = await _ollama.Embed(s.Name + " " + s.Description);
                            if (sv != null) _skillVecs[s.Name] = sv;
                        }
                        if (sv != null) a.Skills.Add((s, Advisor.Cosine(queryVec, sv)));
                    }
                    a.Skills = a.Skills.OrderByDescending(x => x.Similarity).Where(x => x.Similarity > 0.35).Take(3).ToList();
                }
            }
        }
        if (a.Neighbors.Count == 0)
        {
            // 임베딩이 없으면 단어 · 3-gram 유사도로 비슷한 과거 사례를 찾는다 (LLM 없이도 동작)
            var corpus = _store.All();
            a.Neighbors = await Task.Run(() => Advisor.TextNeighbors(embedQuery, corpus, 5, ctx.ProjectName));
            a.OllamaNote = a.Neighbors.Count > 0
                ? L.T("임베딩 없이 단어 유사도로 비슷한 사례를 찾았습니다. (Ollama 를 연결하면 의미 검색으로 바뀝니다)", "Similar cases found by word overlap (connect Ollama for semantic search).")
                : null;
        }
        var neighborRisk = Advisor.CheckNeighborFailureRisk(a.Neighbors);
        if (neighborRisk != null) a.Lints.Add(neighborRisk);

        // 입력 토큰도 실측(actual_in)으로 보정. 이미지 토큰은 보정 후 가산.
        long imageTokens = a.ImageCount * 1100;
        (a.EstInputTokens, a.InputCalibrationFactor, a.InputCalibrationSamples) =
            Advisor.CalibratedEstimate(a.EstInputTokens - imageTokens, _store.InputCalibrationRows(), a.TaskType);
        a.EstInputTokens += imageTokens;

        var calibRows = _store.CalibrationRows();
        (a.EstOutputTokens, a.CalibrationFactor, a.CalibrationSamples) = Advisor.EstimateOutputTokens(a.TaskType, a.Neighbors, calibRows);
        a.PredictionErrorPct = Advisor.PredictionErrorPct(calibRows);

        string pinned = SelectedModelName;
        a.Models = Advisor.RecommendModels(a.TaskType, a.EstInputTokens, a.EstOutputTokens,
            Advisor.LoadModels(), App.Settings.RecommendCount, pinned.Length == 0 ? null : pinned, _store.CalibrationRowsByModelTask());
        return a;
    }

    private static Border Chip(string text, Color? accent = null)
    {
        var c = accent ?? Color.FromRgb(0, 130, 160);
        return new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6),
            Background = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
        };
    }

    private void RenderAnalysis(Analysis a)
    {
        SummaryChips.Children.Clear();
        if (!string.IsNullOrWhiteSpace(a.Context?.ProjectName))
            SummaryChips.Children.Add(Chip(L.T("프로젝트 ", "Project ") + a.Context.ProjectName, Green));
        SummaryChips.Children.Add(Chip(L.T("작업: ", "Task: ") + Advisor.TaskLabel(a.TaskType)));
        if (Equipment.IsEquipmentRequest(_lastPrompt))
            SummaryChips.Children.Add(Chip(L.T("장비 SW 요청", "Equipment SW request"), Amber));
        SummaryChips.Children.Add(Chip(L.T($"예상 입력 ~{a.EstInputTokens:N0} tok", $"Input ~{a.EstInputTokens:N0} tok"), Accent));
        SummaryChips.Children.Add(Chip(L.T($"예상 출력 ~{a.EstOutputTokens:N0} tok", $"Output ~{a.EstOutputTokens:N0} tok"), Green));
        SummaryChips.Children.Add(Chip(Advisor.ConfidenceLabel(a.CalibrationSamples, a.PredictionErrorPct),
            a.CalibrationSamples == 0 ? Red : a.CalibrationSamples < 10 ? Orange : Green));
        if (a.InputCalibrationSamples >= 3 && Math.Abs(a.InputCalibrationFactor - 1.0) > 0.01)
            SummaryChips.Children.Add(Chip(L.T($"입력 보정 ×{a.InputCalibrationFactor:F2} (표본 {a.InputCalibrationSamples}건)", $"input calib ×{a.InputCalibrationFactor:F2} (n={a.InputCalibrationSamples})"), Orange));
        if (a.CalibrationSamples >= 3 && Math.Abs(a.CalibrationFactor - 1.0) > 0.01)
            SummaryChips.Children.Add(Chip(L.T($"출력 보정 ×{a.CalibrationFactor:F2} (표본 {a.CalibrationSamples}건)", $"output calib ×{a.CalibrationFactor:F2} (n={a.CalibrationSamples})"), Orange));
        if (a.Neighbors.Count > 0)
            SummaryChips.Children.Add(Chip(L.T($"비슷한 사례 {a.Neighbors.Count}건", $"{a.Neighbors.Count} similar cases"), Purple));
        if (a.ImageCount > 0)
            SummaryChips.Children.Add(Chip(L.T($"이미지 {a.ImageCount}장 (~{a.ImageCount * 1100:N0} tok)", $"{a.ImageCount} images (~{a.ImageCount * 1100:N0} tok)"), Amber));
        if (a.Models.FirstOrDefault()?.Pinned == true)
            SummaryChips.Children.Add(Chip(L.T("지정 모델: ", "Pinned: ") + a.Models[0].Model.Name, Amber));
        AnalysisSummary.Text = a.OllamaNote ?? (a.Neighbors.Count > 0
            ? L.T($"비슷한 과거 사례 평균 출력 {(long)a.Neighbors.Average(n => n.Row.OutTok):N0} tok 기준으로 예측했습니다.", $"Predicted from similar past cases (avg output {(long)a.Neighbors.Average(n => n.Row.OutTok):N0} tok).")
            : L.T("비슷한 사례가 없어 작업 유형별 기본값으로 예측했습니다. 기록이 쌓이면 정확해집니다.", "No similar cases — predicted from task-type defaults. Accuracy improves as history grows."));

        _suppressContextEvents = true;
        try
        {
            foreach (ComboBoxItem item in ContextTaskCombo.Items)
                if ((string)item.Tag == a.TaskType) { ContextTaskCombo.SelectedItem = item; break; }
            if (a.Context != null)
            {
                ContextWhyText.Text = a.Context.WhyInferred + (!string.IsNullOrWhiteSpace(a.Context.ProjectSummary) ? L.T("\n요약: ", "\nSummary: ") + a.Context.ProjectSummary : "");
                ContextGoalBox.Text = a.Context.SessionGoal;
                RenderContextFiles(a.Context);
            }
        }
        finally { _suppressContextEvents = false; }

        LintPanel.Children.Clear();
        LintEmpty.Visibility = a.Lints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int li = 0;
        foreach (var l in a.Lints)
        {
            var card = Card($"{(l.Severity == 2 ? L.T("경고", "Warning") : L.T("주의", "Note"))} · {l.Rule} — " + L.T($"예상 낭비 ~{l.WasteTokens:N0} tok", $"est. waste ~{l.WasteTokens:N0} tok"),
                l.Message, accent: l.Severity == 2 ? Red : Orange);
            LintPanel.Children.Add(card);
            Charts.FadeIn(card, li++ * 40);
        }
        LintGaugePanel.Children.Clear();
        if (a.Lints.Count > 0 && a.EstInputTokens > 0)
        {
            // 경고끼리 겹치므로(같은 요청에서 함께 뜸) 더하지 않고 가장 큰 항목을 쓴다
            long waste = a.Lints.Max(l => l.WasteTokens);
            double ratio = waste / (double)(waste + Math.Max(1, a.EstInputTokens + a.EstOutputTokens));
            LintGaugePanel.Children.Add(new TextBlock
            {
                Text = L.T($"예상 낭비 ~{waste:N0} tok (경고가 겹치므로 가장 큰 항목 기준 · 예상 사용량의 {ratio * 100:F0}%)", $"Est. waste ~{waste:N0} tok (largest single warning, since they overlap · {ratio * 100:F0}% of expected use)"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3),
            });
            LintGaugePanel.Children.Add(Charts.RatioBar(ratio, Orange, 390));
        }
        BeforeAfterTitle.Visibility = Visibility.Collapsed;
        BeforeAfterPanel.Children.Clear();

        NeighborPanel.Children.Clear();
        if (a.Neighbors.Count == 0) NeighborPanel.Children.Add(Note(L.T("비슷한 사례 없음", "No similar cases")));
        foreach (var n in a.Neighbors)
        {
            string proj = string.IsNullOrWhiteSpace(n.Row.Project) ? "" : $" · {n.Row.Project}";
            var card = Card(L.T($"유사도 {n.Similarity:F2} · {n.Row.DisplaySource}{proj} · 입력 {n.Row.InTok:N0}/출력 {n.Row.OutTok:N0} tok · 턴 {n.Row.SegTurns} · 정정 {n.Row.Fail}",
                                $"sim {n.Similarity:F2} · {n.Row.DisplaySource}{proj} · in {n.Row.InTok:N0}/out {n.Row.OutTok:N0} tok · {n.Row.SegTurns} turns · {n.Row.Fail} corrections"),
                n.Row.Preview, singleLine: true);
            card.ToolTip = n.Row.Text.Length > 1200 ? n.Row.Text[..1200] + "…" : n.Row.Text;
            NeighborPanel.Children.Add(card);
        }

        ModelPanel.Children.Clear();
        int mi = 0;
        foreach (var r in a.Models)
        {
            var card = Card($"{r.Rank}. {r.Model.Name} · " + L.T($"적합도 {r.Fit} · 예상 ~{r.EstTotalTokens / 1000.0:F1}k tok", $"fit {r.Fit} · ~{r.EstTotalTokens / 1000.0:F1}k tok")
                            + (r.Cost > 0 ? $" · ~${r.Cost:F3}" : "") + (r.Pinned ? L.T(" · 지정", " · pinned") : ""),
                $"{r.Model.Plan} — {r.Reason}");
            ModelPanel.Children.Add(card);
            Charts.FadeIn(card, mi++ * 50);
        }
        ModelChartPanel.Children.Clear();
        if (a.Models.Count > 0)
        {
            ModelChartPanel.Children.Add(new TextBlock { Text = L.T("모델별 예상 총토큰", "Expected total tokens by model"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 3) });
            ModelChartPanel.Children.Add(Charts.BarChart(a.Models.Select(r => (Label: r.Model.Name, Value: (double)r.EstTotalTokens, ValueText: $"{r.EstTotalTokens / 1000.0:F1}k")).ToList(), 390, 0));
        }

        SkillPanel.Children.Clear();
        if (a.Skills.Count == 0) SkillPanel.Children.Add(Note(App.DemoMode ? L.T("예시 실행에서는 스킬 추천을 끕니다", "Skill suggestions are off in sample mode") : L.T("추천 스킬 없음 (Ollama 임베딩 연결 시 표시)", "No skill suggestions (needs Ollama embeddings)")));
        foreach (var (s, sim) in a.Skills)
            SkillPanel.Children.Add(Card($"{s.Name} ({L.T("유사도", "sim")} {sim:F2})", s.Description.Length > 140 ? s.Description[..140] + "…" : s.Description));
    }

    private static Border Card(string title, string body, bool singleLine = false, Color? accent = null) => new()
    {
        BorderBrush = accent is { } c ? new SolidColorBrush(Color.FromArgb(170, c.R, c.G, c.B)) : new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
        BorderThickness = accent != null ? new Thickness(3, 1, 1, 1) : new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 3, 0, 3),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                new TextBlock
                {
                    Text = body, Opacity = 0.88, FontSize = 12.5,
                    TextWrapping = singleLine ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    TextTrimming = singleLine ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                },
            },
        },
    };

    private static TextBlock Note(string t) => new() { Text = t, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    // ---------- 개선 프롬프트: 규칙안이 결정, LLM 안은 Shadow ----------
    /// <summary>개선안을 로컬 LLM 으로 채점 (의도 유지·대상 명시·조건·출력 형식·군더더기). 실패하면 -1.</summary>
    private async Task<(int score, string reason)> JudgeAsync(string original, string improved)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            await _ollama.ChatStream(
                "너는 프롬프트 품질 심사자다. 원본 의도 유지, 대상·파일 명시, 조건의 명확성, 출력 형식 지정, 불필요한 내용 없음을 기준으로 0~100점 채점한다. 첫 줄에 '점수: NN', 둘째 줄에 한 줄 이유만 출력한다.",
                $"[원본 프롬프트]\n{Advisor.MaskSensitive(original)}\n\n[개선안]\n{improved}\n\n채점해라.",
                tok => sb.Append(tok));
            return Advisor.ParseJudgeResult(sb.ToString());
        }
        catch { return (-1, ""); }
    }

    private static bool IsLocalUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>나란히 두 칸 말풍선: 왼쪽 = 채택안(복사 대상), 오른쪽 = Shadow 안. 아래에 LLM 에 보낸 내용(마스킹 후) 펼침.</summary>
    private (TextBox adopted, TextBox shadow, TextBlock adoptedHead, TextBlock shadowHead, TextBox sentBox, TextBlock footer) AddRewriteBubble(string model)
    {
        TextBox Box() => new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
        var adopted = Box(); var shadow = Box();
        shadow.Opacity = 0.85;
        var adoptedHead = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) };
        var shadowHead = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4), Opacity = 0.85 };
        var footer = new TextBlock { FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var sentBox = Box(); sentBox.FontSize = 12; sentBox.Opacity = 0.8;
        System.Windows.Automation.AutomationProperties.SetName(adopted, L.T("채택한 개선안", "Adopted rewrite"));
        System.Windows.Automation.AutomationProperties.SetName(shadow, L.T("Shadow 개선안", "Shadow rewrite"));

        var copyOnly = IconButton("", L.T("프롬프트만 복사", "Copy prompt only"));
        copyOnly.FontWeight = FontWeights.SemiBold;
        copyOnly.ToolTip = L.T("왼쪽 채택안의 본문만 복사하고, 보낸 개선본으로 기록합니다 (가져오기 때 실제 턴과 이어짐)", "Copies the adopted prompt and records it as sent (matched with real turns on the next import)");
        copyOnly.Click += (_, _) =>
        {
            var only = Advisor.ExtractPromptOnly(adopted.Text);
            if (only.Length == 0) return;
            Clipboard.SetText(only);
            Flash(copyOnly, L.T("복사됨", "Copied"));
            var after = Advisor.LintRewrite(only);
            int blanks = after.Count(l => l.Id.StartsWith("eq-"));
            bool tracked = _store.SaveRewrite(_lastPrompt, only, adoptedHead.Tag as string ?? "rule", _last?.Lints.Select(l => l.Id) ?? Array.Empty<string>(), after.Select(l => l.Id));
            RegisterSentPrompt(only);
            AddStatus(L.T($"프롬프트만 복사 ({only.Length:N0}자) — 원하는 도구에 Ctrl+V", $"Prompt copied ({only.Length:N0} chars) — paste with Ctrl+V")
                + (blanks > 0 ? L.T($" · 장비 정보 칸 {blanks}개가 비어 있습니다 — 채운 뒤 보내세요", $" · {blanks} equipment fields are still empty — fill them before sending") : "")
                + (tracked ? L.T(" · 개선 추적에 기록", " · recorded for tracking") : ""));
            RefreshVerifyTab();
        };
        var copyShadow = IconButton("", L.T("Shadow 안 복사", "Copy shadow"));
        copyShadow.Click += (_, _) => { var t = Advisor.ExtractPromptOnly(shadow.Text); if (t.Length > 0) { Clipboard.SetText(t); Flash(copyShadow, L.T("복사됨", "Copied")); } };

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Children = { new DockPanel { Children = { adoptedHead } }, adopted } };
        var right = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0),
            Child = new StackPanel { Children = { shadowHead, shadow } },
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        var sentExpander = new Expander { Header = L.T("LLM 에 보낸 내용 (마스킹 후)", "What was sent to the LLM (masked)"), Content = sentBox, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = L.T($"개선안 — {model}", $"Rewrite — {model}"), FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, copyOnly, copyShadow },
        };
        copyShadow.Margin = new Thickness(5, 0, 0, 0);
        ChatPanel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 4, 0, 4),
            Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
            Child = new StackPanel { Children = { header, grid, footer, sentExpander } },
        });
        ChatScroll.ScrollToEnd();
        return (adopted, shadow, adoptedHead, shadowHead, sentBox, footer);
    }

    // 현재 입력(또는 마지막 분석)에 대해 만든다. 분석 전이면 자동 분석 → 규칙안 즉시 → (Ollama 가 있으면) LLM 안을 Shadow 로.
    private async void OnRewrite(object sender, RoutedEventArgs e) => await RewriteCurrentAsync();

    private async Task RewriteCurrentAsync()
    {
        var current = InputBox.Text.Trim();
        if (current.Length == 0 && _lastPrompt.Length == 0) { AddChat(L.T("시스템", "System"), L.T("먼저 프롬프트를 입력하세요.", "Enter a prompt first.")); return; }

        RewriteBtn.IsEnabled = false;
        AnalyzeProgress.Visibility = Visibility.Visible;
        try
        {
            if (_last == null || (current.Length > 0 && current != _lastPrompt))
            {
                _lastPrompt = current;
                AddChat(Me, current);
                _last = await AnalyzeAsync(current);
                RenderAnalysis(_last);
                _store.SavePrediction(current, _last.TaskType, _last.EstInputTokens, _last.EstOutputTokens, _last.Models.FirstOrDefault()?.Model.Name ?? "");
                _sentPromptHashes.Add(Store.HashNorm(current));
            }
            if (_last == null) return;

            var top = _last.Models.FirstOrDefault()?.Model ?? new ModelInfo(L.T("추천 모델", "recommended model"), "", "", 0, 0, Array.Empty<string>(), "");
            var (adopted, shadow, adoptedHead, shadowHead, sentBox, footer) = AddRewriteBubble(top.Name);
            string ruleText = Advisor.BuildImprovedPrompt(_lastPrompt, _last, top.Name);
            adopted.Text = ruleText + "\n" + WhyLine(_last);
            adoptedHead.Text = L.T("규칙안 (채택)", "Rule-based (adopted)");
            adoptedHead.Tag = "rule";
            ShowBeforeAfter(ruleText);

            bool online = await _ollama.EnsureAvailable();
            await RefreshOllamaStatus();
            bool hasModel = online && Ollama.Has(await _ollama.InstalledModels(), _ollama.GenModel);
            if (!hasModel)
            {
                shadowHead.Text = L.T("LLM 안 (Shadow) — 없음", "LLM (shadow) — unavailable");
                shadow.Text = online
                    ? L.T($"생성 모델 {_ollama.GenModel} 이(가) 설치되지 않아 LLM 안을 만들지 않았습니다 (ollama pull {_ollama.GenModel}). 규칙안만으로 모든 기능이 동작합니다.",
                          $"The generation model {_ollama.GenModel} is not installed, so no LLM version was made (ollama pull {_ollama.GenModel}). Everything works with the rule-based version.")
                    : L.T("Ollama 가 꺼져 있어 LLM 안을 만들지 않았습니다. 규칙안만으로 모든 기능이 동작합니다.", "Ollama is off, so no LLM version was made. Everything works with the rule-based version.");
                footer.Text = "";
                AddStatus(L.T("규칙 개선안 완료 — [프롬프트만 복사]로 쓰세요", "Rule-based rewrite ready — use [Copy prompt only]"));
                return;
            }

            // LLM 에 보낼 내용: 마스킹 후, URL 이 이 PC 가 아니면 매번 확인
            bool ko = Advisor.IsKorean(_lastPrompt);
            var ctx = _last.Context;
            string masked = Advisor.MaskSensitive(_lastPrompt);
            var eqMissing = _last.Lints.Where(l => l.Id.StartsWith("eq-")).Select(l => Advisor.RuleName(l.Id)).ToList();
            var (system, user) = Advisor.ShadowRequest(masked, ko, top.Name, top.Tool, Advisor.TaskLabel(_last.TaskType), ctx, _last.Lints.Select(l => l.Rule), eqMissing);
            sentBox.Text = $"[system]\n{system}\n\n[user]\n{user}";
            if (!IsLocalUrl(_ollama.BaseUrl) && MessageBox.Show(
                    L.T($"Ollama 주소가 이 PC 가 아닙니다 ({_ollama.BaseUrl}).\n아래 내용(마스킹 후)을 보낼까요?\n\n{(user.Length > 700 ? user[..700] + "…" : user)}",
                        $"The Ollama URL is not this PC ({_ollama.BaseUrl}).\nSend the following (masked) content?\n\n{(user.Length > 700 ? user[..700] + "…" : user)}"),
                    "Prompt Advisor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                shadowHead.Text = L.T("LLM 안 (Shadow) — 보내지 않음", "LLM (shadow) — not sent");
                return;
            }

            shadowHead.Text = L.T("LLM 안 (Shadow, 결정에 쓰지 않음)", "LLM (shadow, not used for the decision)");
            var sb = new System.Text.StringBuilder();
            string llmText = "", note;
            bool onTopic = false, failed = false; int judge = -1;
            try
            {
                await _ollama.ChatStream(system, user, tok => Dispatcher.Invoke(() => { sb.Append(tok); shadow.Text = sb.ToString(); ChatScroll.ScrollToEnd(); }));
                llmText = sb.ToString().Trim();
                string anchorSource = masked + " " + (ctx != null ? string.Join(" ", ctx.SelectedFiles) + " " + ctx.SessionGoal : "");
                onTopic = Advisor.IsOnTopic(anchorSource, llmText);
                if (onTopic)
                {
                    var (score, reason) = await JudgeAsync(_lastPrompt, llmText);
                    judge = score;
                    note = score >= 0 ? L.T($"심사 {score}점", $"review {score}/100") + (reason.Length > 0 ? $" · {reason}" : "") : L.T("심사 불가", "review unavailable");
                }
                else note = L.T("주제 이탈 → 규칙안 사용", "off-topic → rule-based used");
            }
            catch (Exception ex)
            {
                note = L.T("LLM 생성 실패: ", "LLM failed: ") + ex.Message;
                shadow.Text = note;
                failed = true;
            }

            int ruleLints = Advisor.LintRewrite(ruleText).Count, llmLints = llmText.Length > 0 ? Advisor.LintRewrite(llmText).Count : -1;
            bool llmPass = onTopic && judge >= 60;
            string decided = Advisor.Rules.LlmPrimary && llmPass ? "llm" : "rule";
            if (decided == "llm")
            {
                // 전환된 상태: 검증을 통과한 LLM 안을 채택, 규칙안은 대체용으로 옆에 둔다
                adopted.Text = Advisor.ImprovedHeader(top.Name, ko) + "\n" + llmText;
                adoptedHead.Text = L.T("LLM 안 (채택 — 전환됨)", "LLM (adopted — switched)");
                adoptedHead.Tag = "llm";
                shadow.Text = ruleText;
                shadowHead.Text = L.T("규칙안 (대체용)", "Rule-based (fallback)");
            }
            string topic = failed ? L.T("생성 실패", "failed") : onTopic ? L.T("주제 일치", "on-topic") : L.T("주제 이탈", "off-topic");
            footer.Text = L.T($"Shadow 비교: {topic} · {note} · 남은 경고 규칙안 {ruleLints}개 / LLM 안 {(llmLints < 0 ? "-" : llmLints.ToString())}개 · 채택: {(decided == "llm" ? "LLM 안" : "규칙안")}",
                                $"Shadow: {topic} · {note} · remaining warnings rule {ruleLints} / LLM {(llmLints < 0 ? "-" : llmLints.ToString())} · adopted: {(decided == "llm" ? "LLM" : "rule-based")}");
            if (llmText.Length > 0)
                _store.AddShadow(Store.HashNorm(_lastPrompt), top.Name, onTopic, judge, ruleLints, Math.Max(0, llmLints), decided, note);
            RefreshVerifyTab();
            AddStatus(L.T("개선안 완료 — 왼쪽(채택)의 [프롬프트만 복사]로 쓰세요", "Rewrite ready — use [Copy prompt only] on the adopted (left) side"));
        }
        catch (Exception ex) { _store.LogError(L.T("개선 생성", "Rewrite"), ex.Message); RefreshErrors(); AddChat(L.T("시스템", "System"), L.T("개선 생성 중 오류: ", "Rewrite failed: ") + ex.Message); }
        finally { RewriteBtn.IsEnabled = true; AnalyzeProgress.Visibility = Visibility.Collapsed; }
    }

    private static string WhyLine(Analysis a) => a.Lints.Count == 0
        ? L.T("— 왜: 큰 낭비 요소가 없어 구조(작업/출력 형식)만 잡았습니다.", "— Why: no major waste found; only the structure (task/output format) was added.")
        : L.T($"— 왜: {string.Join(", ", a.Lints.Select(l => l.Rule))} 항목을 조건·형식·채울 칸으로 보완했습니다.", $"— Why: addressed {string.Join(", ", a.Lints.Select(l => l.Rule))} with conditions, format and fill-in fields.");

    /// <summary>개선 전 / 후 카드: 경고 수, 해결된 경고, 채워야 남은 칸.</summary>
    private void ShowBeforeAfter(string improved)
    {
        if (_last == null) return;
        var before = _last.Lints.Select(l => l.Id).Distinct().ToList();
        var after = Advisor.LintRewrite(improved).Select(l => l.Id).Distinct().ToList();
        var fixedIds = before.Except(after).ToList();
        var left = after.ToList();
        BeforeAfterTitle.Visibility = Visibility.Visible;
        BeforeAfterPanel.Children.Clear();
        BeforeAfterPanel.Children.Add(Card(L.T($"경고 {before.Count}개 → {after.Count}개", $"Warnings {before.Count} → {after.Count}"),
            L.T("해결: ", "Resolved: ") + (fixedIds.Count > 0 ? string.Join(", ", fixedIds.Select(Advisor.RuleName)) : "-") + "\n"
            + L.T("남음 (채워 보내면 사라짐): ", "Remaining (fill in before sending): ") + (left.Count > 0 ? string.Join(", ", left.Select(Advisor.RuleName)) : "-") + "\n"
            + L.T($"예상 입력 ~{_last.EstInputTokens:N0} → ~{Advisor.EstimateInputTokens(improved):N0} tok (구조를 더해 입력은 늘고, 정정 턴을 줄이는 쪽)",
                  $"Input ~{_last.EstInputTokens:N0} → ~{Advisor.EstimateInputTokens(improved):N0} tok (a bit more input, aiming for fewer correction turns)"),
            accent: after.Count < before.Count ? Green : Orange));
    }
}
