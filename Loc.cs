using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PromptAdvisor;

/// <summary>
/// 한/영 문자열. 코드 문자열은 L.T("한국어", "English") 로 나란히 두고,
/// XAML 리터럴은 Xaml 표(한국어 → 영어)로 창을 띄울 때 한 번 바꾼다. 언어 변경은 재시작 후 적용.
/// </summary>
public static partial class L
{
    public static bool En { get; private set; }

    /// <summary>pref: "" = OS 언어 (한국어 OS 면 한국어, 아니면 영어) | ko | en</summary>
    public static void Init(string? pref)
        => En = pref == "en" || (pref != "ko" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ko");

    public static string T(string ko, string en) => En ? en : ko;

    /// <summary>XAML 리터럴 번역. 표에 없으면 원문 그대로.</summary>
    public static string X(string ko) => En && Xaml.TryGetValue(ko.Trim(), out var en) ? en : ko;

    /// <summary>창의 논리 트리를 돌며 Text · Content · Header · ToolTip · Title 문자열을 영어로 바꾼다.</summary>
    public static void Apply(DependencyObject root)
    {
        if (!En) return;
        Walk(root);
    }

    private static void Walk(object node)
    {
        switch (node)
        {
            case Window w: w.Title = X(w.Title); break;
            case TextBlock tb when tb.Inlines.Count <= 1 && !string.IsNullOrEmpty(tb.Text): tb.Text = X(tb.Text); break;
            case TextBox tx when tx.IsReadOnly && !string.IsNullOrEmpty(tx.Text): tx.Text = X(tx.Text); break;
            case HeaderedContentControl h when h.Header is string hs: h.Header = X(hs); break;
            case DataGrid dg:
                foreach (var c in dg.Columns) if (c.Header is string chs) c.Header = X(chs);
                break;
        }
        if (node is ContentControl cc && cc.Content is string cs) cc.Content = X(cs);
        if (node is FrameworkElement fe)
        {
            if (fe.ToolTip is string tt) fe.ToolTip = X(tt);
            var an = System.Windows.Automation.AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(an)) System.Windows.Automation.AutomationProperties.SetName(fe, X(an));
        }
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
    }
}
