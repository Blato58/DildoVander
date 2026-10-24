using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

internal static class TestingCharacterRaceTests
{
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"testing-consent-race-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {["Backend:DatabasePath"]=Path.Combine(root,"backend.sqlite")}).Build();
        try
        {
            var provider=new HeldEnrichment();
            using var resources=new TestingResources(config,TimeProvider.System,null,null,provider,null);
            var lab=new PersonaLabStore(Path.Combine(root,"lab.sqlite"),LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId,new GptLiveOptions()));
            using var live=new GptLiveClient(config);using var realtime=new OpenAiRealtimeClient(config);
            using var runtime=new PersonaLabRuntime(lab,live,realtime);
            using var hardware=new TestingHardwareAdapter(config);
            var service=new TestingSessionService(resources,lab,runtime,hardware,TimeProvider.System);
            var character=await service.SaveCharacterAsync("owner",null,new(new("Original","Library"),true,true),default);
            var pending=await service.EnrichAsync("owner",character.Id,true,default);
            if(pending.Profile.Status!="queued")throw new InvalidOperationException("Enrichment request did not return while queued.");
            var processing=resources.ProcessNextCharacterEnrichmentAsync(default);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var withdrawn=await service.SaveCharacterAsync("owner",character.Id,
                new(new("Corrected","Library"),false,false,character.Profile.Revision),default);
            provider.Continue.SetResult();
            await processing;
            var current=service.Characters("owner").Single();
            if(current.LongTermConsent || current.Profile.Revision<withdrawn.Profile.Revision || current.Profile.UserInput.Name!="Corrected")
                throw new InvalidOperationException("Late enrichment restored withdrawn consent or stale profile.");
            Console.WriteLine("Testing character enrichment race preserves withdrawn consent and latest profile (controlled provider).");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private sealed class HeldEnrichment:ICharacterEnrichmentProvider
    {
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CharacterEnrichment> EnrichAsync(CharacterJob job,CancellationToken token)
        {
            Started.TrySetResult();await Continue.Task;
            return new(job.UserInput.Name,job.UserInput.World,"Fictional context",[],[],[],false,[],"controlled-race-provider","test-v1",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),job.Revision,"synthetic");
        }
    }
}
