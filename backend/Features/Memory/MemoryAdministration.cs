using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Memory;

internal static class MemoryAdministration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    internal static IEndpointRouteBuilder MapMemoryAdministration(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/admin/memory", (HttpContext c, ServerAdminAccess admin, HybridMemoryService service) =>
            admin.Authenticate(c) ? Results.Ok(service.Status) : ApiProblem.Create(c, 401, "admin_credential_required"));
        routes.MapGet("/v1/admin/memory/context", (HttpContext c, ServerAdminAccess admin, EveningMemoryStore store, HybridMemoryService service,
            string deviceId, string conversationId, string personalityId) =>
        {
            if (!admin.Authenticate(c)) { return ApiProblem.Create(c, 401, "admin_credential_required"); }
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(conversationId)
                || !PersonalityCatalog.Contains(personalityId)) { return ApiProblem.Create(c, 400, "invalid_request"); }
            var permission = store.GuestPermission(deviceId, conversationId);
            var access = new MemoryAccess(deviceId, store.GetStatus(deviceId).EveningId, conversationId, personalityId, permission);
            var records = store.LocalMemory(access);
            return Results.Json(new { identity = permission, epoch = store.MemoryEpoch,
                deletionEpoch = store.MemoryDeletionEpochFor(deviceId, conversationId),
                records = records.Take(50).ToArray(), totalRecords = records.Count, hasMore = records.Count > 50,
                memoryStatus = service.Status }, Json);
        });
        routes.MapDelete("/v1/admin/memory/scopes/{scope}/{subject}", async (string scope, string subject, HttpContext c,
            ServerAdminAccess admin, EveningMemoryStore store, ConversationStore conversations, HybridMemoryService service) =>
        {
            if (!admin.Authenticate(c)) { return ApiProblem.Create(c, 401, "admin_credential_required"); }
            try
            {
                var request = await Read<ScopeDeleteRequest>(c);
                if (string.IsNullOrWhiteSpace(request.DeviceId) || request.ExpectedEpoch < 0)
                {
                    return ApiProblem.Create(c, 400, "invalid_request");
                }
                if (!store.DeleteScopedMemory(request.DeviceId, scope, subject, request.ExpectedEpoch, out var affectedConversations))
                {
                    return ApiProblem.Create(c, 409, "memory_revision_mismatch");
                }
                service.ClearCache();
                conversations.DeleteArchivedVisualScope(request.DeviceId, scope, subject);
                foreach (var conversationId in affectedConversations)
                {
                    conversations.DeleteVisualContext(request.DeviceId, conversationId);
                }
                return Results.Ok(new { localDeletion = "complete", remoteDeletion = store.MemoryBacklog().Deleting > 0 ? "pending" : "complete" });
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
            {
                return ApiProblem.Create(c, 400, "invalid_request");
            }
        });
        routes.MapPost("/v1/admin/memory/verify", async (HttpContext c, ServerAdminAccess admin, HybridMemoryService service) =>
        {
            if (!admin.Authenticate(c)) return ApiProblem.Create(c, 401, "admin_credential_required");
            try { await service.VerifyAsync(c.RequestAborted); return Results.Ok(service.Status); }
            catch (MemoryProviderException) { return ApiProblem.Create(c, 503, "memory_unavailable"); }
        });
        routes.MapPut("/v1/admin/memory/guest", async (HttpContext c, ServerAdminAccess admin,
            ConversationStore conversations, EveningMemoryStore store, HybridMemoryService service) =>
        {
            if (!admin.Authenticate(c)) return ApiProblem.Create(c, 401, "admin_credential_required");
            try
            {
                var r = await Read<GuestRequest>(c);
                if (string.IsNullOrWhiteSpace(r.DeviceId) || string.IsNullOrWhiteSpace(r.ConversationId)
                    || string.IsNullOrWhiteSpace(r.GuestId) || string.IsNullOrWhiteSpace(r.IdentitySource) || r.ExpectedVersion < 0)
                    return ApiProblem.Create(c, 400, "invalid_request");
                if (conversations.IsLabConversation(r.DeviceId, r.ConversationId))
                { return ApiProblem.Create(c, 403, "lab_hardware_guest_binding_forbidden"); }
                // Binding a new identity is allowed only before speech starts.
                // Revocation can immediately end a running context.
                if (!conversations.CanBindGuest(r.DeviceId, r.ConversationId))
                {
                    if (r.LongTermAllowed || r.CloudExportAllowed) return ApiProblem.Create(c, 409, "conversation_active");
                    conversations.InvalidateMemoryContext(r.DeviceId, r.ConversationId);
                }
                var permission = store.SetGuestPermission(r.DeviceId, r.ConversationId, r.GuestId, r.IdentitySource,
                    r.IdentityVerified, r.LongTermAllowed, r.CloudExportAllowed, r.ExpectedVersion);
                service.ClearCache();
                return permission is null ? ApiProblem.Create(c, 409, "memory_revision_mismatch") : Results.Ok(permission);
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
            { return ApiProblem.Create(c, 400, "invalid_request"); }
        });
        routes.MapPut("/v1/admin/memory/records/{id}", async (string id, HttpContext c, ServerAdminAccess admin,
            EveningMemoryStore store, ConversationStore conversations, HybridMemoryService service) =>
        {
            if (!admin.Authenticate(c)) return ApiProblem.Create(c, 401, "admin_credential_required");
            try
            {
                var r = await Read<RecordRequest>(c);
                if (string.IsNullOrWhiteSpace(r.DeviceId) || string.IsNullOrWhiteSpace(r.EveningId)
                    || string.IsNullOrWhiteSpace(r.ConversationId) || string.IsNullOrWhiteSpace(r.Text)
                    || string.IsNullOrWhiteSpace(r.SourceReference) || r.ExpectedRevision < 0 || r.ExpectedEpoch is < 0)
                    return ApiProblem.Create(c, 400, "invalid_request");
                if (!PersonalityCatalog.Contains(r.PersonalityId)) return ApiProblem.Create(c, 400, "invalid_personality");
                var permission = store.GuestPermission(r.DeviceId, r.ConversationId);
                var record = store.WriteMemory(new(r.DeviceId, r.EveningId, r.ConversationId, r.PersonalityId, permission),
                    new(id, r.Scope, r.Kind, r.Text, r.SourceReference, r.ExpectedRevision, r.ExpectedEpoch));
                if (record is null) return ApiProblem.Create(c, 409, "memory_revision_or_permission_mismatch");
                service.ClearCache(); conversations.InvalidateMemoryContext(r.DeviceId);
                return Results.Ok(new { record.MemoryId, record.Revision, record.Scope, record.IsActive });
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
            { return ApiProblem.Create(c, 400, "invalid_request"); }
        });
        routes.MapDelete("/v1/admin/memory/records/{id}", async (string id, HttpContext c, ServerAdminAccess admin,
            EveningMemoryStore store, ConversationStore conversations, HybridMemoryService service) =>
        {
            if (!admin.Authenticate(c)) return ApiProblem.Create(c, 401, "admin_credential_required");
            try
            {
                var r = await Read<DeleteRequest>(c);
                if (string.IsNullOrWhiteSpace(r.DeviceId) || r.ExpectedRevision < 1) return ApiProblem.Create(c, 400, "invalid_request");
                if (!store.DeleteMemory(r.DeviceId, id, r.ExpectedRevision)) return ApiProblem.Create(c, 409, "memory_revision_mismatch");
                service.ClearCache(); conversations.InvalidateMemoryContext(r.DeviceId);
                return Results.Ok(new { localDeletion = "complete", remoteDeletion = store.MemoryBacklog().Deleting > 0 ? "pending" : "complete" });
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
            { return ApiProblem.Create(c, 400, "invalid_request"); }
        });
        return routes;
    }
    private static async Task<T> Read<T>(HttpContext c)
    {
        var buffer = new byte[4097]; var used = 0;
        while (used < buffer.Length) { var n = await c.Request.Body.ReadAsync(buffer.AsMemory(used), c.RequestAborted); if (n == 0) break; used += n; }
        if (used > 4096) throw new InvalidDataException("Memory command too large.");
        using var document = JsonDocument.Parse(buffer.AsMemory(0, used));
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
            throw new InvalidDataException("Duplicate memory field.");
        return JsonSerializer.Deserialize<T>(document.RootElement, Json) ?? throw new InvalidDataException("Invalid memory command.");
    }
    private sealed record GuestRequest([property: JsonRequired] string DeviceId, [property: JsonRequired] string ConversationId, [property: JsonRequired] string GuestId, [property: JsonRequired] string IdentitySource,
        [property: JsonRequired] bool IdentityVerified, [property: JsonRequired] bool LongTermAllowed, [property: JsonRequired] bool CloudExportAllowed, [property: JsonRequired] long ExpectedVersion);
    private sealed record RecordRequest([property: JsonRequired] string DeviceId, [property: JsonRequired] string EveningId, [property: JsonRequired] string ConversationId, [property: JsonRequired] string PersonalityId,
        [property: JsonRequired] MemoryScope Scope, [property: JsonRequired] MemoryKind Kind, [property: JsonRequired] string Text, [property: JsonRequired] string SourceReference, [property: JsonRequired] long ExpectedRevision, long? ExpectedEpoch);
    private sealed record DeleteRequest([property: JsonRequired] string DeviceId, [property: JsonRequired] long ExpectedRevision);
    private sealed record ScopeDeleteRequest([property: JsonRequired] string DeviceId, [property: JsonRequired] long ExpectedEpoch);
}

internal static class MemoryProvisioning
{
    internal static async Task<int> RunAsync(IConfiguration config, OperationJournal journal, string[] args, CancellationToken token)
    {
        var options = HybridMemoryOptions.Read(config, journal);
        var execute = args.Contains("--execute"); var create = args.Contains("--create");
        if (!execute)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode = "dry-run", options.AppNamespace, options.Environment,
                personal = options.PersonalStoreId.Length > 0 ? "verify_existing" : create ? "create_requested" : "missing",
                motifs = options.MotifStoreId.Length > 0 ? "verify_existing" : create ? "create_requested" : "missing",
                networkRequests = 0, exports = 0
            }));
            return 0;
        }
        using var provider = new OpenAiVectorMemoryProvider(config, options);
        if (!provider.HasKey) { Console.Error.WriteLine("OPENAI_API_KEY is required for --execute."); return 2; }
        try
        {
            foreach (var (scope, id, setting) in new[]
            {
                (MemoryScope.Personal, options.PersonalStoreId, "memory_personal_store"),
                (MemoryScope.EveningMotif, options.MotifStoreId, "memory_motif_store")
            })
            {
                var storeId = id;
                if (storeId.Length == 0)
                {
                    if (!create) { Console.Error.WriteLine("Configure both existing store IDs or explicitly use --create --execute."); return 2; }
                    storeId = await provider.CreateStoreAsync(scope, token);
                    // Persist immediately: an interrupted second creation must
                    // not recreate the already provisioned first resource.
                    journal.WriteSetting(setting, storeId);
                }
                await provider.VerifyStoreAsync(storeId, scope, token); journal.WriteSetting(setting, storeId);
                Console.WriteLine(JsonSerializer.Serialize(new { scope = scope.ToString(), storeId, verified = true, exportedRecords = 0 }));
            }
            return 0;
        }
        catch (MemoryProviderException e) { Console.Error.WriteLine("Provisioning failed: " + e.Code); return 2; }
        catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { Console.Error.WriteLine("Provisioning failed: invalid provider response."); return 2; }
    }
}
