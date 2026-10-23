using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Kufr.Backend.Features.Customers;

internal sealed record CustomerWaitingJoin(string RequestId, string EventId, string ProfileId);
internal sealed record CustomerWaitingEntryCommand(string RequestId, string EventId, string EntryId, long ExpectedRevision);
internal sealed record CustomerWaitingProximity(string BootId, string Challenge);
internal sealed record CustomerWaitingEvent(string Id, string DeviceId, string EveningId, string State, long Revision,
    long? ActivationRevision = null);
internal sealed record CustomerWaitingClaim(string Id, long ExpiresAt);
internal sealed record CustomerWaitingEntry(string Id, string ProfileId, long Sequence, long Revision, string State,
    CustomerWaitingClaim? Claim);
internal sealed record CustomerWaitingView(CustomerWaitingEvent? Event, CustomerWaitingEntry? Entry, int? Position, int Count);
internal sealed record CustomerWaitingHead(string EntryId, long Revision, string State);
internal sealed record CustomerWaitingOperatorView(CustomerWaitingEvent? Event, CustomerWaitingHead? Head, int Count);

// These optional bindings belong to the new waiting API, never the existing ticket/Create DTOs.
internal sealed record CustomerWaitingClaimBinding(string EventId, string EntryId, string ClaimId);

internal sealed record CustomerWaitingLimits(int EventsPerDevice, int EntriesPerEvent, int ReceiptsPerDevice,
    int ReceiptsPerAccountDevice)
{
    internal const string ConfigurationPrefix = "Backend:CustomerWaiting:";
    internal static CustomerWaitingLimits Defaults { get; } = new(4096, 256, 131072, 1024);

    internal static CustomerWaitingLimits Read(IConfiguration configuration)
    {
        var limits = new CustomerWaitingLimits(
            ReadLimit(configuration, nameof(EventsPerDevice), Defaults.EventsPerDevice, 65536),
            ReadLimit(configuration, nameof(EntriesPerEvent), Defaults.EntriesPerEvent, 8192),
            ReadLimit(configuration, nameof(ReceiptsPerDevice), Defaults.ReceiptsPerDevice, 1048576),
            ReadLimit(configuration, nameof(ReceiptsPerAccountDevice), Defaults.ReceiptsPerAccountDevice, 65536));
        if (limits.ReceiptsPerAccountDevice > limits.ReceiptsPerDevice)
        {
            throw new InvalidOperationException(ConfigurationPrefix + nameof(ReceiptsPerAccountDevice)
                + " must not exceed " + ConfigurationPrefix + nameof(ReceiptsPerDevice) + ".");
        }
        return limits;
    }

    private static int ReadLimit(IConfiguration configuration, string name, int fallback, int maximum)
    {
        var key = ConfigurationPrefix + name;
        var configured = configuration[key];
        if (configured is null) { return fallback; }
        if (!int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1 || value > maximum)
        {
            throw new InvalidOperationException(key + " must be an integer between 1 and "
                + maximum.ToString(CultureInfo.InvariantCulture) + ".");
        }
        return value;
    }
}
