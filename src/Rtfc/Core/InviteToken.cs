using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rtfc.Core;

/// <summary>
/// <c>rtfc1_</c> + base64url(JSON). Exchanged out of band over any chat, single-use, and
/// judged expired only by the issuer, so no two clocks have to agree (spec §5.1).
/// </summary>
public static class InviteToken
{
    public const string Prefix = "rtfc1_";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    public static string Encode(InviteTokenPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, CoreJson.Default.InviteTokenPayload);
        return Prefix + Base64Url.EncodeToString(json);
    }

    public static bool TryDecode(string token, out InviteTokenPayload payload)
    {
        payload = null!;
        token = token.Trim();
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var json = Base64Url.DecodeFromChars(token.AsSpan(Prefix.Length));
            var decoded = JsonSerializer.Deserialize(json, CoreJson.Default.InviteTokenPayload);
            if (decoded is null || decoded.V != 1 || !Identity.Ids.IsPerson(decoded.Person) || string.IsNullOrEmpty(decoded.Nonce))
            {
                return false;
            }

            payload = decoded with { Hints = decoded.Hints ?? [] };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The handle a peer suggests for itself is a hint for the local petname, never trusted as-is.</summary>
    public static string SanitizeHandle(string? suggested, string personId)
    {
        var builder = new StringBuilder();
        foreach (var c in (suggested ?? "").Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                builder.Append(c);
            }
            else if (char.IsWhiteSpace(c) && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length == 32)
            {
                break;
            }
        }

        var handle = builder.ToString().TrimEnd('-');
        return handle.Length > 0 ? handle : personId[Identity.Ids.PersonPrefix.Length..][..8];
    }
}
