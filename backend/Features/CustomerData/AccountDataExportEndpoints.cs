using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.CustomerData;

internal static class AccountDataExportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // The shared Program owner calls these methods after registering the real
    // memory adapter. Neither method adds an alternative authentication path.
    internal static IServiceCollection AddAccountDataExports(this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("ApplicationIdentity:Enabled")) { return services; }
        services.AddSingleton<AccountDataExportAuthority>();
        services.AddSingleton<ICustomerDataAuthority>(provider => provider.GetRequiredService<AccountDataExportAuthority>());
        services.AddSingleton<ICustomerDataGuardAuthority>(provider => provider.GetRequiredService<AccountDataExportAuthority>());
        services.AddSingleton(provider =>
        {
            var root = provider.GetRequiredService<AccountConfiguration>().DataRoot;
            using var scope = provider.CreateScope();
            var connection = scope.ServiceProvider.GetRequiredService<AccountDatabase>().Database.GetConnectionString()
                ?? throw new CustomerDataAccessException(503, "customer_data_unavailable");
            // Reject configured aliases before initializing the new job schema.
            CustomerDataDatabasePaths.Validate(connection,
                provider.GetRequiredService<CharacterProfileStore>().CustomerDataDatabasePath,
                provider.GetRequiredService<OperationJournal>().CustomerDataDatabasePath,
                provider.GetRequiredService<KeepsakeStore>().CustomerDataDatabasePath,
                provider.GetRequiredService<ICustomerDataMemoryAdmission>().DatabasePath,
                AccountDataExportStore.PathFor(root));
            return new AccountDataExportStore(root, provider.GetRequiredService<TimeProvider>(),
                AccountDataExportReceiptLimits.Read(configuration));
        });
        services.AddSingleton<AccountDataExportService>();
        services.AddHostedService<AccountDataExportWorker>();
        return services;
    }

    internal static void MapAccountDataExports(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("ApplicationIdentity:Enabled")) { return; }
        var group = app.MapGroup("/app/v1/customer/data/exports")
            .RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try { return await next(invocation); }
            catch (CustomerDataAccessException error)
            {
                return Problem(http, error.Status, error.Code);
            }
            catch (AccountDataExportException error)
            {
                return Problem(http, error.Status, error.Code);
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
            {
                return Problem(http, 503, "account_export_unavailable");
            }
        });

        group.MapPost("", async (HttpContext http, AccountDataExportService exports) =>
        {
            var request = await ReadAsync(http);
            if (request.SchemaVersion != 1 || !Guid.TryParseExact(http.Request.Headers["Idempotency-Key"], "D", out var key)
                || key == Guid.Empty)
            {
                throw new AccountDataExportException(400, "invalid_export_request");
            }
            var view = await exports.RequestAsync(http, key, http.RequestAborted);
            return Results.Accepted($"/app/v1/customer/data/exports/{view.Id}", view);
        });
        group.MapGet("/{id:guid}", async (string id, HttpContext http, AccountDataExportService exports) =>
            Results.Ok(await exports.StatusAsync(http, id, http.RequestAborted)));
        group.MapDelete("/{id:guid}", async (string id, HttpContext http, AccountDataExportService exports) =>
        {
            await exports.DeleteAsync(http, id, http.RequestAborted);
            return Results.NoContent();
        });
        group.MapGet("/{id:guid}/download", async (string id, HttpContext http, AccountDataExportService exports) =>
        {
            await exports.DownloadAsync(http, id, http.RequestAborted);
            return Results.Empty;
        });
    }

    private static IResult Problem(HttpContext http, int status, string code)
    {
        if (http.Response.HasStarted)
        {
            http.Abort();
            return Results.Empty;
        }
        http.Response.Clear();
        http.Response.Headers.CacheControl = "no-store";
        return ApiProblem.Create(http, status, code);
    }

    private static async Task<AccountDataExportRequest> ReadAsync(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
        {
            throw new AccountDataExportException(400, "invalid_export_request");
        }
        var bytes = new byte[1025];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);
            if (read == 0) { break; }
            count += read;
        }
        if (count > 1024) { throw new AccountDataExportException(413, "invalid_export_request"); }
        try
        {
            return JsonSerializer.Deserialize<AccountDataExportRequest>(bytes.AsSpan(0, count), Json)
                ?? throw new AccountDataExportException(400, "invalid_export_request");
        }
        catch (JsonException) { throw new AccountDataExportException(400, "invalid_export_request"); }
    }
}
