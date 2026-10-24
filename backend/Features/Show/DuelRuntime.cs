using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Show;

internal sealed class DuelIdentitySource(KeepsakeStore keepsakes, EveningMemoryStore evenings) : IDuelIdentitySource
{
    public bool IsActiveEvening(string deviceId, string eveningId) => evenings.ReadActiveEveningId(deviceId) == eveningId;
    public bool IsOpenEvening(string deviceId, string eveningId) => IsActiveEvening(deviceId, eveningId)
        && evenings.ReadChronicle(deviceId, eveningId)?.Provisional == true;
    public DuelIdentitySnapshot? Read(string identityId, string eveningId, string deviceId)
    {
        var identity = keepsakes.GetPublicWizardIdentity(identityId, eveningId, deviceId);
        if (identity is null) { return null; }
        var snapshot = identity.GeneratedWand is { } wand
            ? DuelRules.CharacterSnapshot(identity.IdentityId, identity.WizardAlias, wand)
            : DuelRules.Snapshot(identity.IdentityId, identity.WizardAlias, identity.CatalogWandId);
        var artwork = keepsakes.PublicWandArtworkUrl(identityId, eveningId, deviceId, out var synthetic);
        return snapshot with { ArtworkUrl = artwork, ArtworkSynthetic = synthetic };
    }
}

internal sealed class DuelShowSlot(ShowCoordinator coordinator, ShowDeviceCueBridge bridge) : IDuelShowSlot
{
    public bool TryOccupy(string deviceId, string eveningId, string duelId) => coordinator.TryOccupy(deviceId, eveningId, duelId);
    public bool IsCurrent(string deviceId, string duelId) => coordinator.IsOccupationCurrent(deviceId, duelId);
    public void PublishRound(string deviceId, string duelId, int round, string quip) => coordinator.PublishDuelBeat(deviceId, duelId, round, quip.Length <= 400 ? quip : quip[..399] + "…");
    public void Release(string deviceId, string duelId) => coordinator.Release(deviceId, duelId);
    public DuelNarrationBinding BindNarration(string deviceId)
    {
        var reason = bridge.DeviceNarrationUnavailableReason(deviceId);
        return !string.IsNullOrEmpty(reason) ? new("web_only", reason)
            : bridge.HasRecentDevicePoll(deviceId) ? new("physical_device", null) : new("web_only", "device_unavailable");
    }
    public bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text)
    {
        var compartment = PartyWandCatalog.Available(63).FirstOrDefault(wand => wand.StableId == wandId)?.Compartment;
        return compartment is not null && bridge.EnqueueDuelLine(deviceId, duelId, round, scene, compartment.Value, text);
    }
    public string NarrationState(string deviceId, string duelId) => bridge.DuelNarrationState(deviceId, duelId);
}

internal sealed class DuelWorker(DuelStore duels, EveningMemoryStore memory, ILogger<DuelWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                duels.Tick();
                foreach (var completion in duels.PendingEvents())
                {
                    var recorded = memory.RecordShowEvent(new ShowMemoryEvent(
                        $"duel:{completion.Id}:completed", ShowMemoryEventKind.DuelCompleted,
                        completion.DeviceId, completion.EveningId, completion.Id, completion.Revision,
                        completion.Exhibition ? "exhibition_completed" : "duel_completed"));
                    if (recorded) { duels.AcknowledgeEvent(completion.Id); }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Keep tokens, identity content and database documents out of diagnostics.
                logger.LogWarning("Duel worker iteration failed ({ErrorType}).", exception.GetType().Name);
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

internal static class DuelRegistration
{
    public static IServiceCollection AddDuels(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddSingleton<IDuelIdentitySource, DuelIdentitySource>();
        services.AddSingleton<IDuelShowSlot, DuelShowSlot>();
        services.AddSingleton<DuelStore>();
        services.AddHostedService<DuelWorker>();
        services.AddSingleton<DuelCommentaryService>();
        services.AddHostedService<DuelCommentaryWorker>();
        return services;
    }
}
