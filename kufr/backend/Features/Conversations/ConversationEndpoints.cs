using System.Text.Json;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Conversations;

internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/conversations", CreateConversationAsync);
        endpoints.MapPost("/v1/conversations/{id}/turns", SubmitTurnAsync);
        endpoints.MapGet("/v1/operations/{id}", GetOperation);
        return endpoints;
    }

    private static async Task<IResult> CreateConversationAsync(
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

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length is 0 or > 4096)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var body = buffer.ToArray();
        CreateConversationRequest? request;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!HasExpectedProperties(
                    document.RootElement,
                    ["deviceId", "bootId", "generation", "catalogVersion", "locale", "consent"])
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

    private static async Task<IResult> SubmitTurnAsync(
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

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length is 0 or > 4096)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var body = buffer.ToArray();
        TextTurnRequest? request;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!HasExpectedProperties(
                    root,
                    ["clientTurnId", "bootId", "generation", "text", "readyMask", "deviceRevision", "catalogVersion"])
                || !IsUuid(root, "clientTurnId")
                || !IsUuid(root, "bootId")
                || !IsInteger(root, "generation")
                || !IsInteger(root, "readyMask")
                || !IsInteger(root, "deviceRevision"))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            request = JsonSerializer.Deserialize<TextTurnRequest>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request is null
            || request.ClientTurnId == Guid.Empty
            || request.BootId == Guid.Empty
            || request.Generation < 0
            || request.Text is not { Length: > 0 and <= 1000 }
            || string.IsNullOrWhiteSpace(request.Text)
            || request.ReadyMask is < 0 or > 63
            || request.DeviceRevision < 0
            || request.CatalogVersion is not { Length: > 0 and <= 128 })
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.SubmitTurn(deviceId, id, key, body, request);
        return result.OperationId is null
            ? ApiProblem.Create(context, result.Status, result.ErrorCode ?? "turn_rejected")
            : Results.Json(new { operationId = result.OperationId }, statusCode: result.Status);
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

    private static bool IsUuid(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(value.GetString(), "D", out _);
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
}
