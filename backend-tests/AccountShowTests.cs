using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class AccountShowTests
{
    public static async Task RunAsync(WebApplication app, HttpClient owner, HttpClient op, HttpClient anonymous, string userId)
    {
        const string path = "/v1/show/devices/granted-device";
        async Task Permission(string permission)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().ResourceGrants.Where(g => g.Id == "device-grant")
                .ExecuteUpdateAsync(update => update.SetProperty(g => g.Permission, permission));
        }
        Task<HttpResponseMessage> Stop() => op.PostAsJsonAsync(path + "/commands", new {
            commandId = Guid.NewGuid().ToString("D"), expectedStateRevision = 0, kind = "stop", ttlSeconds = 30 });
        Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "anonymous cannot direct show");
        Check((await op.GetAsync(path)).IsSuccessStatusCode, "exact granted device show read");
        Check((await op.GetAsync("/v1/show/devices/denied-device")).StatusCode == HttpStatusCode.Forbidden, "path cannot borrow another device header grant");
        await Permission("read");
        Check((await Stop()).StatusCode == HttpStatusCode.Forbidden, "read grant cannot operate show");
        await Permission("operate");
        op.DefaultRequestHeaders.Remove("X-DV-Confirm");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().Sessions.Where(s => s.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(s => s.AuthenticatedAt, AccountEndpoints.Now() - 601));
        var command = new { commandId = Guid.NewGuid().ToString("D"), expectedStateRevision = 0, kind = "stop", ttlSeconds = 30 };
        Check((await op.PostAsJsonAsync(path + "/commands", command)).IsSuccessStatusCode, "Stop works without step-up or confirmation");
        var replay = await op.PostAsJsonAsync(path + "/commands", command);
        Check(replay.IsSuccessStatusCode && JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString() == "replayed", "existing command replay remains idempotent");
        var end = new { eveningId = Guid.NewGuid().ToString("D"), expectedStateRevision = 0 };
        Check((await op.PostAsJsonAsync(path + "/end-evening", end)).StatusCode == HttpStatusCode.Forbidden, "sensitive close requires recent passkey");
        Check((await op.PostAsJsonAsync(path + "/END-EVENING/", end)).StatusCode == HttpStatusCode.Forbidden,
            "case/trailing slash route aliases cannot bypass recent passkey");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().Sessions.Where(s => s.UserId == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(s => s.AuthenticatedAt, AccountEndpoints.Now()));
        Check((await op.PostAsJsonAsync(path + "/end-evening", end)).StatusCode == HttpStatusCode.Conflict, "sensitive close requires explicit confirmation");
        Check((await op.PostAsJsonAsync(path + "/END-EVENING/", end)).StatusCode == HttpStatusCode.Conflict,
            "case/trailing slash aliases still require explicit confirmation");
        var csrf = op.DefaultRequestHeaders.GetValues("X-DV-CSRF").Single();
        op.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await Stop()).StatusCode == HttpStatusCode.BadRequest, "show mutation cannot bypass account CSRF");
        op.DefaultRequestHeaders.Add("X-DV-CSRF", csrf);
        op.DefaultRequestHeaders.Remove("Origin"); op.DefaultRequestHeaders.Add("Origin", "https://wrong.example");
        Check((await Stop()).StatusCode == HttpStatusCode.Forbidden, "show mutation rejects foreign origin");
        op.DefaultRequestHeaders.Remove("Origin"); op.DefaultRequestHeaders.Add("Origin", "https://localhost");
        op.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        var duel = Guid.NewGuid().ToString("D");
        var grant = new { side = "left", revision = 0, operationKey = Guid.NewGuid().ToString("D") };
        Check((await op.PostAsJsonAsync(path + $"/duels/{duel}/grants", grant)).StatusCode == HttpStatusCode.Forbidden, "operate cannot issue guest duel grants");
        Check((await op.PostAsJsonAsync(path + $"/duels/{duel}/GRANTS/", grant)).StatusCode == HttpStatusCode.Forbidden,
            "case/trailing slash aliases cannot turn manage into operate");
        await Permission("manage");
        Check((await op.PostAsJsonAsync(path + $"/duels/{duel}/grants", grant)).StatusCode == HttpStatusCode.NotFound, "manage reaches scoped duel lookup without inventing a grant");
        Check((await op.GetAsync("/v1/show/device-cues")).StatusCode == HttpStatusCode.Unauthorized, "account cannot poll firmware cue delivery");
        Check((await op.GetAsync($"/v1/show/device-cues/{duel}/audio")).StatusCode == HttpStatusCode.Unauthorized, "account cannot consume firmware cue audio");
        Check((await op.PostAsJsonAsync($"/v1/show/device-cues/{duel}/ack", new { })).StatusCode == HttpStatusCode.Unauthorized, "account cannot acknowledge physical completion");
        Check((await op.PostAsJsonAsync("/v1/show/offline-outcomes", new { })).StatusCode == HttpStatusCode.Forbidden, "unscoped firmware ingest rejects account");
        var imported = new OfflineOutcome(Guid.NewGuid().ToString("D"), "granted-device", Guid.NewGuid().ToString("D"),
            1, Guid.NewGuid().ToString("D"), null, "operator", 1, PartyWandCatalog.Version, 10, 1000, "boot_monotonic", "completed_by_timing");
        Check((await op.PostAsJsonAsync(path + "/offline-outcomes", new OfflineOutcomeRequest(1, imported))).IsSuccessStatusCode,
            "confirmed scoped operator import uses accepted business validation");
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = app.Configuration["Backend:DatabasePath"] }.ToString()))
        {
            await database.OpenAsync(); using var statement = database.CreateCommand();
            statement.CommandText = "SELECT provenance FROM show_offline_outcomes WHERE device_id=$device AND operation_id=$operation";
            statement.Parameters.AddWithValue("$device", imported.DeviceId); statement.Parameters.AddWithValue("$operation", imported.OperationId);
            Check((string?)await statement.ExecuteScalarAsync() == "operator_relay", "account import never asserts firmware physical report provenance");
        }
        Check((await op.PostAsJsonAsync($"/guest/v1/duels/{duel}/choices", new {side="left", round=1, revision=0, action="spell", operationKey=Guid.NewGuid().ToString("D")})).StatusCode == HttpStatusCode.Forbidden,
            "signed-in operator cookie cannot replace scoped guest duel grant");

        await Permission("read");
        Check((await Stop()).StatusCode == HttpStatusCode.Forbidden, "revoked operate permission immediately stops control");
        await Permission("operate");
        owner.DefaultRequestHeaders.Remove("X-DV-Device-Id");
        owner.DefaultRequestHeaders.Add("X-DV-Device-Id", "granted-device");
        Check((await owner.GetAsync(path)).IsSuccessStatusCode, "owner uses same explicit target device boundary");
        owner.DefaultRequestHeaders.Remove("X-DV-Device-Id");
        Console.WriteLine("PASS account Show HTTP exact-device read/operate/manage, Stop/idempotency, CSRF/origin, recent/confirmation, revocation and firmware cue isolation");
    }

    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("Account Show: " + description); }
}
