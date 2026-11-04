using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OctezDesk.Core.Health;
using OctezDesk.Core.Provisioning;

namespace OctezDesk.Win;

/// <summary>Theme brushes by key, falling back to a fixed colour if the theme lacks the key.</summary>
internal static class ThemeBrush
{
    public static Brush Get(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

public sealed class HealthBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        HealthLevel.Green => ThemeBrush.Get("SystemFillColorSuccessBrush", Brushes.LimeGreen),
        HealthLevel.Yellow => ThemeBrush.Get("SystemFillColorCautionBrush", Brushes.Gold),
        HealthLevel.Red => ThemeBrush.Get("SystemFillColorCriticalBrush", Brushes.Red),
        _ => ThemeBrush.Get("ControlStrongFillColorDisabledBrush", Brushes.LightGray), // unknown / not set up
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StepStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StepStatus.Done or StepStatus.AlreadyDone => ThemeBrush.Get("SystemFillColorSuccessBrush", Brushes.Green),
        StepStatus.Failed => ThemeBrush.Get("SystemFillColorCriticalBrush", Brushes.Red),
        StepStatus.Running or StepStatus.Checking => ThemeBrush.Get("AccentFillColorDefaultBrush", Brushes.DodgerBlue),
        _ => ThemeBrush.Get("TextFillColorTertiaryBrush", Brushes.Gray),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>KeyType → "tz4 (BLS)" etc.</summary>
public sealed class KeyTypeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is Core.Octez.KeyType t ? Core.Octez.KeyTypeExtensions.Describe(t) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Keeps a ListBox scrolled to its newest item while items are appended.</summary>
public static class AutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || e.NewValue is not true) return;
        // Subscribed once here, not on Loaded, which fires again each time the list is shown.
        // Reset covers a new ItemsSource, e.g. a tab sharing this list switching logs.
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add) ScrollToEnd(list);
            // After a Reset the new items aren't laid out yet.
            else if (args.Action == NotifyCollectionChangedAction.Reset)
                list.Dispatcher.BeginInvoke(() => ScrollToEnd(list), System.Windows.Threading.DispatcherPriority.Background);
        };
        list.Loaded += (_, _) => ScrollToEnd(list);
    }

    private static void ScrollToEnd(ListBox list)
    {
        if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]);
    }
}

/// <summary>A bounded, UI-thread-safe list of log lines for binding to a ListBox.</summary>
/// <param name="filter">Lines for which this returns false are dropped (e.g. routine node chatter).</param>
public sealed class LogBuffer(int maxLines = 2000, Func<string, bool>? filter = null)
{
    public System.Collections.ObjectModel.ObservableCollection<string> Lines { get; } = [];

    public Func<string, bool>? Filter { get; set; } = filter;

    /// <summary>May be called from any thread.</summary>
    public void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || Filter?.Invoke(line) == false) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Lines.Add(line);
            while (Lines.Count > maxLines) Lines.RemoveAt(0);
        });
    }

    public void Clear() => Application.Current?.Dispatcher.BeginInvoke(Lines.Clear);
}
