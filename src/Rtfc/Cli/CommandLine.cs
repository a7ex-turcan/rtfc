namespace Rtfc.Cli;

/// <summary>
/// Hand-rolled argument parsing, as in rtfm and rtfq: <c>--key value</c>, <c>--key=value</c>,
/// bare <c>--flag</c>, and positionals. Small enough to need no library.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public CommandLine(IReadOnlyList<string> args, params string[] flagNames)
    {
        var flags = new HashSet<string>(flagNames, StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                positionals.Add(arg);
                continue;
            }

            var equals = arg.IndexOf('=');
            if (equals > 0)
            {
                Add(arg[2..equals], arg[(equals + 1)..]);
            }
            else if (flags.Contains(arg[2..]))
            {
                _flags.Add(arg[2..]);
            }
            else if (i + 1 < args.Count)
            {
                Add(arg[2..], args[++i]);
            }
            else
            {
                throw new CommandLineException($"'{arg}' needs a value.");
            }
        }

        Positionals = positionals;
    }

    public IReadOnlyList<string> Positionals { get; }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Values(string name) => _options.TryGetValue(name, out var values) ? values : [];

    public int? IntValue(string name)
    {
        var value = Value(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new CommandLineException($"'--{name}' must be a number, not '{value}'.");
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            _options[name] = values = [];
        }

        values.Add(value);
    }
}

public sealed class CommandLineException(string message) : Exception(message);
