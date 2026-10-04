using System.Text;

namespace Kufr.Backend.Features.Conversations;

internal static class PartyDialoguePrompt
{
    internal static readonly string VoicePersona = ReadResource("Kufr.Backend.VoicePersona");
    internal static readonly string DecisionInstructions = VoicePersona + "\n\n" + ReadResource("Kufr.Backend.PartyPrompt");

    private static string ReadResource(string name)
    {
        using var stream = typeof(PartyDialoguePrompt).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded dialogue prompt is missing: {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }
}
