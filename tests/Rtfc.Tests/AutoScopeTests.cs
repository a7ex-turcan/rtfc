using Rtfc.Cli;

namespace Rtfc.Tests;

/// <summary>The scope <c>rtfc auto … headless</c> falls back to without <c>--scope</c> (spec §7.3), and the directories it refuses.</summary>
public class AutoScopeTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "rtfc-scope", "home", "alex");
    private static readonly string Project = Path.Combine(Home, "src", "payments-api");

    [Fact]
    public void The_session_directory_wins_over_the_working_directory()
    {
        Assert.Equal(Project, AutoScope.Default(Project, Path.Combine(Home, "src"), Home, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(Project, AutoScope.Default(null, Project, Home, out _));
        Assert.Equal(Project, AutoScope.Default("", Project + Path.DirectorySeparatorChar, Home, out _));
    }

    [Fact]
    public void A_folder_under_home_is_fine_but_home_and_above_are_not()
    {
        Assert.NotNull(AutoScope.Default(null, Path.Combine(Home, "notes"), Home, out _));

        Assert.Null(AutoScope.Default(null, Home, Home, out var atHome));
        Assert.Contains("home folder", atHome);
        Assert.Null(AutoScope.Default(null, Path.GetDirectoryName(Home)!, Home, out var aboveHome));
        Assert.Contains("home folder", aboveHome);
        Assert.Contains("--scope", aboveHome);
    }

    [Fact]
    public void A_drive_root_and_anything_in_dot_claude_are_refused()
    {
        var otherRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Null(AutoScope.Default(null, otherRoot, Path.Combine(otherRoot, "Users", "alex"), out var root));
        Assert.NotNull(root);

        Assert.Null(AutoScope.Default(null, Path.Combine(Home, ".claude", "rtfc"), Home, out var claude));
        Assert.Contains("~/.claude", claude);
        Assert.NotNull(AutoScope.Default(null, Path.Combine(Home, ".claude-notes"), Home, out _));
    }
}
