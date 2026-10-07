using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.GptLive;

internal sealed record LiveTranscriptSegment(string Reference, string Speaker, string Text, double StartMs, double EndMs, string? EventId);
internal enum LiveRequestIntent { Selection, Memory, Remember, Forget, Alternative, CancelConversation, StopSpeech, Repeat, Technical, Thanks, Acknowledge }
internal sealed record LiveBackendTask(string DelegationId, Guid TaskId, string ConversationId, string? GuestId,
    string SessionGeneration, long DecisionRevision, int AvailabilityRevision, string State, string? OperationId, double OffsetMs);

internal sealed class LiveConversationProjection(GptLiveOptions options)
{
    private readonly List<LiveTranscriptSegment> segments = [];
    private readonly List<DecisionEvidence> answers = [];
    private readonly HashSet<string> eventIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> questionAnswers = new(StringComparer.Ordinal);
    private string? currentInputReference;
    private string? questionReference;
    private string? currentOutputReference;
    private double latestQuestionEnd = -1;
    private int characters;
    private int outputAnswerCount = -1;
    private string outputTurn = "";
    private int authoritativeFrom;
    private int correctionOffset = -1;
    private int outputGeneration;
    private int inputOutputGeneration = -1;
    private double lastInputEnd = -1;
    private double lastOutputEnd = -1;
    internal long Revision { get; private set; }
    internal long ContentVersion { get; private set; }
    internal bool HasCorrection { get; private set; }
    internal IReadOnlyList<LiveTranscriptSegment> Segments => segments;
    internal string LatestInput => answers.LastOrDefault()?.Text ?? "";
    internal int AnswerCandidates => questionAnswers.Count;
    internal bool UnansweredDiagnosticCandidate => questionReference is not null && !questionAnswers.ContainsKey(questionReference);
    internal string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", answers.Skip(authoritativeFrom)
            .Where(e => Classify(e.Text) is not (LiveRequestIntent.Thanks or LiveRequestIntent.Acknowledge
                or LiveRequestIntent.Repeat or LiveRequestIntent.Technical or LiveRequestIntent.StopSpeech))
            .Select(e => e.Reference + ":" + e.Text)))));
    internal LiveRequestIntent Intent => Classify(LatestInput);
    internal bool DirectSelection => Regex.IsMatch(LatestInput, @"(?i)(prostě|rovnou|hned|nějakou|libovolnou).{0,35}(vyber|zvol)|(?:vyber|zvol).{0,35}(prostě|rovnou|nějakou|libovolnou)");

    internal bool Append(JsonElement value, bool input)
    {
        var text = GptLiveProtocol.RequiredString(value, "delta", options.TranscriptCharacterLimit);
        var start = GptLiveProtocol.Milliseconds(value, "start_ms"); var end = GptLiveProtocol.Milliseconds(value, "end_ms");
        if (end < start) throw new InvalidDataException("Invalid transcript time.");
        var eventId = value.TryGetProperty("event_id", out var eid) ? eid.GetString() : null;
        if (eventId is not null && !eventIds.Add(eventId)) return false;
        // A bounded session archive is independent from the smaller decision
        // context. Exceeding its cap drops later archival text, not audio/session.
        var retained = Math.Max(0, Math.Min(text.Length, options.TranscriptCharacterLimit - characters));
        if (retained > 0)
        {
            characters += retained;
            segments.Add(new("segment-" + segments.Count, input ? "user" : "assistant", text[..retained], start, end, eventId));
        }
        if (input)
        {
            var newAnswer = currentInputReference is null || inputOutputGeneration != outputGeneration && start >= lastOutputEnd;
            if (newAnswer)
            {
                currentInputReference = "answer-" + answers.Count;
                if (Classify(text) is not (LiveRequestIntent.Thanks or LiveRequestIntent.Acknowledge or LiveRequestIntent.Repeat or LiveRequestIntent.Technical or LiveRequestIntent.StopSpeech)) Revision++;
                answers.Add(new(currentInputReference, "")); correctionOffset = -1; inputOutputGeneration = outputGeneration;
            }
            var old = answers[^1];
            // Preserve word splitting verbatim; keep a bounded head and latest
            // tail so a late correction in a long story remains available.
            var joined = old.Text + text;
            if (LiveTextBudget.UpperBound(joined) > options.DecisionContextBytes)
                joined = LiveTextBudget.Bound(joined, options.DecisionContextBytes / 2) + "\n[… delší odpověď …]\n"
                    + Tail(joined, options.DecisionContextBytes / 2 - 100);
            var correction = Regex.Matches(joined, @"(?i)\b(oprava|opravuji|vlastně ne|myslel[ao]? jsem|neplatí)\b").LastOrDefault();
            HasCorrection = correction is not null && correction.Index > correctionOffset;
            if (HasCorrection)
            {
                // Conservative projection: a substantive correction supersedes
                // earlier candidate evidence. Raw timed speech remains separate.
                authoritativeFrom = answers.Count - 1; Revision++;
                joined = joined[correction!.Index..]; correctionOffset = 0;
            }
            answers[^1] = old with { Text = joined }; ContentVersion++;
            lastInputEnd = Math.Max(lastInputEnd, end);
            if (questionReference is not null && Classify(joined) is LiveRequestIntent.Selection
                && !Regex.IsMatch(joined, @"(?i)^(cože|neslyšel|zopakuj|nerozuměl)"))
                questionAnswers[questionReference] = currentInputReference!;
            return true;
        }
        // Track each speaker's timeline separately. Network interleaving never
        // fabricates alternating user/assistant turns.
        if (currentOutputReference is not null && end < lastInputEnd) return true; // late prior-speaker packet: archive only
        lastOutputEnd = Math.Max(lastOutputEnd, end);
        if (currentOutputReference is null || outputAnswerCount != answers.Count && start >= lastInputEnd)
        {
            currentOutputReference = "question-candidate-" + start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            outputAnswerCount = answers.Count; outputTurn = ""; outputGeneration++;
        }
        outputTurn = LiveTextBudget.Bound(outputTurn + text, options.DecisionContextBytes);
        if (IsDiagnosticQuestion(outputTurn))
        {
            questionReference = currentOutputReference; latestQuestionEnd = end;
        }
        return true;
    }
    internal IReadOnlyList<DecisionEvidence> Evidence()
    {
        // Always keep each answer's relevant head plus the newest correction.
        // Do not silently retain only the last two exchanges.
        var active = answers.Skip(authoritativeFrom).TakeLast(64).ToArray();
        var allowance = Math.Max(64, options.DecisionContextBytes / Math.Max(1, active.Length) - 32);
        return active.Select((e, i) => e with
        {
            Text = i == active.Length - 1 ? Tail(e.Text, allowance) : LiveTextBudget.Bound(e.Text, allowance)
        }).ToArray();
    }
    internal static string Tail(string text, int bytes)
    {
        var runes = text.EnumerateRunes().ToArray(); var used = 0; var start = runes.Length;
        while (start > 0 && used + runes[start - 1].Utf8SequenceLength <= bytes) used += runes[--start].Utf8SequenceLength;
        return string.Concat(runes.Skip(start).Select(r => r.ToString()));
    }
    internal static bool IsDiagnosticQuestion(string text) => Regex.IsMatch(text, @"(?i)(jak|co|proč|kdy|popiš|pověz|řekni|kter).*[?]")
        && !Regex.IsMatch(text, @"(?i)(zopak|neslyš|nerozum|souhlas|potvr|odmít|nelíb|proč.{0,20}hůlk)");
    internal static LiveRequestIntent Classify(string text)
    {
        if (Regex.IsMatch(text, @"(?i)(zruš.{0,30}(výběr|operaci|rozhovor|hru)|nechci.{0,20}(hůlku|hrát)|ukonči|končím|nevybírej)")
            || Regex.IsMatch(text.Trim(), @"(?i)^(stačí|dost|konec)[.!]?$")) return LiveRequestIntent.CancelConversation;
        if (Regex.IsMatch(text, @"(?i)(drž hubu|přestaň mluvit|buď zticha)")) return LiveRequestIntent.StopSpeech;
        if (Regex.IsMatch(text.Trim(), @"(?i)^(zapamatuj si|ulož si|pamatuj si)\b")) return LiveRequestIntent.Remember;
        if (Regex.IsMatch(text.Trim(), @"(?i)^(zapomeň|smaž).{0,60}(paměť|vzpomín|všechno|vše o mně|údaj)")) return LiveRequestIntent.Forget;
        if (Regex.IsMatch(text, @"(?i)(pamatuješ|vzpomín|minule|dřívější)")) return LiveRequestIntent.Memory;
        if (Regex.IsMatch(text, @"(?i)(jinou hůlku|alternativ|nelíbí.{0,30}hůlk|odmítám.{0,30}hůlk)")) return LiveRequestIntent.Alternative;
        if (Regex.IsMatch(text.Trim(), @"(?i)^(díky|děkuji|děkuj[ui]|dík)(?:\s+(?:moc|pěkně|za hůlku|za výběr))?[.!\s]*$")) return LiveRequestIntent.Thanks;
        if (Regex.IsMatch(text.Trim(), @"(?i)^(?:(?:ano|jo|tak jo|dobře|jasně|ok|okay|super|souhlasím|beru ji|to beru|sedí|konečně)[,.!\s]*)?(?:(?:tak\s+)?(?:ukaž|předveď|dej|vysuň)\s+(?:ji|mi ji)[.!\s]*)$"))
            return LiveRequestIntent.Acknowledge;
        if (Regex.IsMatch(text.Trim(), @"(?i)^(ano|jo|tak jo|dobře|jasně|ok|okay|super|souhlasím|beru ji|to beru|sedí|konečně)[.!\s]*$"))
            return LiveRequestIntent.Acknowledge;
        if (Regex.IsMatch(text, @"(?i)^(cože|zopakuj|neslyšel|nerozuměl)")) return LiveRequestIntent.Repeat;
        if (Regex.IsMatch(text, @"(?i)^(jak funguje|kolik stojí|jaký model|jaké api)")) return LiveRequestIntent.Technical;
        return LiveRequestIntent.Selection;
    }
}
