using Rtfc.Cli;
using Rtfc.Core;
using Rtfc.Daemon;
using Rtfc.Identity;

namespace Rtfc.Tests.Daemon;

/// <summary>Records what the command asked of it; never touches the machine's real startup settings.</summary>
public sealed class FakeLoginItem : ILoginItem
{
    public bool Installed { get; private set; }
    public string? Executable { get; private set; }
    public string? Fail { get; set; }

    public string Where => "a fake login item";

    public bool IsInstalled() => Installed;

    public Task<string?> InstallAsync(RtfcHome home, string executable, CancellationToken cancellationToken)
    {
        if (Fail is not null)
        {
            return Task.FromResult<string?>(Fail);
        }

        Installed = true;
        Executable = executable;
        return Task.FromResult<string?>(null);
    }

    public Task<string?> UninstallAsync(RtfcHome home, CancellationToken cancellationToken)
    {
        Installed = false;
        return Task.FromResult<string?>(null);
    }
}

/// <summary><c>rtfc daemon always-on</c> (spec §3.1): the setting, the login item, and what the daemon makes of them.</summary>
public class AlwaysOnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (int Code, string Out, string Error) Run(TempHome temp, FakeLoginItem item, List<string> ensured, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Commands.AlwaysOnAsync(new CommandContext(temp.Home, stdout, stderr, Ct), args, item, (home, _) =>
        {
            ensured.Add(home.Root);
            return Task.FromResult(true);
        }).GetAwaiter().GetResult();
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void On_sets_the_config_installs_the_login_item_and_starts_a_daemon_and_off_undoes_both()
    {
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        var item = new FakeLoginItem();
        var ensured = new List<string>();

        var status = Run(temp, item, ensured);
        Assert.Equal(0, status.Code);
        Assert.Contains("Session-bound", status.Out);
        Assert.Contains("Login item: not installed, a fake login item.", status.Out);

        var on = Run(temp, item, ensured, "on");
        Assert.Equal(0, on.Code);
        Assert.Contains("always on", on.Out);
        Assert.True(ConfigFile.Load(temp.Home).AlwaysOn);
        Assert.True(item.Installed);
        Assert.Equal(Environment.ProcessPath, item.Executable);
        Assert.Single(ensured);
        Assert.Contains("Always on", Run(temp, item, ensured).Out);

        var off = Run(temp, item, ensured, "off");
        Assert.Equal(0, off.Code);
        Assert.Contains("session-bound again", off.Out);
        Assert.Null(ConfigFile.Load(temp.Home).AlwaysOn);
        Assert.False(item.Installed);
        Assert.Equal(2, ensured.Count);
    }

    [Fact]
    public void A_login_item_that_fails_leaves_the_setting_as_it_was()
    {
        using var temp = new TempHome();
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        var item = new FakeLoginItem { Fail = "launchctl is not available" };
        var ensured = new List<string>();

        var on = Run(temp, item, ensured, "on");

        Assert.Equal(1, on.Code);
        Assert.Contains("launchctl is not available", on.Error);
        Assert.Null(ConfigFile.Load(temp.Home).AlwaysOn);
        Assert.Empty(ensured);
    }

    [Fact]
    public void Without_an_identity_or_with_a_bad_argument_nothing_changes()
    {
        using var temp = new TempHome();
        var item = new FakeLoginItem();
        var ensured = new List<string>();

        Assert.Equal(1, Run(temp, item, ensured, "on").Code);
        IdentityStore.Create(temp.Home, "alex", "desktop", DateTimeOffset.UtcNow).Dispose();
        Assert.Equal(2, Run(temp, item, ensured, "sometimes").Code);
        Assert.False(item.Installed);
        Assert.Empty(ensured);
    }

    [Fact]
    public async Task A_real_login_item_refuses_a_home_other_than_the_default()
    {
        using var temp = new TempHome();

        Assert.Contains("Unset RTFC_HOME first", LoginItems.RefuseOtherHomes(temp.Home));
        Assert.Null(LoginItems.RefuseOtherHomes(new RtfcHome(RtfcHome.DefaultRoot)));
        if (LoginItems.ForThisMachine() is { } real)
        {
            var refusal = await real.InstallAsync(temp.Home, "rtfc", Ct);
            Assert.Contains("Unset RTFC_HOME first", refusal);
        }
    }

    [Fact]
    public void The_daemon_stays_while_the_config_says_always_on_and_whenever_it_was_told_to_stay()
    {
        using var temp = new TempHome();
        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: null));
        Assert.True(DaemonHost.IdleExit(new DaemonOptions(IdleExit: true, LogToConsole: false), temp.Home));
        Assert.False(DaemonHost.IdleExit(new DaemonOptions(IdleExit: false, LogToConsole: false), temp.Home));

        ConfigFile.Save(temp.Home, new RtfcConfig(Port: 0, HintHosts: null, AlwaysOn: true));
        Assert.False(DaemonHost.IdleExit(new DaemonOptions(IdleExit: true, LogToConsole: false), temp.Home));

        File.WriteAllText(temp.Home.ConfigPath, "{ half written");
        Assert.False(DaemonHost.IdleExit(new DaemonOptions(IdleExit: true, LogToConsole: false), temp.Home));
    }

    [Theory]
    [InlineData("0.9.0", "0.9.1", true)]
    [InlineData("0.9.1", "0.9.1", false)]
    [InlineData("0.10.0", "0.9.1", false)]
    [InlineData("0.9.1", "0.10.0", true)]
    [InlineData("0.9.1-dev", "0.9.1", false)]
    [InlineData("0.9.0+abc123", "0.9.1+def456", true)]
    [InlineData("garbage", "0.9.1", false)]
    public void An_older_daemon_is_one_with_a_lower_release_and_nothing_unreadable_counts(string running, string mine, bool older) =>
        Assert.Equal(older, DaemonLauncher.IsOlder(running, mine));

    [Fact]
    public void The_login_items_say_what_they_run()
    {
        Assert.Equal(
            "\"C:\\Windows\\System32\\conhost.exe\" --headless \"C:\\Users\\A B\\rtfc\\rtfc.exe\" daemon ensure",
            LoginItems.WindowsCommand(@"C:\Windows\System32\conhost.exe", @"C:\Users\A B\rtfc\rtfc.exe"));

        var plist = LoginItems.LaunchAgentPlist("/Users/a&b/.local/share/rtfc/rtfc");
        Assert.Contains("<string>com.a7ex-turcan.rtfc</string>", plist);
        Assert.Contains("<string>/Users/a&amp;b/.local/share/rtfc/rtfc</string>", plist);
        Assert.Contains("<string>daemon</string>", plist);
        Assert.Contains("<key>SuccessfulExit</key>", plist);
        System.Xml.Linq.XDocument.Parse(plist[(plist.IndexOf("<plist", StringComparison.Ordinal))..]);

        var unit = LoginItems.SystemdUnit("/home/a b/.local/share/rtfc/rtfc");
        Assert.Contains("ExecStart=\"/home/a b/.local/share/rtfc/rtfc\" daemon run", unit);
        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains("WantedBy=default.target", unit);
    }
}
