using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Wands;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;

namespace Kufr.Backend.Features.TestingSessions;

internal sealed record TestingSession(string Id, string OwnerId, string VirtualDeviceId, string LabSessionId,
    string Engine, string InputMode, string Status, string? CharacterId, DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt, int Revision, bool OpenAiConsent, bool LongTermConsent = false,
    string? KeepsakeId = null, string? OperationId = null, CharacterConversationSnapshot? CharacterSnapshot = null);
internal sealed record TestingCharacter(string Id, string OwnerId, CharacterProfile Profile, bool LongTermConsent);
internal sealed record TestingSessionRequest(string Engine = "gpt-live-1", string InputMode = "text",
    bool OpenAiConsent = false, bool RecordContent = false, string? OperationId = null, string Platform = "web");
internal sealed record TestingCharacterRequest(CharacterUserInput UserInput, bool OpenAiConsent = false,
    bool LongTermConsent = false, long ExpectedRevision = 0);
internal sealed record TestingEnrichRequest(bool OpenAiConsent = false);
internal sealed record TestingCharacterSelection(string CharacterId, int ExpectedRevision);
internal sealed record TestingModeRequest(string InputMode, int ExpectedRevision);
internal sealed record TestingGenerationRetryRequest(string JobId,bool ConfirmAmbiguous=false);
internal sealed record TestingFaultRequest(string Fault);
internal sealed record TestingDuelRequest(string OtherSessionId, string Mode, string OperationId);
internal sealed record TestingHardwareEvent(string Id, string Type, string Source, string Code,
    DateTimeOffset CreatedAt, int? Compartment = null, string? OperationId = null);
internal sealed record TestingCompartment(int Slot, string State, string? WandName = null);
internal sealed record TestingHardware(bool Connected, bool Ready, string State,
    TestingCompartment[] Compartments, int? SelectedCompartment, string CurrentAction,
    string LedPattern, string? ServoCommand, string? VibrationCommand, bool CameraAvailable,
    TestingHardwareEvent[] Events, bool FailServo = false);
internal sealed record TestingMemory(string Status, IReadOnlyList<MemoryRecord> Records);
internal sealed record TestingSessionDetail(TestingSession Session, LabSession Conversation,
    TestingCharacter? Character, TestingHardware Hardware, WandGenerationSession? Wand,
    KeepsakeView[] Keepsakes, TestingMemory Memory, bool ConversationActive = false);
internal sealed class TestingException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}
