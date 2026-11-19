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
}
