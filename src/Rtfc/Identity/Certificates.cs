using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rtfc.Identity;

/// <summary>
/// Creates and checks the two kinds of certificate rtfc uses (spec §4): a self-signed
/// person CA and device leaves issued by it. ECDSA P-256 throughout, because that is what
/// every OS's native TLS stack accepts; nothing here needs third-party crypto.
/// </summary>
public static class Certificates
{
    private const string Organization = "rtfc";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Validity starts a day in the past. Nothing in rtfc depends on clocks agreeing
    /// (spec §15, invariant 6), but X.509 validity windows are checked against the local
    /// clock, so a peer whose clock runs behind would otherwise reject a fresh certificate.
    /// </summary>
    private static readonly TimeSpan Backdate = TimeSpan.FromDays(1);

    public static readonly TimeSpan PersonValidity = TimeSpan.FromDays(365 * 20);
    public static readonly TimeSpan DeviceValidity = TimeSpan.FromDays(365 * 2);

    /// <summary>The person CA: self-signed, <c>CA:TRUE, pathLen 0</c>. The returned certificate holds its private key.</summary>
    public static X509Certificate2 CreatePersonCa(string handle, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName(handle), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(now - Backdate, now + PersonValidity);
    }

    /// <summary>A device leaf issued by the person CA, usable for both TLS roles. The returned certificate holds its private key.</summary>
    public static X509Certificate2 CreateDevice(X509Certificate2 personCa, string deviceName, DateTimeOffset now)
    {
        if (!personCa.HasPrivateKey)
        {
            throw new ArgumentException("The person CA must hold its private key to issue a device certificate.", nameof(personCa));
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName(deviceName), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ServerAuthOid), new Oid(ClientAuthOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            personCa, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f; // keep the serial positive

        using var issued = request.Create(personCa, now - Backdate, now + DeviceValidity, serial);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// Whether <paramref name="leaf"/> was issued by <paramref name="personCa"/>. Builds a
    /// chain with the CA as the only trust anchor and never consults the OS trust store,
    /// which is the rule for every check rtfc makes (spec §8.2).
    /// </summary>
    public static bool IsIssuedBy(X509Certificate2 leaf, X509Certificate2 personCa) =>
        FindIssuer(leaf, [personCa]) is not null;

    /// <summary>The CA among <paramref name="personCas"/> that issued <paramref name="leaf"/>, or null.</summary>
    public static X509Certificate2? FindIssuer(X509Certificate2 leaf, IReadOnlyCollection<X509Certificate2> personCas)
    {
        if (personCas.Count == 0)
        {
            return null;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        foreach (var ca in personCas)
        {
            chain.ChainPolicy.CustomTrustStore.Add(ca);
        }

        if (!chain.Build(leaf))
        {
            return null;
        }

        var root = chain.ChainElements[^1].Certificate;
        return personCas.FirstOrDefault(ca => ca.RawData.AsSpan().SequenceEqual(root.RawData));
    }

    /// <summary>The CN of the subject: the handle for a person CA, the device name for a leaf.</summary>
    public static string CommonName(X509Certificate2 certificate) =>
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    private static X500DistinguishedName SubjectName(string commonName)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        builder.AddOrganizationName(Organization);
        return builder.Build();
    }
}
