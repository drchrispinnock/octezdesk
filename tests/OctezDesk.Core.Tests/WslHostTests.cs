using OctezDesk.Core.Wsl;

namespace OctezDesk.Core.Tests;

public class WslHostTests
{
    [Fact]
    public void Crlf_scripts_reach_Linux_with_LF()
    {
        Assert.Equal("set -e\nf=\"$3\"\nrm -f \"$f\"\n", WslHost.ToLinuxText("set -e\r\nf=\"$3\"\r\nrm -f \"$f\"\r\n"));
    }

    [Fact]
    public void LF_text_is_unchanged()
    {
        Assert.Equal("a\nb", WslHost.ToLinuxText("a\nb"));
    }
}
