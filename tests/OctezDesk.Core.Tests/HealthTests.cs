using OctezDesk.Core.Health;
using OctezDesk.Core.Octez;

namespace OctezDesk.Core.Tests;

public class NodeMonitorTests
{
    private static FakeHost RunningNode() => new FakeHost()
        .On(a => a[0] == "systemctl")
        .OnFile(OctezPaths.NodeDefaultsFile, "DATADIR=/n")
        .OnFile("/n/config.json", """{"rpc":{"listen-addrs":["127.0.0.1:8732"]}}""")
        .On(a => a[0] == "wget" && a[^1].EndsWith("/is_bootstrapped"), stdout: """{"bootstrapped":true,"sync_state":"synced"}""")
        .On(a => a[0] == "wget" && a[^1].EndsWith("/network/connections"), stdout: """[{"a":1},{"b":2}]""")
        .On(a => a[0] == "wget" && a[^1].EndsWith("/head/header"), stdout: """{"level":5777474,"timestamp":"2026-11-04T12:13:07Z"}""")
        .On(a => a[0] == "wget" && a[^1].EndsWith("/version"), stdout: """{"version":{"major":24,"minor":1,"additional_info":"release"}}""");

    [Fact]
    public async Task Healthy_node_via_host_rpc_is_green()
    {
        var host = RunningNode();
        var health = await new NodeMonitor(host, new NodeService(host)).CheckAsync();

        Assert.Equal(HealthLevel.Green, health.Level);
        Assert.Equal(5777474, health.Observation.HeadLevel);
        Assert.Equal(2, health.Observation.PeerCount);
        Assert.Equal("24.1", health.Observation.Version);
        Assert.Contains(host.Calls, c => c.Argv[0] == "wget" && c.Argv[^1] == "http://127.0.0.1:8732/chains/main/is_bootstrapped");
    }

    [Fact]
    public async Task Rpc_failure_is_red()
    {
        var host = RunningNode().On(a => a[0] == "wget", exitCode: 4);
        var health = await new NodeMonitor(host, new NodeService(host)).CheckAsync();
        Assert.Equal(HealthLevel.Red, health.Level);
        Assert.False(health.Observation.RpcReachable);
    }

    [Fact]
    public async Task Inactive_service_is_red_without_rpc_calls()
    {
        var host = RunningNode().On(a => a[0] == "systemctl", exitCode: 3);
        var health = await new NodeMonitor(host, new NodeService(host)).CheckAsync();
        Assert.Equal(HealthLevel.Red, health.Level);
        Assert.DoesNotContain(host.Calls, c => c.Argv[0] == "wget");
    }
}

public class HealthEvaluatorTests
{
    private static readonly NodeObservation Healthy = new()
    {
        ServiceActive = true, RpcReachable = true, Bootstrapped = true, SyncState = "synced", PeerCount = 12,
    };

    [Fact]
    public void Synced_with_peers_is_green() => Assert.Equal(HealthLevel.Green, HealthEvaluator.Evaluate(Healthy).Level);

    [Fact]
    public void Service_down_is_red()
        => Assert.Equal(HealthLevel.Red, HealthEvaluator.Evaluate(Healthy with { ServiceActive = false }).Level);

    [Fact]
    public void Rpc_unreachable_is_red()
        => Assert.Equal(HealthLevel.Red, HealthEvaluator.Evaluate(Healthy with { RpcReachable = false }).Level);

    [Theory]
    [InlineData(false, "unsynced")]
    [InlineData(true, "stuck")]
    [InlineData(true, "unsynced")]
    public void Not_synced_is_yellow(bool bootstrapped, string syncState)
        => Assert.Equal(HealthLevel.Yellow,
            HealthEvaluator.Evaluate(Healthy with { Bootstrapped = bootstrapped, SyncState = syncState }).Level);

    [Fact]
    public void No_peers_is_yellow()
        => Assert.Equal(HealthLevel.Yellow, HealthEvaluator.Evaluate(Healthy with { PeerCount = 0 }).Level);

    [Fact]
    public void Unknown_peer_count_does_not_degrade()
        => Assert.Equal(HealthLevel.Green, HealthEvaluator.Evaluate(Healthy with { PeerCount = null }).Level);
}
