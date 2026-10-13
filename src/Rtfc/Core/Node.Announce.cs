using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Rtfc.Net;
using Rtfc.Protocol;
using Rtfc.Storage;

namespace Rtfc.Core;

/// <summary>
/// Telling contacts where to reach us (spec §8.4). They learn our hints from the hello of every session, but a contact who cannot
/// reach us never opens one: once a DHCP lease moves this machine, their sends go to the old address and they are told nobody is
/// home. So whenever a contact's device last heard something other than what we advertise now, the pump opens a session that says
/// hello and bye, which every version understands. A device that does not answer is tried again after <see cref="AnnounceRetry"/>,
/// or hears it the moment it connects to us, from our hello.
/// </summary>
public sealed partial class Node
{
    private static readonly TimeSpan AnnounceRetry = TimeSpan.FromMinutes(5);

    /// <summary>The last attempt per device, and for which hints; only the pump touches it.</summary>
    private readonly Dictionary<string, (string Hints, DateTimeOffset At)> _announced = new(StringComparer.Ordinal);

    private async Task AnnounceHintsAsync(CancellationToken cancellationToken)
    {
        if (IsAway)
        {
            return; // nothing listens, so there is nowhere to send them
        }

        var hints = AdvertisedHints();
        var key = string.Join(' ', hints.Order(StringComparer.OrdinalIgnoreCase));
        var now = _clock.GetUtcNow();
        foreach (var contact in _db.ListContacts().Where(c => c.Status == ContactStatus.Active))
        {
            var unaware = _db.ListDevices(contact.PersonId).Where(d => d.Status == DeviceStatus.Active && !HasHeard(d, hints)).ToList();
            if (unaware.Count == 0)
            {
                continue;
            }

            using var ca = X509CertificateLoader.LoadCertificate(contact.PersonCaCert);
            foreach (var device in unaware)
            {
                if (_announced.TryGetValue(device.DeviceId, out var last) && last.Hints == key && now - last.At < AnnounceRetry)
                {
                    continue;
                }

                _announced[device.DeviceId] = (key, now);
                var session = await OpenSessionAsync(contact, ca, device, cancellationToken).ConfigureAwait(false);
                if (session is null)
                {
                    _logger.LogDebug("Could not reach {Handle}/{Device} to tell it our hints; trying again later", contact.Handle, device.Name);
                    continue;
                }

                await using (session)
                {
                    try
                    {
                        await session.SendAsync(new ByeFrame(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ProtocolException)
                    {
                        // The hello already carried the hints; a bye that does not arrive changes nothing.
                    }
                }

                _logger.LogInformation("Told {Handle}/{Device} where to reach us: {Hints}", contact.Handle, device.Name, string.Join(", ", hints));
            }
        }
    }

    private static bool HasHeard(DeviceRow device, string[] hints) =>
        device.ToldHints is { } told && told.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(hints);
}
