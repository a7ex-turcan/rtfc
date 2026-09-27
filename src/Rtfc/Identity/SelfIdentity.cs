using System.Security.Cryptography.X509Certificates;

namespace Rtfc.Identity;

/// <summary>This device's view of who it is: the person it belongs to and its own leaf certificate, both with private keys.</summary>
public sealed class SelfIdentity : IDisposable
{
    public SelfIdentity(X509Certificate2 personCa, X509Certificate2 deviceCertificate)
    {
        if (!personCa.HasPrivateKey || !deviceCertificate.HasPrivateKey)
        {
            throw new ArgumentException("Both certificates must hold their private keys.");
        }

        if (!Certificates.IsIssuedBy(deviceCertificate, personCa))
        {
            throw new ArgumentException("The device certificate was not issued by this person CA.");
        }

        PersonCa = personCa;
        DeviceCertificate = deviceCertificate;
        PersonId = Ids.Person(personCa);
        DeviceId = Ids.Device(deviceCertificate);
        Handle = Certificates.CommonName(personCa);
        DeviceName = Certificates.CommonName(deviceCertificate);
    }

    public X509Certificate2 PersonCa { get; }
    public X509Certificate2 DeviceCertificate { get; }
    public string PersonId { get; }
    public string DeviceId { get; }
    public string Handle { get; }
    public string DeviceName { get; }

    public void Dispose()
    {
        DeviceCertificate.Dispose();
        PersonCa.Dispose();
    }
}
