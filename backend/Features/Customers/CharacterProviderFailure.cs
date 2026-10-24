using System.ClientModel;
using System.Text.Json;

namespace Kufr.Backend.Features.Customers;

// Technical classifications only. Never expose provider messages or bodies.
internal static class CharacterProviderFailure
{
    internal static bool IsTransient(Exception error) => error is HttpRequestException or OperationCanceledException
        || error is ClientResultException response && (response.Status is 408 or 429 || response.Status >= 500);

    internal static int? Status(Exception error) => error is ClientResultException { Status: >= 400 and <= 599 } response
        ? response.Status : null;

    internal static string Code(Exception error) => error switch
    {
        CharacterException character => character.Code,
        OperationCanceledException => "character_provider_timeout",
        HttpRequestException => "character_provider_transport_error",
        ClientResultException { Status: >= 400 and <= 599 } response => "openai_http_" + response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonException or InvalidDataException or KeyNotFoundException or FormatException or OverflowException => "character_provider_invalid_response",
        _ => "character_provider_error"
    };
}
