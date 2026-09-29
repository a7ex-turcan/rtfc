namespace Rtfc;

/// <summary>
/// The one root every path rtfc touches derives from. Defaults to <c>~/.claude/rtfc</c>
/// and is overridden by the <c>RTFC_HOME</c> environment variable, which is how tests and
/// manual runs stay away from the real one (AGENTS.md rule 6).
/// </summary>
public sealed class RtfcHome
{
    public const string EnvironmentVariable = "RTFC_HOME";

    public RtfcHome(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string KeysDirectory => Path.Combine(Root, "keys");
    public string PersonKeyPath => Path.Combine(KeysDirectory, "person.p12");
    public string DeviceKeyPath => Path.Combine(KeysDirectory, "device.p12");
    public string DatabasePath => Path.Combine(Root, "rtfc.db");
    public string ConfigPath => Path.Combine(Root, "config.json");
    public string SocketPath => Path.Combine(Root, "rtfcd.sock");
    public string StatusPath => Path.Combine(Root, "status.json");
    public string LogPath => Path.Combine(Root, "rtfcd.log");

    /// <summary>One small file per Claude Code session: the accept gate for a contact's message pushed into it (spec §7.3).</summary>
    public string GatesDirectory => Path.Combine(Root, "gates");

    public static RtfcHome Resolve()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new RtfcHome(env);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new RtfcHome(Path.Combine(profile, ".claude", "rtfc"));
    }

    /// <summary>Creates the root and the keys directory, private to the user.</summary>
    public void EnsureCreated()
    {
        CreatePrivateDirectory(Root);
        CreatePrivateDirectory(KeysDirectory);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Writes a file only the user can read. On Windows the user-profile ACL already does
    /// this, on Unix the mode is set when the file is created rather than after, so there
    /// is no window where it is world-readable.
    /// </summary>
    public static void WritePrivateFile(string path, ReadOnlySpan<byte> content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(content);
    }
}
