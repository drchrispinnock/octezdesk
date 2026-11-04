using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OctezDesk.Win.Views;

/// <summary>"① Welcome — ② Node — ③ Set up" indicator shown at the top of each wizard page.</summary>
public sealed class WizardSteps : StackPanel
{
    public static readonly string[] Titles = ["Welcome", "Node", "Set up"];

    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(int), typeof(WizardSteps), new PropertyMetadata(1, (d, _) => ((WizardSteps)d).Build()));

    /// <summary>1-based index of the current step.</summary>
    public int Current
    {
        get => (int)GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    public WizardSteps()
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 0, 0, 24);
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        Children.Clear();
        var accent = ThemeBrush.Get("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
        var onAccent = ThemeBrush.Get("TextOnAccentFillColorPrimaryBrush", Brushes.White);
        var muted = ThemeBrush.Get("TextFillColorTertiaryBrush", Brushes.Gray);
        var text = ThemeBrush.Get("TextFillColorPrimaryBrush", Brushes.Black);

        for (var i = 1; i <= Titles.Length; i++)
        {
            var done = i < Current;
            var active = i == Current;

            var circle = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            circle.Children.Add(new Ellipse
            {
                Fill = done || active ? accent : Brushes.Transparent,
                Stroke = done || active ? accent : muted,
                StrokeThickness = 1.5,
            });
            circle.Children.Add(new TextBlock
            {
                Text = done ? "✓" : i.ToString(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = done || active ? onAccent : muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Children.Add(circle);
            Children.Add(new TextBlock
            {
                Text = Titles[i - 1],
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? text : muted,
            });
            if (i < Titles.Length)
                Children.Add(new Rectangle
                {
                    Width = 40, Height = 1, Fill = muted, Margin = new Thickness(12, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
        }
    }
}
