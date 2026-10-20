using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private readonly Dictionary<string, ImageCompletion> imageCompletions = new(StringComparer.Ordinal);

    internal Task WaitForVisualWorkAsync(string deviceId, string conversationId)
    {
        lock (gate)
        {
            return imageCompletions.TryGetValue(conversationId, out var work) && work.DeviceId == deviceId
                ? work.Completion.Task : Task.CompletedTask;
        }
    }

    internal VisualResponseRevision ReadVisualResponseRevision(string deviceId, string conversationId)
    {
        lock (gate)
        {
            return conversations.TryGetValue(conversationId, out var conversation) && conversation.DeviceId == deviceId
                ? new(conversation.VisualResponseGeneration, conversation.VisualResponseChanged.Task, conversation.RevokedVisualRevision)
                : new(-1, Task.CompletedTask);
        }
    }

    // The caller holds the stream writer. Check, commit and begin publication
    // atomically with DELETE, before its cancellation marker can run.
    internal Task PublishVisualResponse(string deviceId, string conversationId, long generation, Func<Task> publish)
    {
        lock (gate)
        {
            if (!conversations.TryGetValue(conversationId, out var conversation)
                || conversation.DeviceId != deviceId || conversation.VisualResponseGeneration != generation)
            { throw new Kufr.Backend.Features.Realtime.VisualResponseRevokedException(); }
            return publish();
        }
    }

    private static void RevokeVisualResponses(ConversationEntry conversation, int revision)
    {
        var previous = conversation.VisualResponseChanged;
        conversation.VisualResponseGeneration++;
        conversation.RevokedVisualRevision = Math.Max(conversation.RevokedVisualRevision, revision);
        conversation.VisualResponseChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    internal MutationResult DiscardVisualRevision(string deviceId, string conversationId, CostumeImageUpload correlation)
    {
        lock (gate)
        {
            var error = ValidateCostumeAccess(deviceId, conversationId, correlation, requireConsent: false);
            if (error is not null) { return new(error.Status, false); }
            var conversation = conversations[conversationId];
            if (correlation.Revision is not { } revision || revision > 1_000_000) { return new(400, false); }
            // Every successful DELETE emits a playback barrier, including a
            // retry whose prior response was lost. A stale revision never clears
            // a newer image, but still lets the client release its local mute.
            RevokeVisualResponses(conversation, revision);
            if (revision < conversation.VisualRevision) { return new(204, false); }
            // Also fence a DELETE that overtakes the corresponding upload.
            conversation.VisualRevision = revision;
            conversation.VisualImageId = null;
            conversation.Costume = null;
            conversation.CostumeCleared = false;
            conversation.ManualCostume = false;
            if (conversation.ImageOperationId is { } operationId)
            {
                journal.CancelOperation(deviceId, operationId, Guid.NewGuid());
                operations.Remove(operationId);
                if (activePaidCalls.TryGetValue(operationId, out var call)) { call.Cancellation.Cancel(); }
            }
            return new(204, true);
        }
    }

    internal async Task<MutationResult> EndConversationAfterImageAsync(string deviceId, string id, Guid key,
        CancellationToken requestAborted = default, TimeSpan? waitTimeout = null)
    {
        Task? pending = null;
        MutationResult result;
        lock (gate)
        {
            if (imageCompletions.TryGetValue(id, out var completion) && completion.DeviceId == deviceId)
            {
                pending = completion.Completion.Task;
            }
            result = EndConversation(deviceId, id, key);
        }
        if (result.Status != 204 || pending is null) { return result; }
        try
        {
            // Stop is already recorded, but only acknowledge a safe fresh start
            // once the old image has released its shared provider slot.
            await pending.WaitAsync(waitTimeout ?? TimeSpan.FromSeconds(5), requestAborted);
            return result;
        }
        catch (TimeoutException) { return new(503, false); }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { return new(503, false); }
    }

    internal SubmitTurnResult SubmitImage(string deviceId, string id, Guid key, CostumeImageUpload upload)
    {
        if (IsLabConversation(deviceId, id)) { return new(403, null, "lab_hardware_image_forbidden"); }
        if (!CostumeJpeg.IsValid(upload.Jpeg) || !CanDecodeImage(upload.Jpeg))
        { return new(400, null, "invalid_image"); }
        var route = $"/v1/conversations/{id}/image";
        var hash = ImageHash(upload, upload.Jpeg);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpired(now);
            var previous = journal.FindExisting(deviceId, route, key, hash);
            if (previous is not null)
            {
                return previous.Conflict ? new(409, null, "idempotency_conflict") : new(202, previous.OperationId, null);
            }
            var error = ValidateCostumeAccess(deviceId, id, upload, requireConsent: true);
            if (error is not null) { return error; }
            var conversation = conversations[id];
            if (imageCompletions.ContainsKey(id)) { return new(409, null, "image_analysis_pending"); }
            if (conversation.CostumeCleared && upload.Revision is null) { return new(409, null, "costume_context_discarded"); }
            if (conversation.ManualCostume && conversation.Costume is null)
            { return new(409, null, "costume_context_expired"); }
            if (upload.CapturedAt > now.AddMinutes(1)) { return new(400, null, "invalid_capture_time"); }
            if (upload.Revision is { } requestedRevision && requestedRevision <= conversation.VisualRevision)
            { return new(409, null, "image_revision_superseded"); }
            var simulated = twinFakeInputs?.Allows(deviceId) == true;
            var paid = !simulated && (vision?.Available == true || dialogRunner?.Enabled == true);
            if (!simulated && vision is not null && !vision.Available)
            { return new(503, null, "vision_provider_unavailable"); }
            if (paid && !(vision is not null ? vision.TryAcquire() : dialogRunner!.TryAcquire()))
            { return new(503, null, "vision_busy"); }
            var queued = false;
            try
            {
                var correlation = new TurnCorrelation(deviceId, upload.BootId, id, upload.ClientTurnId,
                    upload.Generation, upload.DeviceRevision);
                var reservation = journal.Reserve(deviceId, route, key, hash, correlation, kind: "image",
                    consumesTurn: false, estimatedCostMicroUsd: paid ? CostumeImageClient.ReservationMicroUsd : 0);
                if (reservation.Conflict) { return new(409, null, "idempotency_conflict"); }
                if (reservation.Existing) { return new(202, reservation.OperationId, null); }
                var imageId = upload.ImageId ?? Guid.NewGuid().ToString("D");
                var revision = upload.Revision ?? checked(conversation.VisualRevision + 1);
                if (!journal.ArchiveVisualMedia(new(imageId, deviceId, id, revision, upload.Source,
                    upload.CapturedAt ?? now, upload.Jpeg, eveningMemory?.GetStatus(deviceId).EveningId,
                    eveningMemory?.GuestPermission(deviceId, id)?.GuestId)))
                {
                    journal.MarkFailed(reservation.OperationId);
                    return new(409, null, "image_identity_conflict");
                }
                conversation.VisualImageId = imageId;
                conversation.VisualRevision = revision;
                if (upload.Revision.HasValue)
                {
                    conversation.ManualCostume = false;
                    conversation.CostumeCleared = false;
                    conversation.Costume = null;
                }
                conversation.ImageOperationId = reservation.OperationId;
                if (!paid)
                {
                    var context = new CostumeContext("Testovaci kostym; fake fotografie se neanalyzuje.", "", "high", "fake", conversation.AbsoluteExpiresAt);
                    if (!conversation.ManualCostume) { conversation.Costume = context; }
                    operations.Add(reservation.OperationId, new(reservation.OperationId, "image", "completed", correlation, conversation.Costume, null));
                    journal.MarkCompleted(reservation.OperationId);
                    return new(202, reservation.OperationId, null);
                }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(vision?.Stopping ?? dialogRunner!.Stopping);
                cancellation.CancelAfter(TimeSpan.FromSeconds(45));
                activePaidCalls.Add(reservation.OperationId, new(deviceId, id, cancellation, IsImageAnalysis: true));
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                imageCompletions.Add(id, new(deviceId, completion));
                var ownedJpeg = upload.Jpeg.ToArray();
                _ = Task.Run(() => ProcessImageAsync(reservation.OperationId, correlation, imageId, revision, ownedJpeg, cancellation, completion));
                queued = true;
                return new(202, reservation.OperationId, null);
            }
            finally
            {
                if (paid && !queued) { ReleaseVisionSlot(); }
            }
        }
    }

    internal GetOperationResult CorrectCostume(string deviceId, string id, Guid key, CostumeImageUpload correlation,
        string description)
    {
        if (IsLabConversation(deviceId, id)) { return new(403, null); }
        if (!CostumeImageClient.ValidText(description, 240)) { return new(400, null); }
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            var error = ValidateCostumeAccess(deviceId, id, correlation, requireConsent: false);
            if (error is not null) { return new(error.Status, null); }
            var route = $"/v1/conversations/{id}/costume-context";
            var hash = ImageHash(correlation, Encoding.UTF8.GetBytes(description));
            var previous = journal.FindExisting(deviceId, route, key, hash);
            if (previous is not null)
            {
                return previous.Conflict ? new(409, null) : GetOperation(deviceId, previous.OperationId);
            }
            var conversation = conversations[id];
            if (conversation.CostumeCorrectionCount >= 8) { return new(429, null); }
            var reservation = journal.Reserve(deviceId, route, key, hash,
                new(deviceId, correlation.BootId, id, correlation.ClientTurnId, correlation.Generation, correlation.DeviceRevision),
                kind: "costume", consumesTurn: false);
            if (reservation.Conflict) { return new(409, null); }
            conversation.ManualCostume = true;
            conversation.CostumeCleared = description.Length == 0;
            conversation.CostumeCorrectionCount++;
            conversation.VisualRevision++;
            var context = new CostumeContext(description, "", "high", "manual", conversation.AbsoluteExpiresAt);
            // The empty manual receipt acknowledges a clear; it is never an active prompt cue.
            conversation.Costume = conversation.CostumeCleared ? null : context;
            if (conversation.CostumeCleared) { RevokeVisualResponses(conversation, conversation.VisualRevision); }
            if (conversation.ImageOperationId is { } imageId)
            {
                if (conversation.CostumeCleared)
                {
                    // Suppress publication without interrupting usage settlement or the cleanup barrier.
                    journal.CancelOperation(deviceId, imageId, key);
                    operations.Remove(imageId);
                }
                else if (operations.TryGetValue(imageId, out var imageOperation))
                {
                    operations[imageId] = imageOperation with { Result = context };
                }
            }
            var operation = new TurnOperation(reservation.OperationId, "costume", "completed",
                new(deviceId, correlation.BootId, id, correlation.ClientTurnId, correlation.Generation, correlation.DeviceRevision), context, null);
            operations.Add(operation.OperationId, operation);
            journal.MarkCompleted(operation.OperationId);
            return new(200, operation);
        }
    }

    private SubmitTurnResult? ValidateCostumeAccess(string device, string id, CostumeImageUpload upload, bool requireConsent)
    {
        if (IsLabConversation(device, id)) { return new(403, null, "lab_hardware_image_forbidden"); }
        if (!conversations.TryGetValue(id, out var conversation)) { return new(404, null, "conversation_not_found"); }
        if (conversation.DeviceId != device) { return new(403, null, "device_scope_mismatch"); }
        if (requireConsent && !conversation.ImageConsented && !upload.ExplicitConsent) { return new(403, null, "image_consent_required"); }
        if (conversation.BootId != upload.BootId || conversation.Generation != upload.Generation
            || conversation.CatalogVersion != upload.CatalogVersion)
        { return new(409, null, "conversation_version_mismatch"); }
        if (upload.ExplicitConsent) { conversation.ImageConsented = true; }
        return null;
    }

    private static string ImageHash(CostumeImageUpload upload, ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            upload.ClientTurnId, upload.BootId, upload.Generation, upload.DeviceRevision, upload.CatalogVersion,
            upload.ImageId, upload.Source, upload.CapturedAt, upload.Revision, upload.ExplicitConsent })));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task ProcessImageAsync(string operationId, TurnCorrelation correlation, string imageId, int revision, byte[] jpeg,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !journal.MarkRunning(operationId))
                {
                    journal.SettleCost(operationId, 0);
                    return;
                }
            }
            var response = vision is not null ? await vision.AnalyzeAsync(jpeg, cancellation.Token)
                : await dialogRunner!.AnalyzeImageAsync(jpeg, cancellation.Token);
            lock (gate)
            {
                if (!journal.SettleCost(operationId, response.CostMicroUsd)) { journal.MarkFailed(operationId); return; }
                CleanupExpired(clock.GetUtcNow());
                if (cancellation.IsCancellationRequested || !conversations.TryGetValue(correlation.ConversationId, out var conversation))
                { journal.MarkFailed(operationId); return; }
                if (conversation.VisualImageId != imageId
                    || conversation.VisualRevision != revision && !conversation.ManualCostume
                    || conversation.CostumeCleared
                    || !journal.CompleteVisualMedia(correlation.DeviceId, correlation.ConversationId, imageId, revision, response.Description))
                { journal.MarkFailed(operationId); return; }
                var context = new CostumeContext(response.Description, response.PossibleCharacter, response.Uncertainty,
                    "photo", conversation.AbsoluteExpiresAt);
                if (!conversation.ManualCostume) { conversation.Costume = context; }
                // A manual correction also governs a late poll result.
                operations.Add(operationId, new(operationId, "image", "completed", correlation,
                    conversation.Costume, null));
                if (!journal.MarkCompleted(operationId)) { operations.Remove(operationId); }
            }
        }
        catch (Exception)
        {
            lock (gate) { journal.MarkFailed(operationId); }
        }
        finally
        {
            try
            {
                CryptographicOperations.ZeroMemory(jpeg);
                lock (gate)
                {
                    activePaidCalls.Remove(operationId);
                    cancellation.Dispose();
                    ReleaseVisionSlot();
                    imageCompletions.Remove(correlation.ConversationId);
                    completion.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                // A cleanup failure must never produce a false 204 barrier ack.
                completion.TrySetException(exception);
                throw;
            }
        }
    }

    private void ReleaseVisionSlot()
    {
        if (vision is not null) { vision.Release(); }
        else { dialogRunner!.Release(); }
    }

    private static bool CanDecodeImage(byte[] jpeg)
    {
        try
        {
            using var bitmap = SkiaSharp.SKBitmap.Decode(jpeg);
            return bitmap is { Width: > 0 and <= 640, Height: > 0 and <= 640 };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { return false; }
    }

    internal void DeleteVisualContext(string deviceId, string conversationId)
    {
        lock (gate)
        {
            journal.DeleteVisualMedia(deviceId, conversationId);
            if (!conversations.TryGetValue(conversationId, out var conversation) || conversation.DeviceId != deviceId)
            { return; }
            conversation.VisualRevision++;
            conversation.VisualImageId = null;
            conversation.Costume = null;
            conversation.CostumeCleared = true;
            // A provider may have already consumed the image. Ending that context
            // is the only honest way to enforce deletion immediately.
            InvalidateMemoryContext(deviceId, conversationId);
        }
    }

    internal void DeleteArchivedVisualScope(string deviceId, string scope, string subject)
    {
        lock (gate)
        {
            foreach (var conversationId in journal.VisualConversations(deviceId, scope, subject))
            { DeleteVisualContext(deviceId, conversationId); }
        }
    }

    public (int Revision, string Context) ReadVisualContext(string deviceId, string conversationId)
    {
        lock (gate)
        {
            if (!conversations.TryGetValue(conversationId, out var conversation) || conversation.DeviceId != deviceId)
            { return (0, ""); }
            return (conversation.VisualRevision, CostumePrompt(conversation));
        }
    }

    public VisualMediaRecord? ReadVisualImage(string deviceId, string conversationId)
    {
        lock (gate)
        {
            if (!conversations.TryGetValue(conversationId, out var conversation) || conversation.DeviceId != deviceId
                || conversation.VisualImageId is not { } imageId || conversation.Costume is null
                || conversation.CostumeCleared)
            { return null; }
            return journal.ReadVisualMedia(deviceId, conversationId, imageId);
        }
    }

    private string CostumePrompt(ConversationEntry conversation) => conversation.Costume is { } context
        && context.ExpiresAt > clock.GetUtcNow() ? context.Prompt : "";

    private sealed record ImageCompletion(string DeviceId, TaskCompletionSource Completion);
}

internal sealed record VisualResponseRevision(long Generation, Task Changed, int VisualRevision = 0);
