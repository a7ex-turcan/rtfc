using System.Globalization;

namespace Rtfc;

/// <summary>
/// One timestamp format for everything a program parses: ISO 8601, UTC, millisecond
/// precision, invariant culture (AGENTS.md). Timestamps are informational only; nothing
/// orders or expires by comparing clocks across machines (spec §15, invariant 6).
/// </summary>
public static class Timestamps
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseOrNull(string? value) => value is null ? null : Parse(value);
}
