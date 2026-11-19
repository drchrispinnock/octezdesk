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
    public async Task Turning_off_the_baker_disables_it()
    {
        var host = new FakeHost();
        await new ServiceControl(host).TurnOffAsync(OctezService.Baker);
        Assert.True(host.Ran("systemctl", "disable", "--now", "octez-baker"));
        Assert.False(host.Ran("systemctl", "try-restart"));
    }

    [Fact]
    public async Task Turning_off_the_dal_node_makes_the_baker_bake_without_it()
    {
        var withDal = BakerService.DefaultsFile("/d", ["baker"], 10732, new BakerSettings(), 8732);
        var host = new FakeHost().OnFile(OctezPaths.BakerDefaultsFile, withDal);
        await new ServiceControl(host).TurnOffAsync(OctezService.DalNode);

        Assert.True(host.Ran("systemctl", "disable", "--now", "octez-dal-node"));
        var write = Assert.Single(host.Calls, c => c.StdIn is not null);
        Assert.Equal(BakerService.DefaultsFile("/d", ["baker"], null, new BakerSettings(), 8732), write.StdIn);
        Assert.True(host.Ran("systemctl", "try-restart", "octez-baker"));
    }

    [Fact]
    public async Task Turning_off_the_dal_node_leaves_a_baker_without_dal_alone()
    {
        var host = new FakeHost().OnFile(OctezPaths.BakerDefaultsFile,
            BakerService.DefaultsFile("/d", ["baker"], null, new BakerSettings(), 8732));
        await new ServiceControl(host).TurnOffAsync(OctezService.DalNode);
        Assert.DoesNotContain(host.Calls, c => c.StdIn is not null);
        Assert.False(host.Ran("systemctl", "try-restart"));
    }

    [Fact]
    public async Task The_node_cannot_be_turned_off()
        => await Assert.ThrowsAsync<ArgumentException>(() => new ServiceControl(new FakeHost()).TurnOffAsync(OctezService.Node));

    [Fact]
    public async Task Failure_names_the_service()
    {
        var host = new FakeHost().On(a => a is ["systemctl", ..], exitCode: 1);
        var ex = await Assert.ThrowsAsync<HostCommandException>(
            () => new ServiceControl(host).RunAsync(OctezService.DalNode, ServiceAction.Start));
        Assert.StartsWith("Starting the DAL node failed", ex.Message);
    }
}
