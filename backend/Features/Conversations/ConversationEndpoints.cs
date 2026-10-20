using System.Text.Json;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Conversations;

internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/conversations", CreateConversationAsync);
        endpoints.MapPost("/v1/conversations/{id}/turns", RetiredConversationEngine);
        endpoints.MapPost("/v1/conversations/{id}/opening", RetiredConversationEngine);
        endpoints.MapGet("/v1/evening-memory", GetEveningMemory);
        endpoints.MapDelete("/v1/evening-memory/{id}", ClearEveningMemoryAsync);
        endpoints.MapGet("/v1/evenings", GetEvenings);
        endpoints.MapPost("/v1/evenings", CreateEveningAsync);
        endpoints.MapPut("/v1/evenings/{id}/active", ActivateEveningAsync);
        endpoints.MapPost("/v1/conversations/{id}/transcriptions", RetiredConversationEngine);
        endpoints.MapPost("/v1/conversations/{id}/speech", RetiredConversationEngine);
        endpoints.MapGet("/v1/operations/{id}", GetOperation);
        endpoints.MapGet("/v1/audio/{audioId}", GetAudio);
        endpoints.MapDelete("/v1/operations/{id}", CancelOperationAsync);
        endpoints.MapDelete("/v1/conversations/{id}", EndConversationAsync);
        return endpoints;
    }

    private static IResult RetiredConversationEngine(HttpContext context) =>
        ApiProblem.Create(context, StatusCodes.Status410Gone, "conversation_engine_upgrade_required");

    internal static async Task<IResult> CreateConversationAsync(
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        byte[]? body;
        try
        {
            body = await ReadSmallBodyAsync(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (body is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }
        CreateConversationRequest? request;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!HasRequiredAndOptionalProperties(
                    document.RootElement,
                    ["deviceId", "bootId", "generation", "catalogVersion", "locale", "consent"],
                    ["operationEpoch"])
                || document.RootElement.TryGetProperty("operationEpoch", out var operationEpoch)
                    && (operationEpoch.ValueKind != JsonValueKind.String
                        || !OperationEpoch.IsValid(operationEpoch.GetString()))
                || !document.RootElement.TryGetProperty("consent", out var consent)
                || !HasExpectedProperties(consent, ["audio", "image"])
                || !document.RootElement.TryGetProperty("generation", out var generation)
                || generation.ValueKind != JsonValueKind.Number
                || !int.TryParse(generation.GetRawText(), out _)
                || !document.RootElement.TryGetProperty("bootId", out var bootId)
                || bootId.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(bootId.GetString(), "D", out _))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            request = JsonSerializer.Deserialize<CreateConversationRequest>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request is null
            || request.DeviceId is not { Length: > 0 and <= 128 }
            || request.BootId == Guid.Empty
            || request.Generation < 0
            || !OperationEpoch.IsValid(request.OperationEpoch)
            || request.CatalogVersion is not { Length: > 0 and <= 128 }
            || request.Locale != "cs-CZ"
            || request.Consent is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request.DeviceId != deviceId)
        {
            return ApiProblem.Create(context, 403, "device_scope_mismatch");
        }

        var result = conversations.Create(deviceId, key, body, request);
        return result.Response is null
            ? ApiProblem.Create(context, result.Status, "conversation_conflict")
            : Results.Json(result.Response, statusCode: result.Status);
    }

    private static IResult GetOperation(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        var result = conversations.GetOperation(deviceId, id);
        return result.Operation is null
            ? ApiProblem.Create(
                context,
                result.Status,
                result.Status == 403 ? "device_scope_mismatch" : "operation_not_found")
            : Results.Json(result.Operation);
    }

    private static IResult GetAudio(
        string audioId,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        var result = conversations.GetAudio(deviceId, audioId);
        if (result.Wav is null)
        {
            var code = result.Status switch
            {
                403 => "device_scope_mismatch",
                410 => "audio_expired",
                _ => "audio_not_found"
            };
            return ApiProblem.Create(context, result.Status, code);
        }

        context.Response.Headers["X-Audio-Sample-Rate-Hz"] = "32000";
        context.Response.Headers["X-Audio-Channels"] = "1";
        return Results.File(result.Wav, "audio/wav");
    }

    private static async Task<IResult> CancelOperationAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key)
            || !await HasEmptyBodyAsync(context))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.CancelOperation(deviceId, id, key);
        return result.Status == 204
            ? Results.NoContent()
            : ApiProblem.Create(context, result.Status, result.Status == 403
                ? "device_scope_mismatch"
                : "operation_not_found");
    }

    private static async Task<IResult> EndConversationAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key)
            || !await HasEmptyBodyAsync(context))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = await conversations.EndConversationAfterImageAsync(deviceId, id, key, context.RequestAborted);
        return result.Status == 204
            ? Results.NoContent()
            : ApiProblem.Create(context, result.Status, result.Status switch
            {
                403 => "device_scope_mismatch",
                503 => "image_cancel_pending",
                _ => "conversation_not_found"
            });
    }

    private static IResult GetEveningMemory(HttpContext context,
        DeviceAccessStore access, ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        return Results.Ok(conversations.GetEveningMemory(deviceId));
    }

    private static IResult GetEvenings(
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        return deviceId is null
            ? ApiProblem.Create(context, 401, "access_token_required")
            : Results.Ok(conversations.ListEvenings(deviceId));
    }

    private static async Task<IResult> CreateEveningAsync(
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        byte[]? body;
        try
        {
            body = await ReadSmallBodyAsync(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (body is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        string? name;
        Guid expected;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!HasExpectedProperties(root, ["name", "expectedEveningId"])
                || root.GetProperty("name").ValueKind != JsonValueKind.String
                || !IsUuid(root, "expectedEveningId"))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            name = root.GetProperty("name").GetString();
            expected = Guid.ParseExact(root.GetProperty("expectedEveningId").GetString()!, "D");
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (!EveningMemoryStore.IsValidName(name))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.CreateEvening(deviceId, key, body, expected.ToString("D"), name!);
        return EveningMutationResponse(context, result);
    }

    private static async Task<IResult> ActivateEveningAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        if (!Guid.TryParseExact(id, "D", out var target))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        byte[]? body;
        try
        {
            body = await ReadSmallBodyAsync(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (body is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        Guid expected;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!HasExpectedProperties(document.RootElement, ["expectedEveningId"])
                || !IsUuid(document.RootElement, "expectedEveningId"))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            expected = Guid.ParseExact(
                document.RootElement.GetProperty("expectedEveningId").GetString()!, "D");
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.ActivateEvening(
            deviceId, key, body, target.ToString("D"), expected.ToString("D"));
        return EveningMutationResponse(context, result);
    }

    private static IResult EveningMutationResponse(HttpContext context, EveningMutationResult result)
    {
        return result.Response is null
            ? ApiProblem.Create(context, result.Status, result.ErrorCode ?? "evening_rejected")
            : Results.Json(result.Response, statusCode: result.Status);
    }

    private static async Task<IResult> ClearEveningMemoryAsync(string id, HttpContext context,
        DeviceAccessStore access, ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(id, "D", out var eveningId)
            || !Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out _)
            || !await HasEmptyBodyAsync(context))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        // The expected evening ID makes this mutation idempotent indefinitely.
        conversations.ClearEveningMemory(deviceId, eveningId.ToString("D"));
        return Results.NoContent();
    }

    private static bool IsUuid(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(value.GetString(), "D", out _);
    }

    private static async Task<bool> HasEmptyBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > 0)
        {
            return false;
        }

        var prefix = new byte[1];
        return await context.Request.Body.ReadAsync(prefix, context.RequestAborted) == 0;
    }

    private static async Task<byte[]?> ReadSmallBodyAsync(HttpContext context)
    {
        const int maximum = 4096;
        if (context.Request.ContentLength is 0 or > maximum)
        {
            return null;
        }

        using var content = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0)
            {
                return content.Length == 0 ? null : content.ToArray();
            }

            if (content.Length + read > maximum)
            {
                return null;
            }

            content.Write(buffer, 0, read);
        }
    }

    private static bool IsInteger(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && int.TryParse(value.GetRawText(), out _);
    }

    private static bool HasExpectedProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.Count == expected.Length;
    }

    private static bool HasRequiredAndOptionalProperties(
        JsonElement element,
        string[] required,
        string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if ((!required.Contains(property.Name, StringComparer.Ordinal)
                    && !optional.Contains(property.Name, StringComparer.Ordinal))
                || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return required.All(seen.Contains);
    }
}
