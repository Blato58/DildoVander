using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.Memory;

internal static class CustomerMemoryEndpoints
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    internal static IServiceCollection AddCustomerMemoryData(this IServiceCollection services)
    {
        services.AddSingleton<CustomerMemoryData>();
        services.AddSingleton<ICustomerDataMemoryAdmission>(provider => provider.GetRequiredService<CustomerMemoryData>());
        services.AddSingleton<ICustomerMemoryGuardedDataSource>(provider => provider.GetRequiredService<CustomerMemoryData>());
        services.AddSingleton<ICustomerMemoryDataSource, CustomerMemoryDataSource>();
        return services;
    }

    internal static void MapCustomerMemoryEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }

        var routes = app.MapGroup("/app/v1/customer/memory")
            .RequireAuthorization()
            .RequireRateLimiting("account");
        routes.MapGet("", ReadAsync);
        routes.MapPut("/{id}", CorrectAsync);
        routes.MapPost("/{id}/forget", ForgetAsync);
    }

    private static async Task<IResult> CorrectAsync(HttpContext http, string id,
        CustomerMemoryCorrectionRequest request, ICustomerDataAuthority authority,
        ICustomerDataGuardAuthority guards, CustomerMemoryData memory)
    {
        if (http.Request.Headers.ContainsKey("Authorization"))
        {
            return ApiProblem.Create(http, 401, "account_access_unavailable");
        }
        var committed = false;
        try
        {
            var access = await authority.CaptureAsync(http, http.RequestAborted);
            using (var prelude = await guards.AcquireAsync(access, http.RequestAborted))
            using (var admission = await memory.AcquireAsync(prelude.Deadline, http.RequestAborted))
            using (var held = prelude.AcquireShared(admission))
            {
                memory.Correct(held, id, request);
                committed = true;
            }
            return Results.NoContent();
        }
        catch (Exception error) when (committed || error is CustomerMemoryMutationUncertainException)
        {
            return ApiProblem.Create(http, 409, "memory_mutation_outcome_unknown");
        }
        catch (CustomerDataAccessException error)
        {
            return ApiProblem.Create(http, error.Status, error.Code);
        }
    }

    private static async Task<IResult> ForgetAsync(HttpContext http, string id,
        CustomerMemoryForgetRequest request, ICustomerDataAuthority authority,
        ICustomerDataGuardAuthority guards, CustomerMemoryData memory)
    {
        if (http.Request.Headers.ContainsKey("Authorization"))
        {
            return ApiProblem.Create(http, 401, "account_access_unavailable");
        }
        var committed = false;
        try
        {
            var access = await authority.CaptureAsync(http, http.RequestAborted);
            using (var prelude = await guards.AcquireAsync(access, http.RequestAborted))
            using (var admission = await memory.AcquireAsync(prelude.Deadline, http.RequestAborted))
            using (var held = prelude.AcquireShared(admission))
            {
                memory.Forget(held, id, request);
                committed = true;
            }
            return Results.NoContent();
        }
        catch (Exception error) when (committed || error is CustomerMemoryMutationUncertainException)
        {
            return ApiProblem.Create(http, 409, "memory_mutation_outcome_unknown");
        }
        catch (CustomerDataAccessException error)
        {
            return ApiProblem.Create(http, error.Status, error.Code);
        }
    }

    private static async Task<IResult> ReadAsync(HttpContext http,
        ICustomerDataAuthority authority, ICustomerDataGuardAuthority guards,
        ICustomerMemoryGuardedDataSource memory)
    {
        // Account content never accepts a separate service credential.
        if (http.Request.Headers.ContainsKey("Authorization"))
        {
            return ApiProblem.Create(http, 401, "account_access_unavailable");
        }

        var accepted = false;
        try
        {
            var access = await authority.CaptureAsync(http, http.RequestAborted);
            using (var prelude = await guards.AcquireAsync(access, http.RequestAborted))
            using (var admission = await memory.AcquireAsync(prelude.Deadline, http.RequestAborted))
            using (var held = prelude.AcquireShared(admission))
            {
                var snapshot = memory.Capture(held);
                var bytes = SerializeList(snapshot);
                var digest = Convert.ToHexString(SHA256.HashData(bytes.Span));
                http.Response.ContentType = "application/json; charset=utf-8";
                http.Response.Headers.CacheControl = "no-store";
                memory.Release(held, snapshot, bytes, digest, http.Response.BodyWriter);
                accepted = true;
            }

            // Network work happens only after every synchronous content guard
            // has been released. Advance above is the content acceptance point.
            await http.Response.BodyWriter.FlushAsync(http.RequestAborted);
            return Results.Empty;
        }
        catch (Exception error) when (accepted || error is CustomerDataReleaseUncertainException)
        {
            // Bytes may already have been accepted. Never append a JSON error
            // or imply that aborting the connection recalls those bytes.
            http.Abort();
            return Results.Empty;
        }
        catch (CustomerDataAccessException error)
        {
            return ApiProblem.Create(http, error.Status, error.Code);
        }
    }

    internal static ReadOnlyMemory<byte> SerializeList(CustomerMemoryDataSnapshot snapshot)
    {
        var buffer = new BoundedMemoryListBuffer();
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, new CustomerMemoryList(snapshot.Revision,
            snapshot.Items.Select(item => new CustomerMemoryView(item.Id, item.Revision,
                item.Text, item.Kind, item.SourceReference, item.CreatedAt,
                item.ProfileId, item.CustomerSessionId,
                CanCorrect: item.Kind == nameof(MemoryKind.UserReportedFact), CanForget: true)).ToArray(),
            snapshot.Complete), WireJson);
        writer.Flush();
        return buffer.WrittenMemory;
    }

    // JSON escaping can expand otherwise bounded UTF-8 facts. Enforce the wire
    // size while serializing, before allocating an oversized response buffer.
    private sealed class BoundedMemoryListBuffer : IBufferWriter<byte>
    {
        private byte[] bytes = new byte[4096];
        private int written;
        internal ReadOnlyMemory<byte> WrittenMemory => bytes.AsMemory(0, written);

        public void Advance(int count)
        {
            if (count < 0 || count > bytes.Length - written)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0) { throw new ArgumentOutOfRangeException(nameof(sizeHint)); }
            var required = Math.Max(sizeHint, 1);
            if (required > CustomerDataLimits.MaxExportBytes - written)
            {
                throw new CustomerDataAccessException(413, "export_size_limit");
            }
            if (bytes.Length - written < required)
            {
                Array.Resize(ref bytes, Math.Min(CustomerDataLimits.MaxExportBytes,
                    Math.Max(bytes.Length * 2, written + required)));
            }
            return bytes.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    private sealed record CustomerMemoryList(string Revision,
        IReadOnlyList<CustomerMemoryView> Items, bool Complete);

    private sealed record CustomerMemoryView(string Id, long Revision, string Text,
        string Kind, string SourceReference, DateTimeOffset CreatedAt,
        string ProfileId, string CustomerSessionId, bool CanCorrect, bool CanForget);
}
