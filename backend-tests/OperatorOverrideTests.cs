using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;

internal static class OperatorOverrideTests
{
    internal static void Run()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var binding = new OperatorSelectionOverride(2, "evidence-1:availability-9:mask63", now.AddSeconds(30));
        Check(binding.IsCurrent(binding.Fingerprint, now.AddSeconds(29)), "current operator intent admitted");
        Check(!binding.IsCurrent("evidence-2:availability-9:mask63", now), "guest correction invalidates forced slot");
        Check(!binding.IsCurrent("evidence-1:availability-10:mask63", now), "availability revision invalidates forced slot");
        Check(!binding.IsCurrent("evidence-1:availability-9:mask61", now), "availability mask invalidates forced slot");
        Check(!binding.IsCurrent(binding.Fingerprint, now.AddSeconds(30)), "expiry is exclusive at exact boundary");
        var input = new WandDecisionInput(7, 63, 9, [new("input:7", "Hostův aktuální vstup")],
            new GptLiveOptions().Policy(PersonalityCatalog.OllivanderId), 1, true);
        var result = WandDecisionService.OperatorOverride(input, 2);
        Check(result.Status == WandDecisionStatus.ValidProposal && result.Compartment == 2
            && result.DecisionRevision == 7 && result.AvailabilityRevision == 9
            && result.OperationState == "operator_proposal_only" && result.CostMicroUsd == 0,
            "operator proposal preserves current revisions and provenance without provider");
        Check(WandDecisionService.OperatorOverride(input with { ReadyMask = 1 }, 2).Status == WandDecisionStatus.Stale,
            "no unavailable slot override");
        Check(WandDecisionService.OperatorOverride(input with { ExcludeCompartment = 2 }, 2).Status == WandDecisionStatus.Stale,
            "excluded slot is not forced through alternate request");
        Console.WriteLine("Operator override revision/expiry/provenance tests passed.");
    }
    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
}
