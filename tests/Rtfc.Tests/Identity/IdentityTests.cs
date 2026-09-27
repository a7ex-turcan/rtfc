using System.Security.Cryptography.X509Certificates;
using Rtfc.Identity;

namespace Rtfc.Tests.Identity;

public class IdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_person_ca_is_a_self_signed_ca_with_path_length_zero()
    {
        using var ca = Certificates.CreatePersonCa("alex", Now);

        Assert.Equal(ca.Subject, ca.Issuer);
        Assert.True(ca.HasPrivateKey);
        Assert.Equal("alex", Certificates.CommonName(ca));

        var constraints = Assert.Single(ca.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);
    }

    [Fact]
    public void A_device_certificate_chains_to_its_person_ca_and_to_nobody_else()
    {
        using var alex = Certificates.CreatePersonCa("alex", Now);
        using var sasha = Certificates.CreatePersonCa("sasha", Now);
        using var laptop = Certificates.CreateDevice(alex, "laptop", Now);

        Assert.True(Certificates.IsIssuedBy(laptop, alex));
        Assert.False(Certificates.IsIssuedBy(laptop, sasha));
        Assert.Same(alex, Certificates.FindIssuer(laptop, [sasha, alex]));
        Assert.Null(Certificates.FindIssuer(laptop, []));
        Assert.Equal("laptop", Certificates.CommonName(laptop));
    }

    [Fact]
    public void Ids_derive_from_the_public_key_alone()
    {
        using var ca = Certificates.CreatePersonCa("alex", Now);
        using var device = Certificates.CreateDevice(ca, "laptop", Now);

        var personId = Ids.Person(ca);
        var deviceId = Ids.Device(device);

        Assert.True(Ids.IsPerson(personId));
        Assert.True(Ids.IsDevice(deviceId));
        Assert.False(Ids.IsPerson(deviceId));
        Assert.Equal(66, personId.Length);

        // The same key in a certificate without its private half has the same id.
        using var publicOnly = X509CertificateLoader.LoadCertificate(ca.RawData);
        Assert.Equal(personId, Ids.Person(publicOnly));

        Assert.Matches("^[0-9a-f]{4}( [0-9a-f]{4}){3}$", Ids.Fingerprint(personId));
    }

    [Fact]
    public void Two_people_never_share_an_id()
    {
        using var a = Certificates.CreatePersonCa("alex", Now);
        using var b = Certificates.CreatePersonCa("alex", Now);

        Assert.NotEqual(Ids.Person(a), Ids.Person(b));
    }

    [Fact]
    public void A_backdated_certificate_is_valid_for_a_peer_whose_clock_runs_behind()
    {
        using var ca = Certificates.CreatePersonCa("alex", Now);
        using var device = Certificates.CreateDevice(ca, "laptop", Now);

        Assert.True(device.NotBefore <= (Now - TimeSpan.FromHours(12)).UtcDateTime);
        Assert.True(ca.NotBefore <= (Now - TimeSpan.FromHours(12)).UtcDateTime);
    }

    [Fact]
    public void The_store_creates_once_then_loads_the_same_identity()
    {
        using var temp = new TempHome();

        Assert.False(IdentityStore.Exists(temp.Home));
        using var created = IdentityStore.Create(temp.Home, "alex", "laptop", DateTimeOffset.UtcNow);
        Assert.True(IdentityStore.Exists(temp.Home));

        using var loaded = IdentityStore.Load(temp.Home);
        Assert.Equal(created.PersonId, loaded.PersonId);
        Assert.Equal(created.DeviceId, loaded.DeviceId);
        Assert.Equal("alex", loaded.Handle);
        Assert.Equal("laptop", loaded.DeviceName);
        Assert.True(loaded.PersonCa.HasPrivateKey);
        Assert.True(loaded.DeviceCertificate.HasPrivateKey);

        Assert.Throws<InvalidOperationException>(() => IdentityStore.Create(temp.Home, "alex", "laptop", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Key_files_are_readable_only_by_the_user()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the user-profile ACL covers this on Windows
        }

        using var temp = new TempHome();
        using var _ = IdentityStore.Create(temp.Home, "alex", "laptop", DateTimeOffset.UtcNow);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.Home.PersonKeyPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.Home.DeviceKeyPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(temp.Home.KeysDirectory));
    }

    [Fact]
    public void A_loaded_person_ca_can_still_issue_devices()
    {
        using var temp = new TempHome();
        using var _ = IdentityStore.Create(temp.Home, "alex", "laptop", DateTimeOffset.UtcNow);
        using var loaded = IdentityStore.Load(temp.Home);

        using var desktop = Certificates.CreateDevice(loaded.PersonCa, "desktop", DateTimeOffset.UtcNow);

        Assert.True(Certificates.IsIssuedBy(desktop, loaded.PersonCa));
        Assert.NotEqual(loaded.DeviceId, Ids.Device(desktop));
    }

    [Fact]
    public void Home_resolves_from_the_environment_variable_first()
    {
        var previous = Environment.GetEnvironmentVariable(RtfcHome.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(RtfcHome.EnvironmentVariable, Path.Combine(Path.GetTempPath(), "rtfc-env"));
            Assert.EndsWith("rtfc-env", RtfcHome.Resolve().Root);

            Environment.SetEnvironmentVariable(RtfcHome.EnvironmentVariable, null);
            Assert.EndsWith(Path.Combine(".claude", "rtfc"), RtfcHome.Resolve().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RtfcHome.EnvironmentVariable, previous);
        }
    }
}
