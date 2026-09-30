using System.Text;

namespace Rtfc.Core.Sources;

/// <summary>
/// Tokens for third-party accounts (spec §10.5): one private file per account under <c>keys/accounts/</c>, written by
/// <c>rtfc account add</c> in a real terminal and read only by the daemon when it polls. The token is never a database
/// column, a log line, a tool result or a command-line argument.
/// </summary>
public static class AccountStore
{
    public const int MaxNameLength = 64;

    /// <summary>Account names are file names and JSON keys: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, not starting with a dot.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static string TokenPath(RtfcHome home, string name) => Path.Combine(home.AccountsDirectory, name + ".token");

    public static void SaveToken(RtfcHome home, string name, string token)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid account name.", nameof(name));
        }

        home.EnsureCreated();
        RtfcHome.WritePrivateFile(TokenPath(home, name), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Null when there is no token for the account, which the poller reports as the subscription's error.</summary>
    public static string? LoadToken(RtfcHome home, string name)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        var path = TokenPath(home, name);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : null;
    }

    public static bool DeleteToken(RtfcHome home, string name)
    {
        if (!IsValidName(name))
        {
            return false;
        }

        var path = TokenPath(home, name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
