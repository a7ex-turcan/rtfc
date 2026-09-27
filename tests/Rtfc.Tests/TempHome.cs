namespace Rtfc.Tests;

/// <summary>
/// A throwaway <see cref="RtfcHome"/> under the temp directory, so no test can touch the
/// real <c>~/.claude/rtfc</c> (AGENTS.md rule 6). The name is kept short because a Unix
/// domain socket path is limited to about a hundred characters on macOS.
/// </summary>
public sealed class TempHome : IDisposable
{
    public TempHome()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtfc-t", Guid.NewGuid().ToString("N")[..8]);
        Home = new RtfcHome(root);
        Home.EnsureCreated();
    }

    public RtfcHome Home { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Home.Root, recursive: true);
        }
        catch (IOException)
        {
            // A daemon still shutting down may hold the socket or the database for a moment.
        }
    }
}
