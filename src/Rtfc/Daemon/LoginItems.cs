using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace Rtfc.Daemon;

/// <summary>
/// What starts the daemon when the user logs in, for <c>rtfc daemon always-on</c> (spec §3.1). One per operating system; the
/// command talks to this interface so tests never touch the real startup settings.
/// </summary>
public interface ILoginItem
{
    /// <summary>What it is, for the status line: "a startup entry in HKCU\…\Run", "a LaunchAgent at …".</summary>
    string Where { get; }

    bool IsInstalled();

    /// <summary>Registers the daemon to start at login and starts it now. Null on success, else why not.</summary>
    Task<string?> InstallAsync(RtfcHome home, string executable, CancellationToken cancellationToken);

    /// <summary>Removes the login item and whatever daemon it runs. Null on success, else why not.</summary>
    Task<string?> UninstallAsync(RtfcHome home, CancellationToken cancellationToken);
}

public static class LoginItems
{
    public const string Name = "rtfc";
    public const string LaunchAgentLabel = "com.a7ex-turcan.rtfc";

    /// <summary>This machine's login item, or null where rtfc does not know how to make one.</summary>
    public static ILoginItem? ForThisMachine()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRunKey();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LaunchAgent();
        }

        return OperatingSystem.IsLinux() ? new SystemdUserService() : null;
    }

    /// <summary>
    /// A login item starts the daemon for the default home only: it cannot carry <c>RTFC_HOME</c>, and a test or a manual run
    /// with its own home must never register itself to start at login.
    /// </summary>
    public static string? RefuseOtherHomes(RtfcHome home) =>
        string.Equals(Path.GetFullPath(home.Root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(RtfcHome.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? null
            : $"An always-on daemon serves the default home, {RtfcHome.DefaultRoot}, and this command runs with RTFC_HOME={home.Root}. Unset RTFC_HOME first.";

    /// <summary>
    /// The Windows startup command: <c>conhost --headless</c> runs the console program without a window, and <c>daemon ensure</c>
    /// starts the detached daemon the usual way and exits, so nothing stays behind but the daemon.
    /// </summary>
    public static string WindowsCommand(string conhost, string executable) => $"\"{conhost}\" --headless \"{executable}\" daemon ensure";

    /// <summary>The LaunchAgent: run at login, restarted if it crashes, not after a clean <c>rtfc daemon stop</c>.</summary>
    public static string LaunchAgentPlist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{LaunchAgentLabel}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(executable)}</string>
            <string>daemon</string>
            <string>run</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>KeepAlive</key>
          <dict>
            <key>SuccessfulExit</key>
            <false/>
          </dict>
          <key>ProcessType</key>
          <string>Background</string>
          <key>StandardOutPath</key>
          <string>/dev/null</string>
          <key>StandardErrorPath</key>
          <string>/dev/null</string>
        </dict>
        </plist>

        """;

    /// <summary>The systemd user unit: started with the user's session, restarted if it crashes, not after a clean stop.</summary>
    public static string SystemdUnit(string executable) => $"""
        [Unit]
        Description=rtfc daemon (Relay Tool For Contacts)
        After=network-online.target

        [Service]
        ExecStart="{executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}" daemon run
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target

        """;

    /// <summary>Runs a helper such as <c>launchctl</c> or <c>systemctl</c>. Null on success, else its complaint.</summary>
    internal static async Task<string?> RunAsync(string file, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return $"{file} did not start.";
            }

            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? null : $"{file} {string.Join(' ', arguments)} failed: {(await error.ConfigureAwait(false)).Trim()}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"{file} is not available: {ex.Message}";
        }
    }

    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run: per user, no administrator, listed in Task Manager's startup apps.</summary>
    private sealed class WindowsRunKey : ILoginItem
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string Where => $@"a startup entry named ""{Name}"" in HKCU\{RunKey}";

        public bool IsInstalled() => OperatingSystem.IsWindows() && Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is string;

        public Task<string?> InstallAsync(RtfcHome home, string executable, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Task.FromResult<string?>("Not Windows.");
            }

            if (RefuseOtherHomes(home) is { } refusal)
            {
                return Task.FromResult<string?>(refusal);
            }

            var conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            {
                key.SetValue(Name, WindowsCommand(conhost, executable));
            }

            // The startup entry takes over at the next login; the command starts this login's daemon itself.
            return Task.FromResult<string?>(null);
        }

        public Task<string?> UninstallAsync(RtfcHome home, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(Name, throwOnMissingValue: false);
            }

            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>~/Library/LaunchAgents: launchd starts the daemon at login and restarts it if it crashes.</summary>
    private sealed class LaunchAgent : ILoginItem
    {
        private static string PlistPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

        private static string Domain => $"gui/{getuid()}";

        public string Where => $"a LaunchAgent at {PlistPath}";

        public bool IsInstalled() => File.Exists(PlistPath);

        public async Task<string?> InstallAsync(RtfcHome home, string executable, CancellationToken cancellationToken)
        {
            if (RefuseOtherHomes(home) is { } refusal)
            {
                return refusal;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            await File.WriteAllTextAsync(PlistPath, LaunchAgentPlist(executable), cancellationToken).ConfigureAwait(false);
            _ = await RunAsync("/bin/launchctl", cancellationToken, "bootout", $"{Domain}/{LaunchAgentLabel}").ConfigureAwait(false);
            return await RunAsync("/bin/launchctl", cancellationToken, "bootstrap", Domain, PlistPath).ConfigureAwait(false);
        }

        public async Task<string?> UninstallAsync(RtfcHome home, CancellationToken cancellationToken)
        {
            _ = await RunAsync("/bin/launchctl", cancellationToken, "bootout", $"{Domain}/{LaunchAgentLabel}").ConfigureAwait(false);
            File.Delete(PlistPath);
            return null;
        }

        [DllImport("libc")]
        private static extern uint getuid();
    }

    /// <summary>~/.config/systemd/user: systemd starts the daemon with the user's session and restarts it if it crashes.</summary>
    private sealed class SystemdUserService : ILoginItem
    {
        private static string UnitPath => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "systemd", "user", Name + ".service");

        public string Where => $"a systemd user service at {UnitPath}";

        public bool IsInstalled() => File.Exists(UnitPath);

        public async Task<string?> InstallAsync(RtfcHome home, string executable, CancellationToken cancellationToken)
        {
            if (RefuseOtherHomes(home) is { } refusal)
            {
                return refusal;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
            await File.WriteAllTextAsync(UnitPath, SystemdUnit(executable), cancellationToken).ConfigureAwait(false);
            return await RunAsync("systemctl", cancellationToken, "--user", "daemon-reload").ConfigureAwait(false)
                ?? await RunAsync("systemctl", cancellationToken, "--user", "enable", "--now", Name + ".service").ConfigureAwait(false);
        }

        public async Task<string?> UninstallAsync(RtfcHome home, CancellationToken cancellationToken)
        {
            _ = await RunAsync("systemctl", cancellationToken, "--user", "disable", "--now", Name + ".service").ConfigureAwait(false);
            File.Delete(UnitPath);
            _ = await RunAsync("systemctl", cancellationToken, "--user", "daemon-reload").ConfigureAwait(false);
            return null;
        }
    }
}
