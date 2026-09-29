using System.Net;
using Rtfc.Core;
using Rtfc.Daemon;

namespace Rtfc.Tests;

/// <summary>Spec §8.4: what a device advertises, how the user changes it, and which addresses are worth advertising at all.</summary>
public class HintsTests
{
    private static (int Code, string Output, string Error) Run(RtfcHome home, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = EntryPoint.Run(args, stdout, stderr, home);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Hints_are_shown_added_removed_and_reset_in_config_json()
    {
        using var temp = new TempHome();
        Assert.Equal(1, Run(temp.Home, "hints").Code);

        Run(temp.Home, "init", "--handle", "alex", "--device", "desktop", "--port", "0", "--hint-host", "10.0.0.5");
        var (code, output, _) = Run(temp.Home, "hints");
        Assert.Equal(0, code);
        Assert.Contains("  tcp:10.0.0.5:0", output);
        Assert.Contains("Set by hand in config.json", output);
        Assert.Contains("not running", output);

        (code, output, _) = Run(temp.Home, "hints", "add", "100.101.5.7", "alex.tailnet.ts.net");
        Assert.Equal(0, code);
        Assert.Equal(["10.0.0.5", "100.101.5.7", "alex.tailnet.ts.net"], ConfigFile.Load(temp.Home).HintHosts!);
        Assert.Contains("  tcp:alex.tailnet.ts.net:0", output);
        Assert.Contains("Contacts learn these the next time you talk to them", output);

        Assert.Equal(0, Run(temp.Home, "hints", "add", "100.101.5.7").Code);
        Assert.Equal(3, ConfigFile.Load(temp.Home).HintHosts!.Length);

        Assert.Equal(0, Run(temp.Home, "hints", "remove", "10.0.0.5").Code);
        Assert.Equal(["100.101.5.7", "alex.tailnet.ts.net"], ConfigFile.Load(temp.Home).HintHosts!);

        Assert.Equal(1, Run(temp.Home, "hints", "remove", "100.101.5.7", "alex.tailnet.ts.net").Code);
        Assert.Equal(1, Run(temp.Home, "hints", "add", "bad host").Code);
        Assert.Equal(2, Run(temp.Home, "hints", "frobnicate").Code);
        Assert.Equal(["100.101.5.7", "alex.tailnet.ts.net"], ConfigFile.Load(temp.Home).HintHosts!);

        (code, output, _) = Run(temp.Home, "hints", "auto");
        Assert.Equal(0, code);
        Assert.Null(ConfigFile.Load(temp.Home).HintHosts);
        Assert.Contains("Auto-detected", output);
    }

    [Theory]
    [InlineData("10.102.8.212", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("100.101.5.7", true)]
    [InlineData("169.254.212.135", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    public void Only_addresses_a_contact_could_use_are_advertised(string address, bool advertisable) =>
        Assert.Equal(advertisable, HintHosts.IsAdvertisable(IPAddress.Parse(address)));

    [Fact]
    public void Detection_never_advertises_link_local_or_loopback_addresses()
    {
        var detected = HintHosts.Detect();

        Assert.NotEmpty(detected);
        Assert.DoesNotContain(detected, host => host.StartsWith("169.254.", StringComparison.Ordinal) || host.StartsWith("127.", StringComparison.Ordinal));
        Assert.Equal(detected.Count, detected.Distinct().Count());
    }
}
