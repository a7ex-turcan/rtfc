namespace Rtfc.Core;

/// <summary>
/// Project roots and the keys they are stored under (spec §10.2). A session's directory
/// resolves to the git root that encloses it, else to itself. The key is the full path
/// normalized so that the daemon and the status line compare the same strings.
/// </summary>
public static class ProjectPaths
{
    /// <summary>The nearest directory, from <paramref name="directory"/> upwards, that holds a <c>.git</c> directory or file; else <paramref name="directory"/> itself.</summary>
    public static string Root(string directory)
    {
        var full = Trim(Path.GetFullPath(directory));
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return Trim(dir.FullName);
            }
        }

        return full;
    }

    /// <summary>The full path without a trailing separator, in its original case.</summary>
    public static string Full(string path) => Trim(Path.GetFullPath(path));

    /// <summary>The folder name a contact addresses the project by.</summary>
    public static string Name(string root) => Path.GetFileName(Trim(root)) is { Length: > 0 } name ? name : root;

    /// <summary>The full path without a trailing separator, in lower case on Windows, where paths are case-insensitive.</summary>
    public static string Key(string path)
    {
        var full = Trim(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>Whether the path with key <paramref name="pathKey"/> is the project with key <paramref name="rootKey"/> or lies inside it.</summary>
    public static bool Contains(string rootKey, string pathKey) =>
        pathKey == rootKey
        || (pathKey.StartsWith(rootKey, StringComparison.Ordinal)
            && (rootKey.EndsWith(Path.DirectorySeparatorChar) || pathKey[rootKey.Length] == Path.DirectorySeparatorChar));

    private static string Trim(string path) =>
        path.Length > (Path.GetPathRoot(path)?.Length ?? 0) ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
