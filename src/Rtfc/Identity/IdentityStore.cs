using System.Security.Cryptography.X509Certificates;

namespace Rtfc.Identity;

/// <summary>
/// Keeps the private keys as PKCS#12 files under <c>keys/</c>, readable only by the user,
/// and never anywhere else (spec §4).
/// </summary>
/// <remarks>
/// Certificates are always handed out freshly loaded from those files, even right after
/// creation. <c>SslStream</c> on macOS and Windows needs private keys the OS can see; a
/// key that only exists as an in-memory object (which is what <c>CreateSelfSigned</c> and
/// <c>CopyWithPrivateKey</c> produce) fails the TLS handshake there. Loading a PKCS#12
/// with the default key set imports the key where the platform wants it.
/// </remarks>
public static class IdentityStore
{
    // The files are protected by their permissions, not by this. An empty password keeps
    // `rtfc init` non-interactive; the OS keychain is the later answer (spec §4).
    private const string Password = "";

    private static readonly Pkcs12ExportPbeParameters ExportParameters = Pkcs12ExportPbeParameters.Pbes2Aes256Sha256;

    public static bool Exists(RtfcHome home) => File.Exists(home.PersonKeyPath) && File.Exists(home.DeviceKeyPath);

    /// <summary>Creates a new person and their first device. Refuses to overwrite an identity that exists.</summary>
    public static SelfIdentity Create(RtfcHome home, string handle, string deviceName, DateTimeOffset now)
    {
        if (File.Exists(home.PersonKeyPath) || File.Exists(home.DeviceKeyPath))
        {
            throw new InvalidOperationException($"An identity already exists in {home.KeysDirectory}.");
        }

        home.EnsureCreated();
        using var personCa = Certificates.CreatePersonCa(handle, now);
        using var device = Certificates.CreateDevice(personCa, deviceName, now);

        RtfcHome.WritePrivateFile(home.PersonKeyPath, personCa.ExportPkcs12(ExportParameters, Password));
        RtfcHome.WritePrivateFile(home.DeviceKeyPath, device.ExportPkcs12(ExportParameters, Password));

        return Load(home);
    }

    public static SelfIdentity Load(RtfcHome home)
    {
        if (!Exists(home))
        {
            throw new InvalidOperationException($"No identity in {home.KeysDirectory}. Run `rtfc init` first.");
        }

        var personCa = LoadPkcs12(home.PersonKeyPath);
        try
        {
            return new SelfIdentity(personCa, LoadPkcs12(home.DeviceKeyPath));
        }
        catch
        {
            personCa.Dispose();
            throw;
        }
    }

    private static X509Certificate2 LoadPkcs12(string path) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, Password, X509KeyStorageFlags.DefaultKeySet);
}
