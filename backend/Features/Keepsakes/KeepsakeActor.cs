namespace Kufr.Backend.Features.Keepsakes;

// A keepsake operation is authorized either by its existing guest session or by
// a live account session. Account credentials never enter the guest-session store.
internal sealed class KeepsakeActor
{
    private KeepsakeActor(string guestSession)
    {
        GuestSession = guestSession;
        StableIdentity = guestSession;
    }

    private KeepsakeActor(string accountId, string accountSessionId, Func<bool> validateAccountSession)
    {
        AccountId = accountId;
        AccountSessionId = accountSessionId;
        ValidateAccountSession = validateAccountSession;
        StableIdentity = "account-owner:" + accountId + ":" + accountSessionId;
    }

    internal string? GuestSession { get; }
    internal string? AccountId { get; }
    internal string? AccountSessionId { get; }
    internal Func<bool>? ValidateAccountSession { get; }
    internal string StableIdentity { get; }
    internal bool IsAccount => AccountId is not null;

    internal static KeepsakeActor ForAccount(string accountId, string accountSessionId, Func<bool> validateAccountSession) =>
        new(accountId, accountSessionId, validateAccountSession);

    public static implicit operator KeepsakeActor(string guestSession) => new(guestSession);
}
