using System.Text;

namespace Kufr.Backend.Features.Conversations;

internal static class PartyDialoguePrompt
{
    internal static readonly string VoicePersona = ReadResource("Kufr.Backend.VoicePersona");
    internal static readonly string SelectionInstructions = ReadResource("Kufr.Backend.PartyPrompt");
    internal static readonly string DecisionInstructions = VoicePersona + "\n\n" + SelectionInstructions;

    internal static string ReadResource(string name, bool trim = true)
    {
        using var stream = typeof(PartyDialoguePrompt).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded dialogue prompt is missing: {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return trim ? text.Trim() : text;
    }
}
