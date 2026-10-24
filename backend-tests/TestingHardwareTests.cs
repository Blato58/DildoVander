using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.Configuration;

internal static class TestingHardwareTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "testing-hardware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Backend:DatabasePath"] = Path.Combine(root,"backend.sqlite")
        }).Build();
        var id = Guid.NewGuid().ToString("N"); var device = "test-" + id;
        try
        {
            using var adapter = new TestingHardwareAdapter(configuration);
            var initial = await adapter.SnapshotAsync(id, device, default);
            Check(initial.Connected && initial.Ready && initial.Compartments.Length == 6, "packaged native runtime prepares six positions automatically");
            var operation = Guid.NewGuid().ToString("D");
            var presented = await adapter.PresentAsync(id, device, operation, 3, default);
            Check(presented.Events.Any(x=>x.Type=="presentation" && x.Code=="presentation-admitted" && x.Compartment==3),
                "real production C++ state machine acknowledges backend accepted compartment");
            var duplicate = await adapter.PresentAsync(id, device, operation, 3, default);
            Check(duplicate.Events.Any(x=>x.Code=="duplicate"), "persistent production journal refuses duplicate effects");
            var stopped = await adapter.CommandAsync(id,device,"stop",null,null,default);
            Check(!stopped.Ready && stopped.State=="recovery-required", "Stop uses production recovery state");
            var recovered = await adapter.CommandAsync(id,device,"recover",null,null,default);
            Check(recovered.Ready, "explicit simulated inspection restores readiness");
            await adapter.CommandAsync(id,device,"camera","missing",null,default);
            Check(!(await adapter.SnapshotAsync(id,device,default)).CameraAvailable,"missing camera is explicit simulation evidence");
            Check((await adapter.CommandAsync(id,device,"recover",null,null,default)).CameraAvailable,"explicit recovery restores virtual camera availability");
            await adapter.CommandAsync(id,device,"fault","servo",0,default);
            await adapter.PresentAsync(id,device,Guid.NewGuid().ToString("D"),2,default);
            var failed = await adapter.SnapshotAsync(id,device,default);
            var failureDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (failed.Ready && DateTimeOffset.UtcNow < failureDeadline)
            {
                await Task.Delay(100);
                failed = await adapter.SnapshotAsync(id,device,default);
            }
            Check(!failed.Ready, "injected servo failure is observed by C++ safety owner");
            await adapter.CommandAsync(id,device,"disconnect",null,null,default);
            Check(!(await adapter.SnapshotAsync(id,device,default)).Connected, "injected disconnect never reports physical connection");
            await adapter.CommandAsync(id,device,"reconnect",null,null,default);
            await adapter.CommandAsync(id,device,"recover",null,null,default);
            await adapter.CommandAsync(id,device,"duel",Guid.NewGuid().ToString("D"),null,default);
            var queuedEffect = adapter.CommandAsync(id,device,"duel",Guid.NewGuid().ToString("D"),null,default);
            await adapter.CommandAsync(id,device,"shutdown",null,null,default);
            try { await queuedEffect; throw new Exception("retired effect accepted"); }
            catch (TestingException) { }
            Check(!(await adapter.SnapshotAsync(id,device,default)).Connected,
                "queued effect cannot recreate a native process after shutdown");
            try { await adapter.SnapshotAsync(id,"real-device",default); throw new Exception("identity accepted"); }
            catch(TestingException e) when(e.Code=="testing_hardware_identity_invalid") { }
            Console.WriteLine("Packaged native testing hardware admission, duplicate, Stop, recovery and failure checks passed (simulation only).");
        }
        finally { Directory.Delete(root,true); }
    }
    private static void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); }
}
