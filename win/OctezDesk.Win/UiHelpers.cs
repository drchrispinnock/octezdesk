using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OctezDesk.Core.Health;

namespace OctezDesk.Win;

public sealed class HealthBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        HealthLevel.Green => Brushes.LimeGreen,
        HealthLevel.Yellow => Brushes.Gold,
        HealthLevel.Red => Brushes.Red,
        _ => Brushes.LightGray, // unknown / not yet checked
    };

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
        list.Loaded += (_, _) =>
        {
            if (list.Items is INotifyCollectionChanged items)
                items.CollectionChanged += (_, args) =>
                {
                    if (args.Action == NotifyCollectionChangedAction.Add && list.Items.Count > 0)
                        list.ScrollIntoView(list.Items[^1]);
                };
        };
    }
}

/// <summary>A bounded, UI-thread-safe list of log lines for binding to a ListBox.</summary>
public sealed class LogBuffer(int maxLines = 2000)
{
    public System.Collections.ObjectModel.ObservableCollection<string> Lines { get; } = [];

    /// <summary>May be called from any thread.</summary>
    public void Append(string line) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        Lines.Add(line);
        while (Lines.Count > maxLines) Lines.RemoveAt(0);
    });

    public void Clear() => Application.Current?.Dispatcher.BeginInvoke(Lines.Clear);
}
