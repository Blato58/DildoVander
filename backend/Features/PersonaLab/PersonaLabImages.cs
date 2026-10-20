using System.Security.Claims;
using System.Security.Cryptography;
using Kufr.Backend.Features.Conversations;
using SkiaSharp;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed record LabImageRequest(string MimeType, string DataBase64, bool Consent);

// Consent-scoped input, never part of the transcript, archive, or diagnostics.
internal sealed class PersonaLabImages(TimeProvider clock) : IDisposable
{
    private sealed record Entry(string Owner, string Id, byte[] Bytes, DateTimeOffset ExpiresAt);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> images = new(StringComparer.Ordinal);

    internal bool HasImage(string owner, string id)
    {
        lock (gate)
        {
            Expire();
            return images.TryGetValue(id, out var image) && image.Owner == owner;
        }
    }

    internal void Put(string owner, string id, LabImageRequest request)
    {
        if (!request.Consent || request.MimeType != "image/jpeg" || request.DataBase64 is not { Length: > 0 and <= 60_000 })
        {
            throw new LabException(400, "lab_image_consent_or_format");
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.DataBase64); }
        catch (FormatException) { throw new LabException(400, "lab_image_format"); }
        try
        {
            if (bytes.Length > 45_000 || !CostumeJpeg.IsValid(bytes))
            {
                throw new LabException(400, "lab_image_format");
            }
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is not { Width: > 0 and <= 640, Height: > 0 and <= 640 })
            {
                throw new LabException(400, "lab_image_format");
            }
            lock (gate)
            {
                Expire();
                if (images.Count >= 100 && !images.ContainsKey(id))
                {
                    throw new LabException(429, "lab_image_capacity");
                }
                if (images.TryGetValue(id, out var old) && old.Owner != owner)
                {
                    throw new LabException(404, "lab_session_not_found");
                }
                if (old is not null) { CryptographicOperations.ZeroMemory(old.Bytes); }
                images[id] = new(owner, Guid.NewGuid().ToString("N"), bytes.ToArray(), clock.GetUtcNow().AddMinutes(15));
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal VisualMediaRecord? Read(string owner, string id)
    {
        lock (gate)
        {
            Expire();
            return images.TryGetValue(id, out var image) && image.Owner == owner
                ? new(image.Id, "persona-lab", id, 1, "phone", clock.GetUtcNow(), image.Bytes.ToArray()) : null;
        }
    }

    internal void Clear(string owner, string id)
    {
        lock (gate)
        {
            if (images.TryGetValue(id, out var image) && image.Owner == owner)
            {
                images.Remove(id);
                CryptographicOperations.ZeroMemory(image.Bytes);
            }
        }
    }

    private void Expire()
    {
        foreach (var id in images.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
        {
            var image = images[id];
            images.Remove(id);
            CryptographicOperations.ZeroMemory(image.Bytes);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var image in images.Values) { CryptographicOperations.ZeroMemory(image.Bytes); }
            images.Clear();
        }
    }
}

internal static class PersonaLabImageEndpoints
{
    internal static void MapPersonaLabImages(this RouteGroupBuilder group)
    {
        group.MapPost("/sessions/{id}/image", (HttpContext http, string id, LabImageRequest request,
            PersonaLabRuntime runtime, PersonaLabImages images) =>
        {
            var owner = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new LabException(401, "account_required");
            return Results.Json(runtime.ChangeImage(owner, id, _ => images.Put(owner, id, request)));
        });
        group.MapDelete("/sessions/{id}/image", (HttpContext http, string id,
            PersonaLabRuntime runtime, PersonaLabImages images) =>
        {
            var owner = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new LabException(401, "account_required");
            return Results.Json(runtime.ChangeImage(owner, id, _ => images.Clear(owner, id), revoke: true));
        });
    }
}
