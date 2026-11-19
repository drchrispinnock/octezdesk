using OctezDesk.Core.Hosting;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Tests;

public class ServiceControlTests
{
    [Theory]
    [InlineData(OctezService.Node, ServiceAction.Start, "start", "octez-node")]
    [InlineData(OctezService.DalNode, ServiceAction.Stop, "stop", "octez-dal-node")]
    [InlineData(OctezService.Baker, ServiceAction.Restart, "restart", "octez-baker")]
    public async Task Runs_systemctl_on_the_unit(OctezService service, ServiceAction action, string verb, string unit)
    {
        var host = new FakeHost();
        await new ServiceControl(host).RunAsync(service, action);
        var call = Assert.Single(host.Calls);
        Assert.Equal(["systemctl", verb, unit], call.Argv);
        Assert.Equal(HostUser.Root, call.User);
    }

    [Fact]
    public async Task Stop_never_disables_the_unit()
    {
        var host = new FakeHost();
        await new ServiceControl(host).RunAsync(OctezService.Baker, ServiceAction.Stop);
        Assert.False(host.Ran("systemctl", "disable"));
    }

    [Fact]
    public async Task Failure_names_the_service()
    {
        var host = new FakeHost().On(a => a is ["systemctl", ..], exitCode: 1);
        var ex = await Assert.ThrowsAsync<HostCommandException>(
            () => new ServiceControl(host).RunAsync(OctezService.DalNode, ServiceAction.Start));
        Assert.StartsWith("Starting the DAL node failed", ex.Message);
    }
}
