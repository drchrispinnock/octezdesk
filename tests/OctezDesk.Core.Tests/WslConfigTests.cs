using OctezDesk.Core.Provisioning;
using OctezDesk.Core.Wsl;

namespace OctezDesk.Core.Tests;

public class WslConfigFileTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("[wsl2]\nmemory=8GB\n", null)]
    [InlineData("[wsl2]\nnetworkingMode=nat\n", "nat")]
    [InlineData("[WSL2]\r\n  NetworkingMode = \"Mirrored\" # comment\r\n", "Mirrored")]
    [InlineData("[experimental]\nnetworkingMode=mirrored\n", null)]
    [InlineData("[wsl2]\n# networkingMode=mirrored\n", null)]
    public void GetNetworkingMode_reads_wsl2_section_only(string? content, string? expected)
        => Assert.Equal(expected, WslConfigFile.GetNetworkingMode(content));

    [Fact]
    public void Creates_file_content_when_missing()
        => Assert.Equal("[wsl2]\r\nnetworkingMode=mirrored\r\n", WslConfigFile.WithMirroredNetworking(null));

    [Fact]
    public void Adds_key_to_existing_wsl2_section_and_keeps_the_rest()
    {
        var before = "# mine\n[wsl2]\nmemory=8GB\n\n[experimental]\nautoMemoryReclaim=gradual\n";
        Assert.Equal(
            "# mine\n[wsl2]\nmemory=8GB\nnetworkingMode=mirrored\n\n[experimental]\nautoMemoryReclaim=gradual\n",
            WslConfigFile.WithMirroredNetworking(before));
    }

    [Fact]
    public void Appends_wsl2_section_when_absent()
        => Assert.Equal(
            "[experimental]\nsparseVhd=false\n\n[wsl2]\nnetworkingMode=mirrored\n",
            WslConfigFile.WithMirroredNetworking("[experimental]\nsparseVhd=false\n\n"));

    [Fact]
    public void Replaces_existing_value()
        => Assert.Equal("[wsl2]\r\nnetworkingMode=mirrored\r\n", WslConfigFile.WithMirroredNetworking("[wsl2]\r\nnetworkingMode=nat\r\n"));
}

public class EnableMirroredNetworkingStepTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"wslconfig-{Guid.NewGuid():N}");

    public void Dispose()
    {
        File.Delete(_config);
        File.Delete(_config + ".octezdesk-backup");
    }

    private EnableMirroredNetworkingStep Step(FakeHost host) => new(new WslManager(), host, _config);

    [Fact]
    public async Task Done_when_wsl_reports_mirrored()
    {
        var host = new FakeHost().On(a => a is ["wslinfo", "--networking-mode"], stdout: "mirrored\n");
        Assert.True(await Step(host).IsDoneAsync(default));
    }

    [Fact]
    public async Task Not_done_in_nat_mode_without_a_user_choice()
    {
        var host = new FakeHost().On(a => a is ["wslinfo", "--networking-mode"], stdout: "nat\n");
        File.WriteAllText(_config, "[wsl2]\nmemory=8GB\n");
        Assert.False(await Step(host).IsDoneAsync(default));
    }

    [Fact]
    public async Task Leaves_a_networking_mode_chosen_by_the_user_alone()
    {
        var host = new FakeHost().On(a => a is ["wslinfo", "--networking-mode"], stdout: "nat\n");
        File.WriteAllText(_config, "[wsl2]\nnetworkingMode=nat\n");
        Assert.True(await Step(host).IsDoneAsync(default));
    }
}
