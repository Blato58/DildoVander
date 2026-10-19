using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Identity;

internal sealed record AccountConfiguration(bool Enabled, string DataRoot, string KeyRoot,
    string RelyingPartyId, string[] AllowedOrigins)
{
    public static AccountConfiguration Read(IConfiguration config)
    {
        if (!config.GetValue<bool>("ApplicationIdentity:Enabled"))
        {
            return new(false, "", "", "", []);
        }
        var root = config["ApplicationIdentity:DataRoot"] ?? "";
        var keys = config["ApplicationIdentity:DataProtectionRoot"] ?? "";
        var rp = config["ApplicationIdentity:RelyingPartyId"] ?? "";
        var origins = config.GetSection("ApplicationIdentity:AllowedOrigins").Get<string[]>() ?? [];
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(keys)
            || Uri.CheckHostName(rp) != UriHostNameType.Dns || rp.Contains(':')
            || origins.Length == 0 || origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != origin
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !(uri.Host == rp || uri.Host.EndsWith("." + rp, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("ApplicationIdentity requires absolute durable data/key paths, an explicit RP domain and exact HTTPS origins within that domain.");
        }
        var archiveRoot = config["Keepsake:DataRoot"];
        if (!string.IsNullOrWhiteSpace(archiveRoot))
        {
            var archiveKeys = config["Keepsake:DataProtectionRoot"] ?? archiveRoot + ".keys";
            if (!Path.GetFullPath(archiveKeys).Equals(Path.GetFullPath(keys), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Identity and keepsakes must explicitly share the existing durable DataProtection root.");
            }
        }
        return new(true, root, keys, rp, origins);
    }
}

internal static class AccountIdentity
{
    internal const string SessionClaim = "dv:account-session";
    internal const string CeremonyCookie = "__Host-dv-ceremony";
    internal static readonly CookieOptions CeremonyCookieOptions = new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(5)
    };

    public static IServiceCollection AddAccountIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var config = AccountConfiguration.Read(configuration);
        services.AddSingleton(config);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-DV-CSRF";
            options.Cookie.Name = "__Host-dv-csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
        });
        services.AddAuthorization();
        var requestsPerMinute = Math.Clamp(configuration.GetValue("ApplicationIdentity:RequestsPerMinute", 40), 10, 120);
        services.AddRateLimiter(options => options.AddPolicy("account", context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        // Register schemes even when disabled so existing callers remain anonymous.
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Host-dv-account";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => ApiProblem.Create(context.HttpContext, 401, "account_required").ExecuteAsync(context.HttpContext);
            options.Events.OnRedirectToAccessDenied = context => ApiProblem.Create(context.HttpContext, 403, "account_forbidden").ExecuteAsync(context.HttpContext);
            options.Events.OnValidatePrincipal = ValidateSessionAsync;
        });
        if (!config.Enabled)
        {
            return services;
        }
        Directory.CreateDirectory(config.DataRoot);
        Directory.CreateDirectory(config.KeyRoot);
        services.AddDataProtection().SetApplicationName("DildoVander.DV10")
            .PersistKeysToFileSystem(new DirectoryInfo(config.KeyRoot));
        services.AddDbContext<AccountDatabase>(options => options.UseSqlite(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(config.DataRoot, "identity.sqlite"), ForeignKeys = true
            }.ToString()));
        services.AddIdentityCore<AccountUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddRoles<IdentityRole>().AddEntityFrameworkStores<AccountDatabase>().AddSignInManager().AddDefaultTokenProviders();
        services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ServerDomain = config.RelyingPartyId;
            options.UserVerificationRequirement = "required";
            options.ResidentKeyRequirement = "required";
            options.AuthenticatorTimeout = TimeSpan.FromMinutes(3);
            options.ValidateOrigin = context => ValueTask.FromResult(config.AllowedOrigins.Contains(context.Origin, StringComparer.Ordinal));
        });
        return services;
    }

    public static async Task InitializeAccountsAsync(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await db.Database.GetPendingMigrationsAsync()).Any()
            || (await db.Database.GetAppliedMigrationsAsync()).Any(x => !known.Contains(x)))
        {
            throw new InvalidOperationException("Account schema migration required. Stop the service, run the configured binary with --account-migrate, then start it. The command preserves a private SQLite backup before upgrades.");
        }
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in AccountRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded) { throw new InvalidOperationException("Cannot initialize account roles."); }
            }
        }
    }

    public static void UseAccountIdentity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            var accountApi = context.Request.Path.StartsWithSegments("/app/v1");
            var existingAccountRequest = context.Request.Path.StartsWithSegments("/v1")
                && context.User.HasClaim(c => c.Type == SessionClaim)
                && !context.Request.Headers.ContainsKey("Authorization");
            // A WebSocket GET carries state-changing microphone frames after
            // admission; it needs operate permission and mutation CSRF/origin.
            var accountSocket = existingAccountRequest && context.WebSockets.IsWebSocketRequest;
            if (existingAccountRequest && context.Request.Headers["X-DV-Device-Id"].ToString() is { Length: > 0 and <= 128 } target)
            {
                var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
                var provisioned = configuration.GetSection("Devices").GetChildren().Any(x => x["DeviceId"] == target);
                var permission = !accountSocket && (context.Request.Method is "GET" or "HEAD") ? "read" : "operate";
                if (provisioned && await AccountResourceAuthorization.HasPermissionAsync(context, "device", target, permission))
                {
                    context.Items["dv-account-device"] = target;
                }
            }
            if (accountApi || existingAccountRequest)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (accountApi && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                {
                    bodySize.MaxRequestBodySize = 65536;
                }
                if (accountSocket || context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
                {
                    var config = context.RequestServices.GetRequiredService<AccountConfiguration>();
                    if (!config.Enabled)
                    {
                        await ApiProblem.Create(context, 503, "accounts_not_configured").ExecuteAsync(context); return;
                    }
                    if (!context.Request.IsHttps || !config.AllowedOrigins.Contains(context.Request.Headers.Origin.ToString(), StringComparer.Ordinal))
                    {
                        await ApiProblem.Create(context, 403, "account_origin_required").ExecuteAsync(context); return;
                    }
                    try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                    catch (AntiforgeryValidationException)
                    {
                        await ApiProblem.Create(context, 400, "csrf_required").ExecuteAsync(context); return;
                    }
                    if (existingAccountRequest && context.Request.Path.StartsWithSegments("/v1/admin"))
                    {
                        if (!AccountRoles.IsAdmin(context.User))
                        { await ApiProblem.Create(context, 403, "account_admin_required").ExecuteAsync(context); return; }
                        var recent = await AccountAuthorization.RequireRecentAsync(context);
                        if (recent is not null) { await recent.ExecuteAsync(context); return; }
                        if (context.Request.Headers["X-DV-Confirm"] != "yes")
                        { await ApiProblem.Create(context, 409, "confirmation_required").ExecuteAsync(context); return; }
                    }
                }
            }
            await next(context);
        });
    }

    private static async Task ValidateSessionAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<AccountConfiguration>();
        if (!config.Enabled) { context.RejectPrincipal(); return; }
        var id = context.Principal?.FindFirstValue(SessionClaim);
        var db = context.HttpContext.RequestServices.GetRequiredService<AccountDatabase>();
        var session = id is null ? null : await db.Sessions.FindAsync(id);
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<AccountUser>>();
        var user = session is null ? null : await users.FindByIdAsync(session.UserId);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || user is null || user.Disabled
            || user.SecurityStamp != context.Principal?.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType))
        {
            context.RejectPrincipal(); return;
        }
        // Rebuild roles on every request: revocation is effective immediately.
        var now = AccountEndpoints.Now();
        if (session.LastSeenAt < now - 60)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync();
        }
        var principal = await context.HttpContext.RequestServices.GetRequiredService<IUserClaimsPrincipalFactory<AccountUser>>().CreateAsync(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, session.Id));
        context.ReplacePrincipal(principal);
    }
}
