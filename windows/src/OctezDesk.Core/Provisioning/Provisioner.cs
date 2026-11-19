namespace OctezDesk.Core.Provisioning;

/// <summary>
/// One idempotent setup step. <see cref="IsDoneAsync"/> inspects the real state of the host, so
/// an interrupted or failed run can simply be run again and resumes at the first unfinished step.
/// </summary>
public interface IProvisioningStep
{
    string Title { get; }
    Task<bool> IsDoneAsync(CancellationToken ct);
    Task RunAsync(Action<string> log, CancellationToken ct);
}

public enum StepStatus
{
    Pending,
    Checking,
    Running,
    AlreadyDone,
    Done,
    Failed,
}

public sealed record StepUpdate(int Index, IProvisioningStep Step, StepStatus Status, string? Error = null);

public sealed class Provisioner(IReadOnlyList<IProvisioningStep> steps)
{
    public IReadOnlyList<IProvisioningStep> Steps { get; } = steps;

    /// <summary>Runs every step that is not already done, in order. Stops at the first failure.</summary>
    /// <returns>True if all steps completed.</returns>
    public async Task<bool> RunAsync(IProgress<StepUpdate> progress, Action<string> log, CancellationToken ct = default)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            try
            {
                progress.Report(new(i, step, StepStatus.Checking));
                if (await step.IsDoneAsync(ct))
                {
                    progress.Report(new(i, step, StepStatus.AlreadyDone));
                    continue;
                }

                progress.Report(new(i, step, StepStatus.Running));
                log($"== {step.Title}");
                await step.RunAsync(log, ct);
                progress.Report(new(i, step, StepStatus.Done));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"!! {step.Title} failed: {ex.Message}");
                progress.Report(new(i, step, StepStatus.Failed, ex.Message));
                return false;
            }
        }
        return true;
    }
}
