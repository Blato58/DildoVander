
namespace Kufr.Backend.Features.Conversations;

// Deterministic adapter retained for offline fixtures and the explicitly fake twin.
// Production conversation engines never register or construct this adapter.
internal sealed class TextDialogRunner : IDisposable
{
    private readonly Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, string, string, Task<TextDialogResponse>>? generate;
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly Func<byte[], CancellationToken, Task<CostumeImageResponse>>? analyzeImage;
    private volatile bool enabled = true;

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, Task<TextDialogResponse>> generate,
        CancellationToken stopping = default)
    {
        this.generate = (text, mask, history, question, token, _, _) => generate(text, mask, history, question, token);
        Stopping = stopping;
    }

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, string, Task<TextDialogResponse>> generate)
    {
        this.generate = (text, mask, history, question, token, context, _) => generate(text, mask, history, question, token, context);
    }

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, string, string, Task<TextDialogResponse>> generate,
        Func<byte[], CancellationToken, Task<CostumeImageResponse>>? analyzeImage = null)
    {
        this.generate = generate;
        this.analyzeImage = analyzeImage;
    }

    public string Model { get; } = ResponsesTextDialogBudget.Model;

    public bool Available => generate is not null;
    public bool Enabled => Available && enabled;
    internal void SetEnabled(bool value) => enabled = value;

    public CancellationToken Stopping { get; }

    public bool TryAcquire() => Enabled && slot.Wait(0);

    internal Task<CostumeImageResponse> AnalyzeImageAsync(byte[] jpeg, CancellationToken cancellationToken) =>
        analyzeImage is null
            ? throw new InvalidOperationException("Image provider unavailable.")
            : analyzeImage(jpeg, cancellationToken);

    public Task<TextDialogResponse> GenerateAsync(
        string text,
        int readyMask,
        IReadOnlyList<DialogExchange> history,
        bool requireQuestion,
        CancellationToken cancellationToken,
        string eveningContext = "",
        string personalityId = PersonalityCatalog.DefaultId,
        string costumeContext = "")
    {
        return generate!(text, readyMask, history, requireQuestion, cancellationToken, eveningContext + costumeContext, personalityId);
    }

    public void Release() => slot.Release();

    public void Dispose()
    {
        // Fixture delegates own their resources. Pending finally blocks may
        // still release the semaphore during cancellation cleanup.
    }
}
