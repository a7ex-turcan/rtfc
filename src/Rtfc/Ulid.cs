using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rtfc;

/// <summary>
/// ULIDs for message ids (spec §7.1): 48 bits of milliseconds, 80 bits of randomness,
/// 26 characters of Crockford base32. Sortable by creation time on one machine, which
/// is a convenience only; ordering between machines uses <c>seq</c>.
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 26;

    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    public static string NewUlid(DateTimeOffset now)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        Span<char> chars = stackalloc char[Length];
        var time = now.ToUnixTimeMilliseconds();
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(time & 31)];
            time >>= 5;
        }

        var entropy = ((UInt128)BinaryPrimitives.ReadUInt16BigEndian(random[..2]) << 64)
            | BinaryPrimitives.ReadUInt64BigEndian(random[2..]);
        for (var i = Length - 1; i >= 10; i--)
        {
            chars[i] = Alphabet[(int)(entropy & 31)];
            entropy >>= 5;
        }

        return new string(chars);
    }

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length || value[0] > '7')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
