using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rtfc.Identity;

/// <summary>
/// Identity is keys (spec §4, §15). A person is the SHA-256 of their CA's
/// SubjectPublicKeyInfo, a device the SHA-256 of its certificate's. Both are derived from
/// the public key alone, so anyone holding the certificate can compute them and nothing
/// has to be trusted about the certificate's subject fields.
/// </summary>
public static class Ids
{
    public const string PersonPrefix = "p_";
    public const string DevicePrefix = "d_";

    public static string Person(X509Certificate2 personCa) => PersonPrefix + SpkiHash(personCa);

    public static string Device(X509Certificate2 deviceCertificate) => DevicePrefix + SpkiHash(deviceCertificate);

    public static bool IsPerson(string id) => id.StartsWith(PersonPrefix, StringComparison.Ordinal) && IsHash(id.AsSpan(PersonPrefix.Length));

    public static bool IsDevice(string id) => id.StartsWith(DevicePrefix, StringComparison.Ordinal) && IsHash(id.AsSpan(DevicePrefix.Length));

    /// <summary>
    /// A short rendering for people to compare out loud or on a screen: the first 16 hex
    /// digits of the hash in groups of four. Spec §5.1 asks for fingerprint words; this is
    /// the placeholder until a word list exists.
    /// </summary>
    public static string Fingerprint(string id)
    {
        var hash = id.AsSpan(id.IndexOf('_') + 1);
        return $"{hash[..4]} {hash[4..8]} {hash[8..12]} {hash[12..16]}";
    }

    private static string SpkiHash(X509Certificate2 certificate)
    {
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(spki));
    }

    private static bool IsHash(ReadOnlySpan<char> value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
