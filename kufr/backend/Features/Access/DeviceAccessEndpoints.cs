using Microsoft.AspNetCore.WebUtilities;

namespace Kufr.Backend.Features.Access;

internal static class DeviceAccessEndpoints
{
    public static IEndpointRouteBuilder MapDeviceAccess(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/device-access/token", (HttpContext context, DeviceAccessStore access) =>
        {
            if (!TryReadBearer(context, out var credential))
            {
                return ApiProblem.Create(context, 401, "credential_required");
            }

            var deviceId = access.VerifyCredential(credential);
            if (deviceId is null)
            {
                return ApiProblem.Create(context, 401, "invalid_credential");
            }

            var issued = access.IssueToken(deviceId);
            return issued is null
                ? ApiProblem.Create(context, 429, "token_exchange_limit")
                : Results.Json(issued);
        });

        return endpoints;
    }

    public static string? Authenticate(HttpContext context, DeviceAccessStore access)
    {
        return TryReadBearer(context, out var token)
            ? access.VerifyAccessToken(token)
            : null;
    }

    private static bool TryReadBearer(HttpContext context, out string token)
    {
        token = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[7..];
        return token.Length is > 0 and <= 256 && token.Trim() == token && !token.Contains(' ');
    }
}

internal static class ApiProblem
{
    public static IResult Create(HttpContext context, int status, string code)
    {
        context.Items["diagnosticCode"] = code;
        return Results.Json(
            new
            {
                type = "about:blank",
                title = ReasonPhrases.GetReasonPhrase(status),
                status,
                code,
                traceId = context.TraceIdentifier
            },
            statusCode: status,
            contentType: "application/problem+json");
    }
}
