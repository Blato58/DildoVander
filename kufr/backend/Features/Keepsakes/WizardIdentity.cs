using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Show;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Keepsakes;

// Storage provenance is intentionally separate from the explicitly public projection.
internal sealed record WizardIdentity(string IdentityId, string KeepsakeId, string EditionId,
    string SourcePresentationId, string CatalogWandId, WizardIdentityContent Content,
    string[] SourceRefs, string ContentVersion, string Visibility, DateTimeOffset CreatedAt);
internal sealed record WizardIdentityContent(string WizardAlias, string HouseOrGuild,
    string DubiousSpecialization, string SignatureAbility, string Weakness, string MinistryAssessment,
    string WandBond, string Motto, string MicroStory);
internal sealed record WizardIdentityCard(string IdentityId, string KeepsakeId, string EditionId,
    string CatalogWandId, string WizardAlias, string HouseOrGuild, string DubiousSpecialization,
    string SignatureAbility, string Weakness, string MinistryAssessment, string WandBond,
    string Motto, string MicroStory, string Omen, string ContentVersion, string Visibility);
internal sealed record StoredProphecy(string IdentityId, string EditionId, string Omen, string Revelation,
    string? Afterword, DateTimeOffset CreatedAt, DateTimeOffset UnlockAt, string? UnlockReason = null,
    string ContentVersion = "dv10-prophecy-v1", string Visibility = "owner_only");
internal sealed record ProphecyView(string IdentityId, string EditionId, string Omen,
    DateTimeOffset CreatedAt, DateTimeOffset UnlockAt, string State, string UnlockReason,
    string? Revelation = null, string? Afterword = null);
internal sealed record OperatorProphecy(string OutcomeId, string IdentityId, string Omen,
    DateTimeOffset UnlockAt, string State);

internal sealed partial class KeepsakeStore
{
    internal const string IdentityVersion = "dv10-wizard-identity-v1";
    internal const string PublicIdentityVisibility = "public_fiction";

    // The authorial fallback uses no conversation, photograph or inferred personal attributes.
    // Its edition seed distinguishes two visitors choosing the same physical wand.
    internal static WizardIdentityContent AuthorialIdentity(string editionId, KeepsakeIssueSnapshot snapshot)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(editionId));
        string[] titles = ["Kancelářský mág", "Vrchní odkladač", "Baron posledního doušku", "Inspektor nejasných znamení"];
        string[] places = ["Řád odložených povinností", "Ústav praktické nerozhodnosti", "Cech nepodepsaných kouzel", "Kolej prodloužené přestávky"];
        var shortId = Convert.ToHexString(seed.AsSpan(4, 3));
        return new($"{titles[seed[0] % titles.Length]} {shortId}", places[seed[1] % places.Length],
            "Razítkování výjimek z pravidel, která ještě nikdo nenapsal.",
            "Promění trapné ticho v řádnou přestávku na občerstvení.",
            "Kouzlo selhává při otázce: kdo to potom uklidí?",
            "Fiktivní ministerstvo: podmíněně způsobilý. Razítko vydáno během oběda.",
            $"Katalogová hůlka z přihrádky {snapshot.Slot} přijala společné mlčení jako závaznou smlouvu.",
            "Když nevíš, zakouzli přestávku.",
            "U přepážky nebyla fronta. To bylo první podezřelé znamení. Hůlka se přesto přihlásila do služby, "
            + "převzala formulář pro mimořádné okolnosti a místo podpisu nakreslila malou židli. "
            + "Ministerstvo uznalo, že tak přesný plán večera už dlouho nevidělo. Jde o komediální fikci, nikoli osobní posudek.");
    }

    internal static bool ValidIdentity(WizardIdentityContent content) =>
        Valid(content.WizardAlias, 60) && Valid(content.HouseOrGuild, 160)
        && Valid(content.DubiousSpecialization, 160) && Valid(content.SignatureAbility, 160)
        && Valid(content.Weakness, 160) && Valid(content.MinistryAssessment, 160)
        && Valid(content.WandBond, 160) && Valid(content.Motto, 120) && Valid(content.MicroStory, 900);

    private static bool Valid(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private void EnsureWizardIdentity(string id, Document document, string editionId, KeepsakeStory story, WizardIdentityContent? generatedIdentity = null)
    {
        if (document.WizardIdentities.Any(item => item.EditionId == editionId)) { return; }
        var generated = generatedIdentity is not null && ValidIdentity(generatedIdentity);
        var content = generated ? generatedIdentity! : AuthorialIdentity(editionId, document.Snapshot);
        if (!ValidIdentity(content)) { throw new KeepsakeException("wizard_identity_invalid"); }
        var created = Now;
        var identityId = NewId();
        document.WizardIdentities.Add(new(identityId, id, editionId, document.Snapshot.OutcomeId,
            document.Snapshot.CatalogId, content,
            ["confirmed_outcome", "catalog:" + document.Snapshot.CatalogVersion, (generated ? "provider_validated:" : "authorial_fallback:") + IdentityVersion],
            IdentityVersion, PublicIdentityVisibility, created));
        document.Prophecies.Add(new(identityId, editionId, "Dnes ještě zazní slavné poslední slovo: jenom jedno.",
            "Ještě dnes ti někdo vysvětlí, že už opravdu odchází. Potkáš ho u baru dvakrát.",
            "Ministerstvo předvídání si pro jistotu objednalo také.", created, created.AddMinutes(15), document.ProphecyUnlockReason));
    }

    private static WizardIdentityCard ToCard(WizardIdentity identity) => new(identity.IdentityId,
        identity.KeepsakeId, identity.EditionId, identity.CatalogWandId, identity.Content.WizardAlias,
        identity.Content.HouseOrGuild, identity.Content.DubiousSpecialization, identity.Content.SignatureAbility,
        identity.Content.Weakness, identity.Content.MinistryAssessment, identity.Content.WandBond,
        identity.Content.Motto, identity.Content.MicroStory, "Dnes ještě zazní slavné poslední slovo: jenom jedno.",
        identity.ContentVersion, identity.Visibility);

    private ProphecyView OwnerProphecy(StoredProphecy prophecy)
    {
        var unlocked = prophecy.UnlockReason is not null || Now >= prophecy.UnlockAt;
        return new(prophecy.IdentityId, prophecy.EditionId, prophecy.Omen, prophecy.CreatedAt,
            prophecy.UnlockAt, unlocked ? "revealed" : "locked", prophecy.UnlockReason ?? "after_15_minutes",
            unlocked ? prophecy.Revelation : null, unlocked ? prophecy.Afterword : null);
    }

    internal IReadOnlyList<WizardIdentityCard> ListPublicWizardIdentities(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT document,published_edition_id FROM dv10_keepsakes WHERE device_id=$d AND evening_id=$e AND deleted=0 AND published_edition_id IS NOT NULL ORDER BY published_at_ticks DESC LIMIT 200",
                ("$d", deviceId), ("$e", eveningId));
            using var reader = command.ExecuteReader();
            var results = new List<WizardIdentityCard>();
            while (reader.Read())
            {
                var document = JsonSerializer.Deserialize<Document>(reader.GetString(0), Json)!;
                var editionId = reader.GetString(1);
                if (!document.Editions.Any(edition => edition.Id == editionId && edition.PublicationState == "ready")) { continue; }
                results.AddRange(document.WizardIdentities.Where(identity => identity.EditionId == editionId
                    && identity.Visibility == PublicIdentityVisibility).Select(ToCard));
            }
            return results;
        }
    }

    internal WizardIdentityCard? GetPublicWizardIdentity(string identityId, string eveningId, string deviceId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT document,published_edition_id FROM dv10_keepsakes WHERE device_id=$d AND evening_id=$e AND deleted=0 "
                + "AND published_edition_id IS NOT NULL AND EXISTS(SELECT 1 FROM json_each(document,'$.wizardIdentities') "
                + "WHERE json_extract(value,'$.identityId')=$identity) LIMIT 1",
                ("$d", deviceId), ("$e", eveningId), ("$identity", identityId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { return null; }
            var document = JsonSerializer.Deserialize<Document>(reader.GetString(0), Json)!;
            var editionId = reader.GetString(1);
            if (!document.Editions.Any(edition => edition.Id == editionId && edition.PublicationState == "ready")) { return null; }
            var identity = document.WizardIdentities.SingleOrDefault(item => item.IdentityId == identityId
                && item.EditionId == editionId && item.Visibility == PublicIdentityVisibility);
            return identity is null ? null : ToCard(identity);
        }
    }

    internal IReadOnlyList<OperatorProphecy> ListOperatorProphecies(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT document FROM dv10_keepsakes WHERE device_id=$d AND evening_id=$e AND deleted=0 ORDER BY rowid DESC LIMIT 200",
                ("$d", deviceId), ("$e", eveningId));
            using var reader = command.ExecuteReader();
            var results = new List<OperatorProphecy>();
            while (reader.Read())
            {
                var document = JsonSerializer.Deserialize<Document>(reader.GetString(0), Json)!;
                results.AddRange(document.Prophecies.Where(prophecy => document.Editions.Any(edition =>
                    edition.Id == prophecy.EditionId && edition.PublicationState != "private_history"))
                    .Select(prophecy => new OperatorProphecy(document.Snapshot.OutcomeId, prophecy.IdentityId,
                        prophecy.Omen, prophecy.UnlockAt, OwnerProphecy(prophecy).State)));
            }
            return results;
        }
    }

    internal int RevealProphecy(string deviceId, string eveningId, string outcomeId) =>
        UnlockProphecies(deviceId, eveningId, outcomeId, "operator");

    internal int UnlockEveningProphecies(string deviceId, string eveningId) =>
        UnlockProphecies(deviceId, eveningId, null, "evening_ended");

    private int UnlockProphecies(string deviceId, string eveningId, string? outcomeId, string reason)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction,
                "SELECT id,document FROM dv10_keepsakes WHERE device_id=$d AND evening_id=$e AND deleted=0",
                ("$d", deviceId), ("$e", eveningId));
            using var reader = command.ExecuteReader();
            var documents = new List<(string Id, Document Document)>();
            while (reader.Read()) { documents.Add((reader.GetString(0), JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!)); }
            reader.Close();
            var changed = 0;
            foreach (var (id, document) in documents)
            {
                if (outcomeId is not null && document.Snapshot.OutcomeId != outcomeId) { continue; }
                var needsSave = document.ProphecyUnlockReason is null;
                document.ProphecyUnlockReason ??= reason;
                for (var index = 0; index < document.Prophecies.Count; index++)
                {
                    if (document.Prophecies[index].UnlockReason is not null) { continue; }
                    document.Prophecies[index] = document.Prophecies[index] with { UnlockReason = reason };
                    changed++;
                    needsSave = true;
                }
                if (!needsSave) { continue; }
                document.Revision++;
                Save(connection, transaction, id, document);
            }
            transaction.Commit();
            return changed;
        }
    }

    private sealed record PendingIdentityEvent(string KeepsakeId, string IdentityId, string EditionId,
        string DeviceId, string EveningId);

    internal void PublishIdentityEvents(EveningMemoryStore memory)
    {
        // No archive gate or SQLite transaction spans a call into the separate memory store.
        // Memory source tombstones reject publication racing with manual deletion.
        foreach (var pending in ReadPendingIdentityEvents())
        {
            var accepted = memory.RecordShowEvent(new("identity:" + pending.KeepsakeId,
                ShowMemoryEventKind.IdentityCompleted, pending.DeviceId, pending.EveningId,
                pending.KeepsakeId, 1, "confirmed_outcome_identity"));
            if (accepted) { AcknowledgeIdentityEvent(pending); }
            if (memory.ReadChronicle(pending.DeviceId, pending.EveningId) is { Provisional: false })
            {
                UnlockEveningProphecies(pending.DeviceId, pending.EveningId);
            }
        }
    }

    private IReadOnlyList<PendingIdentityEvent> ReadPendingIdentityEvents()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT id,document FROM dv10_keepsakes WHERE deleted=0 AND evening_id IS NOT NULL "
                + "AND EXISTS(SELECT 1 FROM json_each(document,'$.wizardIdentities') i WHERE NOT EXISTS "
                + "(SELECT 1 FROM json_each(document,'$.publishedIdentityEvents') a WHERE a.value=json_extract(i.value,'$.identityId')) "
                + "AND EXISTS(SELECT 1 FROM json_each(document,'$.editions') e WHERE json_extract(e.value,'$.id')=json_extract(i.value,'$.editionId') "
                + "AND json_extract(e.value,'$.publicationState')!='private_history')) LIMIT 32");
            using var reader = command.ExecuteReader();
            var pending = new List<PendingIdentityEvent>();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var document = JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!;
                foreach (var identity in document.WizardIdentities)
                {
                    if (document.PublishedIdentityEvents.Contains(identity.IdentityId)) { continue; }
                    if (!document.Editions.Any(edition => edition.Id == identity.EditionId && edition.PublicationState != "private_history")) { continue; }
                    pending.Add(new(id, identity.IdentityId, identity.EditionId, document.Snapshot.DeviceId, document.Snapshot.EveningId!));
                }
            }
            return pending;
        }
    }

    private void AcknowledgeIdentityEvent(PendingIdentityEvent pending)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Document document;
            try { document = Load(connection, transaction, pending.KeepsakeId); }
            catch (KeepsakeException exception) when (exception.Status == 404) { return; }
            if (document.Snapshot.DeviceId != pending.DeviceId || document.Snapshot.EveningId != pending.EveningId
                || !document.WizardIdentities.Any(identity => identity.IdentityId == pending.IdentityId && identity.EditionId == pending.EditionId)
                || !document.Editions.Any(edition => edition.Id == pending.EditionId && edition.PublicationState != "private_history")) { return; }
            if (!document.PublishedIdentityEvents.Add(pending.IdentityId)) { return; }
            Save(connection, transaction, pending.KeepsakeId, document);
            transaction.Commit();
        }
    }

    public WizardIdentityCard CreateLegacyWizardIdentity(string id, string session, string editionId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            var edition = document.Editions.SingleOrDefault(item => item.Id == editionId && item.PublicationState != "private_history")
                ?? throw new KeepsakeException("edition_not_found", 404);
            var existingIdentity = document.WizardIdentities.SingleOrDefault(item => item.EditionId == editionId);
            if (existingIdentity is not null) { return ToCard(existingIdentity); }
            // Never expand a previously issued immutable share/export with a later identity.
            using var shares = Command(connection, transaction,
                "SELECT count(*) FROM dv10_links WHERE keepsake_id=$id AND kind='share' AND edition_id=$edition",
                ("$id", id), ("$edition", editionId));
            if ((long)shares.ExecuteScalar()! > 0 || document.Assets.Any(asset => asset.EditionId == editionId && asset.Kind.StartsWith("export_", StringComparison.Ordinal)))
            { throw new KeepsakeException("new_edition_required", 409); }
            EnsureWizardIdentity(id, document, editionId, edition.Story);
            document.Revision++;
            Save(connection, transaction, id, document);
            transaction.Commit();
            return ToCard(document.WizardIdentities.Single(item => item.EditionId == editionId));
        }
    }
}

internal sealed class WizardIdentityMemoryWorker(KeepsakeStore store, EveningMemoryStore memory,
    ILogger<WizardIdentityMemoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { store.PublishIdentityEvents(memory); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Wizard identity event delivery deferred ({ExceptionType})", exception.GetType().Name);
            }
        }
    }
}
