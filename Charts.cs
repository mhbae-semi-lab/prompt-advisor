using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PromptAdvisor;

/// <summary>외부 라이브러리 없는 경량 차트 (Canvas). 막대 너비 애니메이션 포함.</summary>
public static class Charts
{
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0x00, 0xA6, 0xC8), Color.FromRgb(0x00, 0x78, 0xD4),
        Color.FromRgb(0xE8, 0x6B, 0x00), Color.FromRgb(0x1E, 0x9E, 0x57),
        Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xD4, 0x8B, 0x0F),
    };

    private static Brush TextBrush()
    {
        try { if (Application.Current?.TryFindResource("TextFillColorPrimaryBrush") is Brush b) return b; }
        catch { }
        return Brushes.Gray;
    }

    /// <summary>레이블 + 가로 막대 + 값 텍스트 행들. highlightIndex는 강조색 유지, 나머지는 옅게.</summary>
    public static Canvas BarChart(IReadOnlyList<(string Label, double Value, string ValueText)> items,
        double width = 370, int highlightIndex = 0)
    {
        const double rowH = 24, labelW = 118, valueW = 86;
        var canvas = new Canvas { Width = width, Height = Math.Max(1, items.Count) * rowH };
        if (items.Count == 0) return canvas;
        double max = Math.Max(1, items.Max(i => i.Value));
        double barMax = Math.Max(40, width - labelW - valueW - 8);
        var tb = TextBrush();

        for (int i = 0; i < items.Count; i++)
        {
            double y = i * rowH + 3;
            var (label, value, valueText) = items[i];
            var color = Palette[i % Palette.Length];
            if (highlightIndex >= 0 && i != highlightIndex) color = Color.FromArgb(90, color.R, color.G, color.B);

            var name = new TextBlock
            {
                Text = label, Width = labelW - 6, FontSize = 11.5, Foreground = tb,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            };
            Canvas.SetLeft(name, 0); Canvas.SetTop(name, y + 1);
            canvas.Children.Add(name);

            double target = barMax * value / max;
            var bar = new Rectangle
            {
                Height = 13, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(color), Width = 0,
            };
            Canvas.SetLeft(bar, labelW); Canvas.SetTop(bar, y + 2);
            canvas.Children.Add(bar);
            bar.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(0, target, new Duration(TimeSpan.FromMilliseconds(450)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, BeginTime = TimeSpan.FromMilliseconds(i * 60) });

            var val = new TextBlock { Text = valueText, FontSize = 11.5, Foreground = tb, FontWeight = FontWeights.SemiBold };
            Canvas.SetLeft(val, labelW + barMax + 6); Canvas.SetTop(val, y + 1);
            canvas.Children.Add(val);
        }
        return canvas;
    }

    /// <summary>0..1 비율 게이지 (배경 + 채움 애니메이션).</summary>
    public static Grid RatioBar(double ratio, Color color, double width = 370, double height = 12)
    {
        var grid = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128)),
        });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        grid.Children.Add(fill);
        double target = width * Math.Clamp(ratio, 0, 1);
        fill.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(0, target, new Duration(TimeSpan.FromMilliseconds(500)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        return grid;
    }

    /// <summary>카드 페이드-인 (stagger 딜레이 ms).</summary>
    public static void FadeIn(UIElement el, int delayMs = 0)
    {
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160)))
            { BeginTime = TimeSpan.FromMilliseconds(delayMs) });
    }
}
