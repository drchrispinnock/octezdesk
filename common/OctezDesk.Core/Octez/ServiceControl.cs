using OctezDesk.Core.Hosting;

namespace OctezDesk.Core.Octez;

/// <summary>The Octez systemd units the dashboard can start and stop.</summary>
public enum OctezService { Node, DalNode, Baker }

public enum ServiceAction { Start, Stop, Restart }

/// <summary>
/// Starts, stops and restarts the Octez units from the dashboard's ☰ menu.
/// Stop doesn't disable a unit, so it starts again the next time the distro boots. Disabling would
/// also make the DAL node and baker look "Not set up", and the wizard would then switch them off.
/// </summary>
public sealed class ServiceControl(IOctezHost host)
{
    public static string UnitName(OctezService service) => service switch
    {
        OctezService.Node => OctezPaths.NodeService,
        OctezService.DalNode => OctezPaths.DalNodeService,
        OctezService.Baker => OctezPaths.BakerService,
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    /// <summary>"node", "DAL node", "baker": for "Stop the baker?" and similar.</summary>
    public static string DisplayName(OctezService service) => service switch
    {
        OctezService.Node => "node",
        OctezService.DalNode => "DAL node",
        OctezService.Baker => "baker",
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    /// <summary>"Starting the baker…", for the dashboard's busy text.</summary>
    public static string Describe(OctezService service, ServiceAction action) => action switch
    {
        ServiceAction.Start => "Starting",
        ServiceAction.Stop => "Stopping",
        ServiceAction.Restart => "Restarting",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    } + " the " + DisplayName(service);

    public async Task RunAsync(OctezService service, ServiceAction action, CancellationToken ct = default)
    {
        var verb = action switch
        {
            ServiceAction.Start => "start",
            ServiceAction.Stop => "stop",
            ServiceAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        (await host.RunAsync(["systemctl", verb, UnitName(service)], ct: ct)).EnsureSuccess(Describe(service, action));
    }

    /// <summary>
    /// "Deconfigure and stop": stops the DAL node or baker and stops it starting with the distro,
    /// so the dashboard shows it as not set up. Its package, data and the baking keys are kept, and
    /// "Set up DAL node…" or "Set up baker…" turns it on again. Turning off the DAL node also
    /// switches the baker to bake without DAL, which it can't reach any more.
    /// </summary>
    public async Task TurnOffAsync(OctezService service, CancellationToken ct = default)
    {
        if (service == OctezService.Node)
            throw new ArgumentException("The node can't be turned off this way; stop it instead.", nameof(service));
        (await host.RunAsync(["systemctl", "disable", "--now", UnitName(service)], ct: ct))
            .EnsureSuccess($"Turning off the {DisplayName(service)}");

        if (service == OctezService.DalNode
            && await host.TryReadFileAsync(OctezPaths.BakerDefaultsFile, ct) is { } bakerDefaults
            && BakerService.WithoutDal(bakerDefaults) is { } updated)
        {
            await host.WriteFileAsync(OctezPaths.BakerDefaultsFile, updated, ct);
            (await host.RunAsync(["systemctl", "try-restart", OctezPaths.BakerService], ct: ct))
                .EnsureSuccess("Restarting the baker without DAL");
        }
    }
}
