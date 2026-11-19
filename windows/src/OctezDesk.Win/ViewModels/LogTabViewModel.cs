using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OctezDesk.Win.ViewModels;

/// <summary>One tab of the dashboard's log panel: follows one service's log file.</summary>
/// <param name="follow">Streams the log to the callback until cancelled (e.g. <c>NodeService.FollowLogAsync</c>).</param>
/// <param name="routine">Lines hidden unless "Show routine messages" is on; null if this log has none.</param>
public sealed partial class LogTabViewModel(
    string title, Func<Action<string>, CancellationToken, Task> follow, Regex? routine = null) : ObservableObject
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private CancellationToken _parent;
    private CancellationTokenSource? _cts;

    public string Title { get; } = title;

    public LogBuffer Log { get; } = new(filter: HideRoutine(routine));

    public bool HasRoutineMessages => routine is not null;

    [ObservableProperty] private bool _showRoutine;

    partial void OnShowRoutineChanged(bool value)
    {
        if (routine is null) return;
        Log.Filter = value ? null : HideRoutine(routine);
        Restart(); // re-read the recent lines with the new filter
    }

    private static Func<string, bool>? HideRoutine(Regex? routine)
        => routine is null ? null : line => !routine.IsMatch(line);

    /// <summary>Starts following the log; stops when <paramref name="ct"/> is cancelled.</summary>
    public void Start(CancellationToken ct)
    {
        _parent = ct;
        Restart();
    }

    private void Restart()
    {
        _cts?.Cancel();
        Log.Clear();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_parent);
        _ = FollowLoopAsync(_cts.Token);
    }

    /// <summary>tail -F exits if the distro restarts, so keep re-attaching.</summary>
    private async Task FollowLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Run(() => follow(Log.Append, ct), ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Append($"(log unavailable: {ex.Message})"); }

            try { await Task.Delay(RetryInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
