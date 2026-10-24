namespace Kufr.Backend.Features.Show;

internal interface IDuelIdentitySource
{
    // Closing admission preserves public results until the active evening rotates.
    bool IsActiveEvening(string deviceId, string eveningId);
    bool IsOpenEvening(string deviceId, string eveningId);
    DuelIdentitySnapshot? Read(string identityId, string eveningId, string deviceId);
}

internal interface IDuelShowSlot
{
    bool TryOccupy(string deviceId, string eveningId, string duelId);
    bool IsCurrent(string deviceId, string duelId);
    void PublishRound(string deviceId, string duelId, int round, string quip);
    void Release(string deviceId, string duelId);
    DuelNarrationBinding BindNarration(string deviceId);
    bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text);
    string NarrationState(string deviceId, string duelId);
}

internal sealed record DuelNarrationBinding(string Mode, string? UnavailableReason);

internal sealed record DuelCreateRequest(string EveningId, string LeftIdentityId, string RightIdentityId,
    string Mode, bool ConsentConfirmed, string OperationKey);
internal sealed record DuelCommand(int Revision, string OperationKey);
internal sealed record DuelGrantRequest(string Side, int Revision, string OperationKey);
internal sealed record DuelChoiceRequest(string Side, int Round, int Revision, string Action, string OperationKey);
internal sealed record DuelJoinRequest(string Side, bool ConsentConfirmed, string OperationKey);
internal sealed record DuelGrant(string Token, string Side, DateTimeOffset ExpiresAt, DuelView Duel);
internal sealed record DuelView(string Id, string DeviceId, string EveningId, string RulesVersion, string Mode,
    int Revision, string State, DuelIdentitySnapshot Left, DuelIdentitySnapshot Right,
    int Round, DateTimeOffset? Deadline, bool LeftChoiceLocked, bool RightChoiceLocked,
    IReadOnlyList<DuelRound> Rounds, DuelResult? Result, string? Seed, DuelCommentary? Commentary = null,
    string NarrationMode = "unbound", string NarrationStatus = "not_started", string? NarrationUnavailableReason = null,
    bool RequiresParticipantConsent = false, bool LeftJoined = false, bool RightJoined = false);
internal sealed record DuelRoundCommentary(int Round, string Quip, string Mishap);
internal sealed record DuelCommentary(string Provider, string Model, IReadOnlyList<DuelRoundCommentary> Rounds);
internal sealed record DuelCompletionEvent(string Id, string DeviceId, string EveningId,
    int Revision, bool Exhibition);

internal sealed class DuelDocument
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string EveningId { get; init; }
    public required string Seed { get; init; }
    public string RulesVersion { get; init; } = DuelRules.Version;
    public required string Mode { get; init; }
    public int Revision { get; set; }
    public string State { get; set; } = "Ready";
    public required DuelIdentitySnapshot Left { get; init; }
    public required DuelIdentitySnapshot Right { get; init; }
    public required DuelCommitment[] Commitments { get; init; }
    public List<DuelRound> Rounds { get; init; } = [];
    public string? LeftChoice { get; set; }
    public string? RightChoice { get; set; }
    public int? ChoiceRevision { get; set; }
    public bool RequiresParticipantConsent { get; init; }
    public bool LeftJoined { get; set; }
    public bool RightJoined { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public DuelResult? Result { get; set; }
    public string NarrationMode { get; set; } = "unbound";
    public string? NarrationUnavailableReason { get; set; }
    public string NarrationStage { get; set; } = "choice";
    public DateTimeOffset? NarrationExpiresAt { get; set; }

    public DuelView View() => new(Id, DeviceId, EveningId, RulesVersion, Mode, Revision, State,
        Left, Right, Math.Min(Rounds.Count + 1, 3), Deadline, LeftChoice is not null,
        RightChoice is not null, Rounds.ToArray(), State == "Completed" ? Result : null,
        State == "Completed" ? Seed : null, NarrationMode: NarrationMode,
        NarrationStatus: NarrationMode == "physical_device" ? NarrationStage
            : NarrationMode == "web_only" ? "web_only_no_spoken_confirmation" : "not_started",
        NarrationUnavailableReason: NarrationUnavailableReason, RequiresParticipantConsent: RequiresParticipantConsent,
        LeftJoined: LeftJoined, RightJoined: RightJoined);
}
