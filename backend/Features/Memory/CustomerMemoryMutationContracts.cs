namespace Kufr.Backend.Features.Memory;

internal sealed record CustomerMemoryCorrectionRequest(long ExpectedRevision, string Text);
internal sealed record CustomerMemoryForgetRequest(long ExpectedRevision, bool Confirmed);
internal sealed record CustomerMemoryMutationResult(string Id, long Revision, string State);

// A rejected correction commits only the archive's existing diagnostic counter.
// It must leave the old fact, its permissions and its indexes unchanged.
internal sealed record CustomerMemoryCorrectionResult(
    CustomerMemoryMutationResult? Mutation, bool CapacityRejected);
