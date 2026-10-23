using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Character;

// A projection owned and serialized by the existing conversation session owner.
// It neither starts sessions nor owns audio, device operations, timers or persistence.
internal sealed class CharacterSession
{
    internal const int MaximumSourceCharacters = 4096;
    internal const long MaximumSourceRevision = 9_007_199_254_740_991;
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly List<CharacterAnchor> anchors = new(3);
    private readonly HashSet<string> acceptedSignals = new(StringComparer.Ordinal);
    private readonly HashSet<string> disallowedSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> retiredImageSources = new(StringComparer.Ordinal);
    private long anchorSequence;
    private long visualPermissionRevision;
    private bool visualAllowed;
    private string? currentImageSource;
    private readonly ICharacterToneState toneState;

    internal CharacterSession(string sessionGeneration, CharacterPolicy policy, IReadOnlyList<string>? recentStyles = null, ICharacterToneState? toneState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionGeneration);
        if (sessionGeneration.Length > 128 || !Enum.IsDefined(policy.HostTone) || !Enum.IsDefined(policy.FinaleProfile)
            || string.IsNullOrWhiteSpace(policy.Version) || policy.Version.Length > 80)
        {
            throw new ArgumentException("Invalid character policy or session generation.");
        }
        SessionGeneration = sessionGeneration;
        Policy = policy;
        this.toneState = toneState ?? new CharacterToneState(policy.HostTone);
        RecentStyles = (recentStyles ?? []).Where(EveningStyleState.IsAllowed).TakeLast(36).ToArray();
    }

    internal string SessionGeneration { get; }
    internal CharacterPolicy Policy { get; }
    internal IReadOnlyList<string> RecentStyles { get; }
    internal IReadOnlyList<CharacterAnchor> Anchors => anchors.ToArray();
    internal CharacterTone Tone => toneState.Tone;
    internal CharacterTempo Tempo { get; private set; }
    internal CharacterEngagement Engagement { get; private set; }
    internal bool RoastAllowed => toneState.RoastAllowed;
    internal bool Closed { get; private set; }
    internal long Revision { get; private set; }
    internal long ResponseGeneration { get; private set; }
    internal long SourceRevision { get; private set; }

    // Operator settings remain constrained by the frozen host ceiling and all
    // guest opt-outs already recorded by this same session owner.
    internal void ApplyOperatorStyle(string? tone = null, string? tempo = null)
    {
        if (Closed) { return; }
        if (tone is not null && Enum.TryParse<CharacterTone>(tone, true, out var requested) && Enum.IsDefined(requested))
        {
            toneState.SetOperatorTone(requested);
        }
        if (tempo is "express") { Tempo = CharacterTempo.Express; }
        else if (tempo is "standard" && Engagement != CharacterEngagement.LowPressure) { Tempo = CharacterTempo.Standard; }
        Revision++;
    }

    internal CharacterInputChange AcceptInput(string sourceId, long sourceRevision, string text, bool correction = false)
    {
        if (Closed || !Policy.Enabled || string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128
            || sourceRevision is < 0 or > MaximumSourceRevision
            || string.IsNullOrWhiteSpace(text) || text.Length > MaximumSourceCharacters)
        {
            return default;
        }
        if (sources.TryGetValue(sourceId, out var previous) && sourceRevision <= previous.Revision)
        {
            return default;
        }

        // Bounded original spans exist only in this transient projection.
        sources[sourceId] = new Source(sourceRevision, text);
        SourceRevision = sourceRevision;
        disallowedSources.Remove(sourceId);
        while (sources.Count > 32)
        {
            var oldest = sources.Keys.First();
            sources.Remove(oldest);
            disallowedSources.Remove(oldest);
            anchors.RemoveAll(anchor => anchor.SourceId == oldest);
        }

        var command = ClassifyExplicitControl(text);
        if (command is { } control)
        {
            RememberControl($"{sourceId}:{sourceRevision}:{control}:");
            if (previous is not null && anchors.RemoveAll(anchor => anchor.SourceId == sourceId) > 0)
            {
                Revision++;
                CancelResponse();
                var changed = Apply(control, null, sourceId, sourceRevision);
                return changed with { CancelResponse = true, EvidenceChanged = true };
            }
            return Apply(control, null, sourceId, sourceRevision);
        }

        Revision++;
        var changedExisting = previous is not null || correction;
        if (correction)
        {
            RevokeTextAnchors(null);
            disallowedSources.Remove(sourceId);
        }
        else if (previous is not null)
        {
            anchors.RemoveAll(anchor => anchor.SourceId == sourceId);
        }

        // The fallback captures only a short explicit self-statement/preference. It is
        // still a reported utterance, never an inferred trait, diagnosis or verified fact.
        var compact = text.Trim();
        if (compact.Length <= 160 && !compact.Contains('\n') && !compact.Contains('"')
            && !compact.Contains('„') && !compact.Contains('“') && !compact.Contains(':')
            && !Regex.IsMatch(compact, @"[.!?].+\S", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            var kind = IsExplicitPreference(compact) ? CharacterAnchorKind.Preference : CharacterAnchorKind.GuestStatement;
            if (kind == CharacterAnchorKind.Preference || IsExplicitSelfStatement(compact))
            {
                AddAnchor(kind, compact.TrimEnd('.', '!', '?'), sourceId, sourceRevision);
            }
        }
        Engagement = compact.Length <= 12 ? CharacterEngagement.LowPressure : CharacterEngagement.Conversational;
        if (changedExisting)
        {
            CancelResponse();
        }
        return new CharacterInputChange(changedExisting, EvidenceChanged: true);
    }

    // For the existing evidence projection: a valid span establishes source binding,
    // not correct interpretation. The caller remains responsible for semantic eligibility.
    internal bool AcceptAnchor(string generation, string sourceId, long sourceRevision,
        int quoteStart, int quoteLength, CharacterAnchorKind kind)
    {
        if (!TrySource(generation, sourceId, sourceRevision, quoteStart, quoteLength, out var quote)
            || !Enum.IsDefined(kind) || kind == CharacterAnchorKind.VisualObservation || !RoastAllowed
            || disallowedSources.Contains(sourceId) || ClassifyExplicitControl(sources[sourceId].Text) is not null)
        {
            return false;
        }
        if (anchors.Any(anchor => anchor.SourceId == sourceId && anchor.SourceRevision == sourceRevision
            && anchor.Text == quote && anchor.Kind == kind))
        {
            return false;
        }
        AddAnchor(kind, quote, sourceId, sourceRevision);
        return true;
    }

    internal CharacterInputChange AcceptSignal(CharacterSignal signal)
    {
        if (!Enum.IsDefined(signal.Kind)
            || !TrySource(signal.SessionGeneration, signal.SourceId, signal.SourceRevision,
                signal.QuoteStart, signal.QuoteLength, out _))
        {
            return default;
        }
        // A suggested semantic control must agree with a complete explicit accepted
        // utterance. Arbitrary model labels or quoted Stop never acquire authority.
        if (ClassifyExplicitControl(sources[signal.SourceId].Text) != signal.Kind)
        {
            return default;
        }
        var identity = $"{signal.SourceId}:{signal.SourceRevision}:{signal.Kind}:{signal.TargetAnchorId}";
        if (!RememberControl(identity))
        {
            return default;
        }
        return Apply(signal.Kind, signal.TargetAnchorId, signal.SourceId, signal.SourceRevision);
    }

    internal CharacterInputChange UpdateVisualPermission(string generation, long permissionRevision, bool allowed)
    {
        if (Closed || generation != SessionGeneration || permissionRevision <= visualPermissionRevision)
        {
            return default;
        }
        visualPermissionRevision = permissionRevision;
        visualAllowed = allowed;
        RetireCurrentImage();
        currentImageSource = null;
        var removed = anchors.RemoveAll(anchor => anchor.Kind == CharacterAnchorKind.VisualObservation) > 0;
        if (removed || !allowed)
        {
            CancelResponse();
        }
        Revision++;
        return new CharacterInputChange(removed || !allowed, ImageRevoked: !allowed);
    }

    internal bool AcceptVisualObservation(string generation, string observationId, long sourceRevision,
        long permissionRevision, string visibleDetail, bool unambiguousSubject)
    {
        if (Closed || !Policy.Enabled || generation != SessionGeneration || !visualAllowed || !RoastAllowed
            || permissionRevision != visualPermissionRevision || !unambiguousSubject
            || sourceRevision is < 0 or > MaximumSourceRevision
            || retiredImageSources.Contains(observationId) || retiredImageSources.Count >= 32
            || string.IsNullOrWhiteSpace(observationId) || observationId.Length > 128
            || string.IsNullOrWhiteSpace(visibleDetail) || visibleDetail.Length > 160)
        {
            return false;
        }
        if (currentImageSource != observationId)
        {
            RetireCurrentImage();
            anchors.RemoveAll(anchor => anchor.Kind == CharacterAnchorKind.VisualObservation);
            currentImageSource = observationId;
        }
        var existing = anchors.Where(anchor => anchor.Kind == CharacterAnchorKind.VisualObservation
            && anchor.SourceId == observationId).ToArray();
        if (existing.Any(anchor => anchor.SourceRevision > sourceRevision
            || (anchor.SourceRevision == sourceRevision && anchor.Text == visibleDetail)))
        {
            return false;
        }
        if (existing.Any(anchor => anchor.SourceRevision < sourceRevision))
        {
            anchors.RemoveAll(anchor => anchor.Kind == CharacterAnchorKind.VisualObservation);
            CancelResponse();
        }
        Revision++;
        AddAnchor(CharacterAnchorKind.VisualObservation, visibleDetail.Trim(), observationId, sourceRevision, permissionRevision);
        return true;
    }

    internal void CancelResponse() => ResponseGeneration++;

    internal void Close()
    {
        if (Closed)
        {
            return;
        }
        Closed = true;
        CancelResponse();
        Revision++;
        anchors.Clear();
        sources.Clear();
        acceptedSignals.Clear();
        disallowedSources.Clear();
        retiredImageSources.Clear();
        currentImageSource = null;
        visualAllowed = false;
    }

    private CharacterInputChange Apply(CharacterSignalKind kind, string? targetAnchorId, string sourceId, long sourceRevision)
    {
        switch (kind)
        {
            case CharacterSignalKind.RequestEnd:
                Close();
                return new CharacterInputChange(true, true, true, true);
            case CharacterSignalKind.RequestExpress:
                Tempo = CharacterTempo.Express;
                return default;
            case CharacterSignalKind.RequestStopSpeech:
                CancelResponse();
                return new CharacterInputChange(true);
            case CharacterSignalKind.ToneDown:
            case CharacterSignalKind.NoSwearing:
            case CharacterSignalKind.ToneUp:
            case CharacterSignalKind.WithdrawRoast:
                var cancel = toneState.ApplyControl(kind, sourceId, sourceRevision);
                if (cancel) { CancelResponse(); }
                return new CharacterInputChange(cancel);
            case CharacterSignalKind.RevokeImage:
                visualAllowed = false;
                visualPermissionRevision++;
                RetireCurrentImage();
                currentImageSource = null;
                anchors.RemoveAll(anchor => anchor.Kind == CharacterAnchorKind.VisualObservation);
                Revision++;
                CancelResponse();
                return new CharacterInputChange(true, ImageRevoked: true);
            case CharacterSignalKind.RevokeTopic:
                var target = targetAnchorId is null ? anchors.LastOrDefault()
                    : anchors.FirstOrDefault(anchor => anchor.Id == targetAnchorId);
                if (target?.Kind == CharacterAnchorKind.VisualObservation)
                {
                    // A revoked visual topic cannot return under a new observation
                    // label from the same image. New consent needs a new source.
                    visualAllowed = false;
                    visualPermissionRevision++;
                    RetireCurrentImage();
                    currentImageSource = null;
                    anchors.RemoveAll(anchor => anchor.Kind == CharacterAnchorKind.VisualObservation);
                }
                else if (target is not null)
                {
                    RevokeTextAnchors(target.Id);
                }
                Revision++;
                CancelResponse();
                return new CharacterInputChange(true, ImageRevoked: target?.Kind == CharacterAnchorKind.VisualObservation);
            case CharacterSignalKind.CorrectEvidence:
            case CharacterSignalKind.AttributeToOther:
            case CharacterSignalKind.MarkFiction:
                RevokeTextAnchors(targetAnchorId);
                Revision++;
                CancelResponse();
                return new CharacterInputChange(true, EvidenceChanged: true);
            default:
                return default;
        }
    }

    private bool RememberControl(string identity)
    {
        if (!acceptedSignals.Add(identity))
        {
            return false;
        }
        while (acceptedSignals.Count > 64)
        {
            acceptedSignals.Remove(acceptedSignals.First());
        }
        return true;
    }

    private void RetireCurrentImage()
    {
        if (currentImageSource is not null)
        {
            retiredImageSources.Add(currentImageSource);
        }
    }

    private void RevokeTextAnchors(string? targetAnchorId)
    {
        foreach (var anchor in anchors.Where(anchor => targetAnchorId is null
            ? anchor.Kind != CharacterAnchorKind.VisualObservation : anchor.Id == targetAnchorId))
        {
            disallowedSources.Add(anchor.SourceId);
        }
        anchors.RemoveAll(anchor => targetAnchorId is null
            ? anchor.Kind != CharacterAnchorKind.VisualObservation : anchor.Id == targetAnchorId);
    }

    private bool TrySource(string generation, string sourceId, long sourceRevision,
        int start, int length, out string quote)
    {
        quote = "";
        if (Closed || !Policy.Enabled || generation != SessionGeneration || string.IsNullOrWhiteSpace(sourceId)
            || length is <= 0 or > 160
            || start < 0 || !sources.TryGetValue(sourceId, out var source) || source.Revision != sourceRevision
            || start > source.Text.Length - length)
        {
            return false;
        }
        quote = source.Text.Substring(start, length);
        return !string.IsNullOrWhiteSpace(quote);
    }

    private void AddAnchor(CharacterAnchorKind kind, string text, string sourceId, long sourceRevision, long? permission = null)
    {
        if (!RoastAllowed)
        {
            return;
        }
        anchors.Add(new CharacterAnchor($"anchor-{++anchorSequence}", kind, text, sourceId, sourceRevision, Revision, permission));
        while (anchors.Count > 3)
        {
            anchors.RemoveAt(0);
        }
    }

    private static bool IsExplicitPreference(string text) => Regex.IsMatch(text,
        @"^(chci |mám rád |mám ráda |preferuji |vybral bych |vybrala bych |nejradši bych |nejraději bych )", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(20));

    private static bool IsExplicitSelfStatement(string text) => Regex.IsMatch(text,
        @"^(jsem |já jsem |moje |můj |můj kostým |ztrácím klíče )", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(20));

    internal static CharacterSignalKind? ClassifyExplicitControl(string text)
    {
        var phrase = text.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ',');
        if (phrase.Length > 240 || phrase.Contains('"') || phrase.Contains('„') || phrase.Contains('“'))
        {
            return null;
        }
        phrase = Regex.Replace(phrase, @"^(?:(?:ne,?|no,)\s+)?(?:(?:fakt|really)\s+)?", "", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(20));
        if (phrase.StartsWith("prosím ", StringComparison.Ordinal))
        {
            phrase = phrase[7..];
        }
        else if (phrase.StartsWith("please ", StringComparison.Ordinal))
        {
            phrase = phrase[7..];
        }
        if (phrase.EndsWith(" prosím", StringComparison.Ordinal))
        {
            phrase = phrase[..^7].TrimEnd(',');
        }
        else if (phrase.EndsWith(" please", StringComparison.Ordinal))
        {
            phrase = phrase[..^7].TrimEnd(',');
        }
        if (Regex.IsMatch(phrase,
            @"^(i don't want to continue|i do not want to continue|i want to end (this )?conversation|end (this )?conversation|stop (this )?conversation|let's end (this )?conversation|we're done|i'm done)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.RequestEnd;
        }
        if (Regex.IsMatch(phrase, @"^(nechci pokračovat|chci skončit|ukonči rozhovor|přestaň s rozhovorem)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.RequestEnd;
        }
        if (Regex.IsMatch(phrase, @"^(don't roast me|do not roast me|stop roasting me|i don't want (a )?roast|no more roasts|don't insult me|do not insult me|stop insulting me)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.WithdrawRoast;
        }
        if (Regex.IsMatch(phrase, @"^(už mě neurážej|nechci roast|přestaň mě urážet)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.WithdrawRoast;
        }
        if (Regex.IsMatch(phrase, @"^(nefoti mě|nefoť mě|nepoužívej můj vzhled|nepoužívej moji fotku|fotku (už )?nepoužívej)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.RevokeImage;
        }
        if (Regex.IsMatch(phrase, @"^(don't photograph me|do not photograph me|don't take my (photo|picture)|do not take my (photo|picture)|don't use my (photo|picture|appearance)|do not use my (photo|picture|appearance)|stop using my (photo|picture|appearance)|i withdraw my consent to use my (photo|picture|appearance)|no photos of me)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.RevokeImage;
        }
        if (Regex.IsMatch(phrase, @"^(ty vtipy jsou (dobrý|dobré),? (ale |jen )?bez nadávek|bez nadávek)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.NoSwearing;
        }
        if (Regex.IsMatch(phrase, @"^(no swearing|don't swear|do not swear|no profanity|don't use profanity|do not use profanity|keep it clean)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.NoSwearing;
        }
        if (Regex.IsMatch(phrase, @"^(don't talk about (this|that|it)|do not talk about (this|that|it)|let's not talk about (this|that|it)|i don't want to talk about (this|that|it)|change the subject)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.RevokeTopic;
        }
        if (Regex.IsMatch(phrase, @"^(stop talking|stop speaking|don't speak|do not speak|be quiet|please be quiet)(?:[,.! ]|$)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.RequestStopSpeech;
        }
        if (Regex.IsMatch(phrase, @"^to (s .{1,60} )?byl (můj )?(brácha|kamarád),? ne já$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)))
        {
            return CharacterSignalKind.AttributeToOther;
        }
        return phrase switch
        {
            "drž hubu" or "přestaň mluvit" or "buď zticha" or "teď nemluv" or "jen mlč" => CharacterSignalKind.RequestStopSpeech,
            "stop talking" or "stop speaking" or "don't speak" or "do not speak" or "be quiet" => CharacterSignalKind.RequestStopSpeech,
            "stop" or "konec" or "nechci pokračovat" or "ukonči rozhovor" or "končíme" => CharacterSignalKind.RequestEnd,
            "i don't want to continue" or "i do not want to continue" or "end conversation" or "end this conversation" or "stop conversation" or "stop this conversation" or "we're done" or "i'm done" => CharacterSignalKind.RequestEnd,
            "vyber rovnou" or "zrychli" or "rychleji" or "bez dalších otázek" => CharacterSignalKind.RequestExpress,
            "pick one" or "choose one" or "just choose" or "speed it up" or "faster" or "no more questions" or "skip the questions" or "decide for me" => CharacterSignalKind.RequestExpress,
            "mírněji" or "uber" or "trochu mírněji" => CharacterSignalKind.ToneDown,
            "tone it down" or "less intense" or "make it gentler" or "take it down a notch" or "calm down" => CharacterSignalKind.ToneDown,
            "přitvrď" or "ostřeji" => CharacterSignalKind.ToneUp,
            "turn it up" or "more intense" or "go harder" or "make it sharper" or "be edgier" => CharacterSignalKind.ToneUp,
            "bez nadávek" or "nenadávej" => CharacterSignalKind.NoSwearing,
            "no swearing" or "don't swear" or "do not swear" or "no profanity" => CharacterSignalKind.NoSwearing,
            "už mě neurážej" or "nechci roast" or "přestaň mě urážet" => CharacterSignalKind.WithdrawRoast,
            "don't roast me" or "do not roast me" or "stop roasting me" or "i don't want a roast" or "i don't want roast" or "no more roasts" or "don't insult me" or "do not insult me" => CharacterSignalKind.WithdrawRoast,
            "nefoti mě" or "nefoť mě" or "nepoužívej můj vzhled" or "nepoužívej moji fotku" or "odvolávám souhlas s fotkou" => CharacterSignalKind.RevokeImage,
            "don't photograph me" or "do not photograph me" or "don't take my photo" or "don't take my picture" or "don't use my photo" or "don't use my picture" or "don't use my appearance" or "no photos of me" => CharacterSignalKind.RevokeImage,
            "o tom nemluv" or "tohle téma nechci" => CharacterSignalKind.RevokeTopic,
            "don't talk about this" or "don't talk about that" or "let's not talk about this" or "let's not talk about that" or "change the subject" => CharacterSignalKind.RevokeTopic,
            "vlastně naopak" or "to není pravda" => CharacterSignalKind.CorrectEvidence,
            "to řekl kamarád" or "to řekla kamarádka" => CharacterSignalKind.AttributeToOther,
            "dělal jsem si srandu" or "dělala jsem si srandu" or "to byl vtip" => CharacterSignalKind.MarkFiction,
            _ => null
        };
    }

    private sealed record Source(long Revision, string Text);
}
