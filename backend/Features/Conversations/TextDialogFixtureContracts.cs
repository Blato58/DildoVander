namespace Kufr.Backend.Features.Conversations;

// The retired text driver uses this response only in deterministic fixtures.
internal sealed record TextDialogResponse(
    string Reply,
    string Decision,
    string Reason,
    int? Compartment,
    string ReturnedModel,
    int InputTokens,
    int OutputTokens,
    string? ServiceTier);

// Both retained engines use the full exchange history as application context.
internal sealed record DialogExchange(string VisitorText, string Reply);

// Also used by retained decision/vision usage validation.
internal sealed class TextDialogUnavailableException(string message) : Exception(message);

internal sealed class TextDialogRequestTooLargeException() : Exception("Dialogue request exceeds its reserved size.");
