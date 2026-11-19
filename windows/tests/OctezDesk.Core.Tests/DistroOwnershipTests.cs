using OctezDesk.Core.Hosting;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Core.Tests;

public class DistroOwnershipTests
{
    private static Func<string, Task<DistroOwner>> Owners(params (string Name, DistroOwner Owner)[] owners)
        => name => Task.FromResult(owners.Single(o => o.Name == name).Owner);

    [Fact]
    public async Task No_distro_uses_the_plain_name()
        => Assert.Equal("OctezDesk", await DistroOwnership.ResolveNameAsync(["Ubuntu"], Owners()));

    [Fact]
    public async Task Our_distro_keeps_its_name()
        => Assert.Equal("OctezDesk", await DistroOwnership.ResolveNameAsync(
            ["Ubuntu", "OctezDesk"], Owners(("OctezDesk", DistroOwner.OctezDesk))));

    [Fact]
    public async Task Someone_elses_OctezDesk_is_left_alone()
        => Assert.Equal("OctezDesk-2", await DistroOwnership.ResolveNameAsync(
            ["octezdesk"], Owners(("octezdesk", DistroOwner.Other), ("OctezDesk", DistroOwner.Other))));

    [Fact]
    public async Task Our_second_distro_is_found_even_after_the_name_frees_up()
        => Assert.Equal("OctezDesk-2", await DistroOwnership.ResolveNameAsync(
            ["OctezDesk-2"], Owners(("OctezDesk-2", DistroOwner.OctezDesk))));

    [Fact]
    public async Task A_distro_that_cannot_be_checked_is_not_duplicated()
        => Assert.Equal("OctezDesk", await DistroOwnership.ResolveNameAsync(
            ["OctezDesk"], Owners(("OctezDesk", DistroOwner.Unknown))));

    [Fact]
    public async Task Skips_every_name_taken_by_others()
    {
        var taken = DistroOwnership.CandidateNames.Take(3).ToList();
        var name = await DistroOwnership.ResolveNameAsync(taken, _ => Task.FromResult(DistroOwner.Other));
        Assert.Equal("OctezDesk-4", name);
    }

    [Fact]
    public async Task All_names_taken_is_an_error()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => DistroOwnership.ResolveNameAsync(
            DistroOwnership.CandidateNames, _ => Task.FromResult(DistroOwner.Other)));

    [Theory]
    [InlineData(0, "octezdesk\n", DistroOwner.OctezDesk)]
    [InlineData(0, "other\n", DistroOwner.Other)]
    [InlineData(1, "", DistroOwner.Unknown)]
    [InlineData(0, "", DistroOwner.Unknown)]
    public async Task Check_reads_the_script_output(int exit, string stdout, DistroOwner expected)
    {
        var host = new FakeHost().On(a => a is ["sh", "-c", DistroOwnership.CheckScript, ..], exit, stdout);
        Assert.Equal(expected, await DistroOwnership.CheckAsync(host));
    }

    [Fact]
    public void Check_recognises_distros_from_earlier_builds()
    {
        Assert.Contains("/var/tezos/.octezdesk-node.json", DistroOwnership.CheckScript);
        Assert.Contains("# Managed by OctezDesk", DistroOwnership.CheckScript);
        Assert.StartsWith("# Managed by OctezDesk", Provisioning.EnableSystemdStep.WslConf);
        Assert.Equal(Octez.OctezPaths.OctezDeskNodeSettingsFile, "/var/tezos/.octezdesk-node.json");
    }
}
