using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunTestingSessionHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(configureEndpoints: app =>
        {
            app.MapPersonaLab(); app.MapTestingSessions();
        }, configureServices: services =>
        {
            // Fixture configuration clears inherited environment sources. No key,
            // provider fake or physical-device grant is used for these HTTP tests.
            services.AddSingleton(new GptLiveOptions());
            services.AddSingleton<GptLiveClient>(); services.AddSingleton<OpenAiRealtimeClient>();
            services.AddPersonaLab(); services.AddTestingSessions();
        });
        _ = fixture.App.Services.GetRequiredService<TestingSessionService>();
        using var anonymous = fixture.Client();
        Check((await anonymous.GetAsync("/app/v1/testing-sessions")).StatusCode == HttpStatusCode.Unauthorized,
            "anonymous testing-session reads require normal authentication");
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner"); await CsrfAsync(owner);
        using var tester = fixture.Client();
        using var testerKey = new VirtualPasskey();
        await InviteAndEnroll(tester, testerKey, AccountRoles.Tester);
        var request = new TestingSessionRequest(OperationId: Guid.NewGuid().ToString("D"), OpenAiConsent: true, RecordContent: true);
        var csrf = tester.DefaultRequestHeaders.GetValues("X-DV-CSRF").Single();
        tester.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await tester.PostAsJsonAsync("/app/v1/testing-sessions", request)).StatusCode == HttpStatusCode.BadRequest,
            "real antiforgery rejects test-session creation without CSRF");
        tester.DefaultRequestHeaders.Add("X-DV-CSRF", csrf);
        var created = await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions", request));
        var id = created.GetProperty("id").GetString()!;
        var labId = created.GetProperty("labSessionId").GetString()!;
        var virtualId = created.GetProperty("virtualDeviceId").GetString()!;
        Check(created.GetProperty("inputMode").GetString() == "text" && created.GetProperty("engine").GetString() == "gpt-live-1"
            && virtualId == "test-" + id, "one-click creation uses default real engine and explicit virtual identity");
        var duplicate = await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions", request));
        Check(duplicate.GetProperty("id").GetString() == id, "uncertain create retry is idempotent");
        var detail = await JsonAsync(await tester.GetAsync("/app/v1/testing-sessions/" + id));
        var hardware = detail.GetProperty("hardware");
        Check(hardware.GetProperty("connected").GetBoolean() && hardware.GetProperty("ready").GetBoolean()
            && hardware.GetProperty("compartments").GetArrayLength() == 6,
            "backend automatically prepares production native virtual hardware without BLE or manual setup");
        Check((await JsonAsync(await tester.GetAsync("/app/v1/testing-sessions"))).GetArrayLength() == 1,
            "the authenticated tester lists its durable test session");
        Check((await JsonAsync(await tester.GetAsync("/app/v1/persona-lab/sessions"))).GetArrayLength() == 0,
            "normal Lab list excludes testing-managed conversation lifecycle");
        Check((await tester.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + labId + "/end", new {})).StatusCode == HttpStatusCode.Conflict,
            "raw Lab end cannot bypass test-session shutdown");
        Check((await tester.DeleteAsync("/app/v1/persona-lab/sessions/" + labId)).StatusCode == HttpStatusCode.Conflict,
            "raw Lab deletion cannot orphan test resources");
        var catalog = await JsonAsync(await tester.GetAsync("/app/v1/testing-sessions/catalog"));
        Check(!catalog.GetProperty("capabilities").GetProperty("providerConfigured").GetProperty("live").GetBoolean(),
            "backend truthfully reports absent provider configuration");
        Check((await tester.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + labId + "/turns", new { text="No fake response" })).StatusCode == HttpStatusCode.ServiceUnavailable,
            "missing provider remains an actual API failure and cannot produce a fake successful reply");
        var character = await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions/characters", new
        {
            userInput = new { name="Clockmaker", world="Original fiction", description="Repairs clocks" },
            openAiConsent=false, longTermConsent=false, expectedRevision=0
        }));
        var characterId=character.GetProperty("id").GetString()!;
        await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions/"+id+"/character",new { characterId,expectedRevision=created.GetProperty("revision").GetInt32() }));
        using var other = fixture.Client(); using var otherKey = new VirtualPasskey();
        await InviteAndEnroll(other,otherKey,AccountRoles.Tester);
        Check((await other.GetAsync("/app/v1/testing-sessions/"+id)).StatusCode==HttpStatusCode.NotFound,
            "another Tester cannot read the owned test session");
        Check((await other.PostAsJsonAsync("/app/v1/testing-sessions/"+id+"/end",new {})).StatusCode==HttpStatusCode.NotFound,
            "another Tester cannot terminate the owned test session");
        Check((await JsonAsync(await other.GetAsync("/app/v1/testing-sessions/characters"))).GetArrayLength()==0,
            "test characters remain isolated between accounts");
        using var customer=fixture.Client();using var customerKey=new VirtualPasskey();
        await InviteAndEnroll(customer,customerKey,AccountRoles.Customer);
        Check((await customer.GetAsync("/app/v1/testing-sessions")).StatusCode==HttpStatusCode.Forbidden,
            "Customer role does not acquire testing capabilities");
        Check((await owner.GetAsync("/app/v1/testing-sessions")).StatusCode==HttpStatusCode.Forbidden,
            "production Owner authority does not implicitly grant a testing capability");
        using var staff=fixture.Client();using var staffKey=new VirtualPasskey();
        await InviteAndEnroll(staff,staffKey,AccountRoles.Operator);
        Check((await staff.PostAsJsonAsync("/app/v1/testing-sessions",request)).StatusCode==HttpStatusCode.Forbidden,
            "production Operator role cannot create a test session without Tester capability");
        Check((await tester.GetAsync("/app/v1/admin/users")).StatusCode==HttpStatusCode.Forbidden,
            "Tester cannot inspect production account administration");
        Check((await tester.GetAsync("/v1/test-device")).StatusCode==HttpStatusCode.Unauthorized,
            "Tester account is not physical device authentication");
        using var virtualCredential=fixture.Client();
        virtualCredential.DefaultRequestHeaders.Authorization=new("Bearer",virtualId);
        Check((await virtualCredential.GetAsync("/v1/test-device")).StatusCode==HttpStatusCode.Unauthorized,
            "virtual suitcase identity is never a physical device credential");
        using var resumed=fixture.Client();await SignInAsync(resumed,testerKey);await CsrfAsync(resumed);
        var restored=await JsonAsync(await resumed.GetAsync("/app/v1/testing-sessions/"+id));
        Check(restored.GetProperty("session").GetProperty("id").GetString()==id
            && restored.GetProperty("character").GetProperty("id").GetString()==characterId,
            "normal passkey sign-in resumes stable session and character after client restart");
        var ended=await JsonAsync(await resumed.PostAsJsonAsync("/app/v1/testing-sessions/"+id+"/end",new {}));
        Check(ended.GetProperty("status").GetString()=="ended", "explicit end persists canonical lifecycle");
        Check((await resumed.DeleteAsync("/app/v1/testing-sessions/"+id)).StatusCode==HttpStatusCode.NoContent,
            "explicit authorized test deletion disposes test-owned resources");
        Check((await resumed.GetAsync("/app/v1/testing-sessions/"+id)).StatusCode==HttpStatusCode.NotFound,
            "deleted session cannot be resumed");
        Check((await JsonAsync(await owner.GetAsync("/app/v1/admin/users"))).GetProperty("users").GetArrayLength()==5,
            "test resource deletion preserves production account records");
        Console.WriteLine("Testing-session HTTPS: real passkey invitations/authentication, CSRF, hardware-free start, isolated ownership, lifecycle, resume, deletion and production authorization passed (no provider calls).");

        async Task InviteAndEnroll(HttpClient client,VirtualPasskey key,string role)
        {
            await CsrfAsync(owner);
            var invitation=await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",new {role,recoveryUserId=(string?)null}));
            await EnrollAsync(client,key,invitation.GetProperty("token").GetString()!,role);await CsrfAsync(client);
        }
    }
}
