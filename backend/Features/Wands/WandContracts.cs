using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Conversations;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Wands;

// PhysicalSlot is a presentation location. WandId is never a hardware command.
internal sealed record WandSpell(string Action, string Name, string Description);
internal sealed record GeneratedWandSnapshot(string WandId, string SessionId, string ProfileId,
    long ProfileRevision, int PhysicalSlot, string Name, string Material, string Core,
    string Temperament, string Lore, string DesignDescription, string Affinity, string Weakness,
    WandSpell[] Spells)
{
    internal GeneratedWandPublicSnapshot ToPublic() => new(WandId, PhysicalSlot, Name, Material, Core,
        Temperament, Lore, DesignDescription, Affinity, Weakness, Spells);
}
internal sealed record GeneratedWandPublicSnapshot(string WandId, int PhysicalSlot, string Name,
    string Material, string Core, string Temperament, string Lore, string DesignDescription,
    string Affinity, string Weakness, WandSpell[] Spells);
internal sealed record WandCandidateContent(string Name, string Material, string Core,
    string Temperament, string Lore, string DesignDescription, string Affinity, string Weakness,
    WandSpell[] Spells);
internal sealed record WandGenerationRequest(string AccountId, string OperationId,
    CharacterConversationSnapshot Profile, int ReadyMask, bool OpenAiConsent, string[] Evidence);
internal sealed record WandGenerationSession(string SessionId, string OperationId, string ProfileId,
    long ProfileRevision, int ReadyMask, string Status, string? ErrorCode,
    GeneratedWandSnapshot[] Candidates, GeneratedWandSnapshot? SelectedWand);
internal sealed record WandGenerationJob(string SessionId, string LeaseToken, WandGenerationRequest Request);
internal interface IWandGenerationProvider
{
    Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken cancellationToken);
}
internal sealed class WandGenerationException(string code, int status = 409) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal static class WandGenerationValidation
{
    private static readonly HashSet<string> LegacyNames = PartyWandCatalog.Available(63)
        .SelectMany(legacy => new[] { legacy.DisplayName, legacy.DisplayName.Split("hůlka ", 2)[^1] })
        .Select(SpokenIdentityKey).ToHashSet(StringComparer.Ordinal);
    internal static int[] Slots(int mask) => Enumerable.Range(1, 6).Where(slot => (mask & (1 << (slot - 1))) != 0).ToArray();
    internal static void Validate(WandCandidateContent[] candidates, int mask)
    {
        if (mask is < 1 or > 63 || candidates is null || candidates.Length != Slots(mask).Length)
            throw new InvalidDataException("Candidate count does not match eligible slots.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spokenNames = new HashSet<string>(StringComparer.Ordinal);
        var spells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate is null || !Text(candidate.Name, 100) || !names.Add(Normalize(candidate.Name))
                || string.IsNullOrWhiteSpace(SpokenIdentityKey(candidate.Name))
                || !spokenNames.Add(SpokenIdentityKey(candidate.Name))
                || LegacyNames.Contains(SpokenIdentityKey(candidate.Name))
                || !Text(candidate.Material, 160) || !Text(candidate.Core, 160)
                || !Text(candidate.Temperament, 300) || !Text(candidate.Lore, 700)
                || !Text(candidate.DesignDescription, 700) || !Text(candidate.Affinity, 200)
                || !Text(candidate.Weakness, 200) || candidate.Spells is not { Length: 3 }
                || !candidate.Spells.Select(spell => spell?.Action).OrderBy(action => action)
                    .SequenceEqual(new[] { "shield", "strike", "trick" }))
                throw new InvalidDataException("Invalid generated wand fields.");
            foreach (var spell in candidate.Spells)
                if (spell is null || !Text(spell.Name, 100) || !Text(spell.Description, 300) || !spells.Add(Normalize(spell.Name)))
                    throw new InvalidDataException("Spells must be original and bounded.");
        }
        var slots = Slots(mask);
        var speechEntries = candidates.Select((candidate, index) => new PartyWandCatalog.WandEntry(
            slots[index], candidate.Name, "", [])).ToArray();
        if (speechEntries.Any(candidate => !Kufr.Backend.Features.GptLive.LiveTerminalSpeech.Ready(
            candidate.DisplayName, candidate.Compartment, speechEntries)))
            throw new InvalidDataException("Candidate names cannot be distinguished in the spoken verdict.");
    }

    internal static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
    private static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormKC);
    internal static string SpokenIdentityKey(string value)
    {
        var plain = string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        var marker = plain.IndexOf(" hulka ", StringComparison.Ordinal);
        if (marker >= 0) plain = plain[(marker + " hulka ".Length)..];
        return string.Join(" ", Regex.Matches(plain, @"\p{L}+").Select(match => match.Value));
    }
}
