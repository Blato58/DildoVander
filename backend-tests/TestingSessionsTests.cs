using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.TestingSessions;
using Kufr.Backend.Features.Wands;
using Microsoft.Extensions.Configuration;

internal static class TestingSessionsTests
{
    internal static async Task RunOptionalEnrichmentAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"testing-optional-enrichment-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                {["Backend:DatabasePath"]=Path.Combine(root,"backend.sqlite")}).Build();
            var characterProvider=new CharacterDouble { Fail=true };
            using var resources=new TestingResources(configuration,TimeProvider.System,null,null,characterProvider,null);
            var lab=new PersonaLabStore(Path.Combine(root,"lab.sqlite"),LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId,new GptLiveOptions()));
            using var live=new GptLiveClient(configuration);using var realtime=new OpenAiRealtimeClient(configuration);
            using var runtime=new PersonaLabRuntime(lab,live,realtime);
            using var hardware=new TestingHardwareAdapter(configuration);
            var service=new TestingSessionService(resources,lab,runtime,hardware,TimeProvider.System);
            var character=await service.SaveCharacterAsync("alice",null,
                new(new("Neville","Wizarding World"),true),default);
            var queued=await service.EnrichAsync("alice",character.Id,true,default);
            Check(queued.Profile.Status=="queued","AI enrichment is durably queued without holding the request open");
            Check(await resources.ProcessNextCharacterEnrichmentAsync(default),"isolated worker consumes the queued job");
            var failed=(await service.CharactersAsync("alice",default)).Single();
            Check(failed.Profile.Status=="failed"&&failed.Profile.ErrorCode=="character_provider_error",
                "provider failure is visible in profile status");
            var staleRunning=failed.Profile with {Status="running",ErrorCode=null};
            Check(service.StoreEnrichedCharacter("alice",failed.Id,staleRunning).Profile.Status=="failed",
                "a stale polling response cannot replace a same-revision terminal enrichment result");
            var session=service.Create("alice",new(OpenAiConsent:true,RecordContent:true,OperationId:Guid.NewGuid().ToString()));
            var selected=await service.SelectCharacterAsync("alice",session.Id,new(failed.Id,session.Revision),default);
            Check(selected.Conversation.Testing?.CharacterContext.Contains("Neville")==true
                &&selected.Conversation.Testing.CharacterContext.Contains("Wizarding World"),
                "a character with only its name and world remains selectable after optional enrichment fails");
            var fenced=await service.SaveCharacterAsync("bob",null,
                new(new("Alice","Wonderland"),true),default);
            resources.SessionsStore.Fence("bob");
            Check(await resources.ProcessNextCharacterEnrichmentAsync(default),"worker retires queued work discovered after the account deletion fence");
            Check(characterProvider.Calls==1&&await resources.Profiles("bob",fenced.Id).ReadAsync("bob") is null,
                "worker does not dispatch consented work for an account already fenced");
            var deleted=await service.SaveCharacterAsync("carol",null,
                new(new("Dorothy","Oz"),true),default);
            await service.DeleteCharacterAsync("carol",deleted.Id,default);
            Check(!await resources.ProcessNextCharacterEnrichmentAsync(default)&&characterProvider.Calls==1,
                "a deleted character's queued work cannot reach its provider");
            Console.WriteLine("Optional tester enrichment queues, persists provider failure and leaves name/world character usable (controlled fake provider).");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root,true);
        }
    }

    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"testing-session-journey-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {["Backend:DatabasePath"]=Path.Combine(root,"production.sqlite")}).Build();
        try
        {
            var characterProvider=new CharacterDouble();
            using var resources=new TestingResources(configuration,TimeProvider.System,new WandDouble(),new FakeKeepsakeProvider(),characterProvider,
                new WandDecisionService((input,token)=>Task.FromResult(new WandDecisionResult(WandDecisionStatus.ValidProposal,2,
                    [new(input.Evidence[0].Reference,input.Evidence[0].Text[..Math.Min(80,input.Evidence[0].Text.Length)],"profile context")],"",input.Revision,input.AvailabilityRevision))));
            var baseline=LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId,new GptLiveOptions());
            var lab=new PersonaLabStore(Path.Combine(root,"lab.sqlite"),baseline);
            using var live=new GptLiveClient(configuration);using var realtime=new OpenAiRealtimeClient(configuration);
            using var runtime=new PersonaLabRuntime(lab,live,realtime);
            using var hardware=new TestingHardwareAdapter(configuration);
            var service=new TestingSessionService(resources,lab,runtime,hardware,TimeProvider.System);
            var request=new TestingSessionRequest(OpenAiConsent:true,RecordContent:true,OperationId:Guid.NewGuid().ToString());
            var created=service.Create("alice",request);
            Check(service.Create("alice",request).Id==created.Id,"same operation is idempotent without a second provider session");
            Check(created.VirtualDeviceId=="test-"+created.Id&&created.InputMode=="text","one click creates virtual identity and defaults typed input");
            Check(service.Sessions("bob").Length==0,"other account list excludes testing data");
            try{service.Read("bob",created.Id);throw new Exception("cross-account read accepted");}catch(TestingException e)when(e.Status==404){}
            var first=await service.SaveCharacterAsync("alice",null,new(new("Marta","Clock library"),true,true),default);
            var second=await service.SaveCharacterAsync("alice",null,new(new("Alena","Sea library"),false),default);
            Check(service.Characters("alice").Length==2&&service.Characters("bob").Length==0,"multiple persistent test characters separated by account");
            first=await service.EnrichAsync("alice",first.Id,true,default);
            Check(first.Profile.Status=="queued"&&first.Profile.Enrichment is null,
                "requesting optional character enrichment queues work without waiting for OpenAI");
            Check(await resources.ProcessNextCharacterEnrichmentAsync(default),"isolated test worker claims the queued character job");
            first=(await service.CharactersAsync("alice",default)).Single(c=>c.Id==first.Id);
            Check(first.Profile.Enrichment?.Model=="controlled-character-double","character enrichment runs the existing provider contract and persists result");
            characterProvider.Fail=true;
            var partial=await service.SaveCharacterAsync("alice",null,new(new("Neville","Wizarding World")),default);
            partial=await service.EnrichAsync("alice",partial.Id,true,default);
            Check(partial.Profile.Status=="queued","optional enrichment request responds before provider completion");
            Check(await resources.ProcessNextCharacterEnrichmentAsync(default),"test worker processes an optional enrichment failure");
            partial=(await service.CharactersAsync("alice",default)).Single(c=>c.Id==partial.Id);
            Check(partial.Profile.Status=="failed"&&partial.Profile.ErrorCode=="character_provider_error",
                "provider failure is persisted while the name-and-world character remains usable");
            characterProvider.Fail=false;
            var active=await service.SelectCharacterAsync("alice",created.Id,new(first.Id,created.Revision),default);
            Check(active.Conversation.Testing?.CharacterContext.Contains("Clock library")==true,"actual selected character enters existing conversation context");
            await service.SaveCharacterAsync("alice",first.Id,new(new("Changed future profile","Different library"),false,true,first.Profile.Revision),default);
            Check((await service.DetailAsync("alice",created.Id,default)).Character?.Profile.UserInput.Name=="Marta",
                "editing saved character does not change active immutable conversation/wand identity");
            lab.Change("alice",created.LabSessionId,s=>s with {Turns=[new("user-fixture","user","I repair clocks.",DateTimeOffset.UtcNow),new("reply-fixture","assistant","Fixture provider reply.",DateTimeOffset.UtcNow)]});
            await service.CompleteTurnAsync(runtime.Detail("alice",created.LabSessionId),default);
            var remembered=await service.DetailAsync("alice",created.Id,default);
            Check(remembered.Memory.Records.Any(m=>m.Text.Contains("repair clocks")),"canonical isolated memory persists actual permitted user evidence");
            var selected=await service.SelectAsync("alice",created.Id,default);
            Check(selected.Wand?.SelectedWand?.Name.Contains("Marta")==true,"generated identity is character dependent, physical slot remains a position");
            Check(selected.Hardware.Events.Any(e=>e.Type=="presentation_completed"&&e.OperationId==Guid.ParseExact(created.Id,"N").ToString("D")),
                "virtual effects come from actual terminal production C++ acknowledgement");
            Check(selected.Session.KeepsakeId is not null&&selected.Keepsakes.Length==1,"accepted virtual outcome enters real persistent keepsake store");
            var generated=await service.GenerateAsync("alice",created.Id,default);
            Check(generated.Keepsakes[0].Editions.Count==1&&generated.Keepsakes[0].Artwork?.AssetId is not null,
                "controlled provider outputs flow through existing artwork and story jobs and asset persistence");
            var partialSession=service.Create("alice",request with {OperationId=Guid.NewGuid().ToString()});
            var partialDetail=await service.SelectCharacterAsync("alice",partialSession.Id,
                new(partial.Id,partialSession.Revision),default);
            Check(partialDetail.Conversation.Testing?.CharacterContext.Contains("Neville")==true,
                "a tester can start with name and world when optional AI context failed");
            await service.DeleteAsync("alice",partialSession.Id,default);
            var returning=service.Create("alice",request with {OperationId=Guid.NewGuid().ToString()});
            var returningDetail=await service.SelectCharacterAsync("alice",returning.Id,new(first.Id,returning.Revision),default);
            Check(returningDetail.Memory.Records.Any(m=>m.Text.Contains("repair clocks")),
                "same authenticated test character can retrieve authorized persistent personal memory in a second session");
            var editable=service.Characters("alice").Single(c=>c.Id==first.Id);
            await service.SaveCharacterAsync("alice",first.Id,new(editable.Profile.UserInput,false,false,editable.Profile.Revision),default);
            var withdrawn=await service.DetailAsync("alice",created.Id,default);
            Check(!withdrawn.Session.LongTermConsent&&withdrawn.Memory.Records.All(record=>record.Scope!=Kufr.Backend.Features.Memory.MemoryScope.Personal),
                "explicit consent withdrawal immediately revokes existing active returning-memory bindings");
            await service.DeleteAsync("alice",returning.Id,default);
            var opponent=service.Create("alice",request with {OperationId=Guid.NewGuid().ToString()});
            await service.SelectCharacterAsync("alice",opponent.Id,new(second.Id,opponent.Revision),default);
            await service.SelectAsync("alice",opponent.Id,default);
            var duel=service.CreateDuel("alice",created.Id,new(opponent.Id,"interactive",Guid.NewGuid().ToString()));
            duel=service.StartDuel("alice",created.Id,duel.Id,new(duel.Revision,Guid.NewGuid().ToString()));
            duel=await service.DuelMoveAsync("alice",created.Id,duel.Id,new("left",1,duel.Revision,"strike",Guid.NewGuid().ToString()),default);
            duel=await service.DuelMoveAsync("alice",created.Id,duel.Id,new("right",1,duel.Revision,"shield",Guid.NewGuid().ToString()),default);
            Check(duel.Rounds.Count==1&&runtime.Detail("alice",created.LabSessionId).Events.Any(e=>e.Type=="duel_round"),
                "actual character duel rules persist round outcome and separately acknowledged virtual native effect");
            var effectCount=runtime.Detail("alice",created.LabSessionId).Events.Count(e=>e.Type=="duel_round");
            await service.ReadDuelAsync("alice",created.Id,duel.Id,default);
            Check(runtime.Detail("alice",created.LabSessionId).Events.Count(e=>e.Type=="duel_round")==effectCount,
                "reading duel history never starts or replays hardware effects");
            await service.DeleteAsync("alice",opponent.Id,default);
            var resumed=new TestingSessionService(resources,lab,runtime,hardware,TimeProvider.System);
            Check(resumed.Read("alice",created.Id).KeepsakeId==selected.Session.KeepsakeId,"session and results resume from durable IDs");
            var mode=resumed.Mode("alice",created.Id,new("hybrid",resumed.Read("alice",created.Id).Revision));
            Check(mode.CharacterId==first.Id&&runtime.Detail("alice",created.LabSessionId).Turns.Length==2,"modality change retains identity and real retained turns");
            await resumed.FaultAsync("alice",created.Id,"disconnect",default);
            var recovered=await resumed.FaultAsync("alice",created.Id,"recover",default);
            Check(recovered.Hardware.Connected&&recovered.Hardware.Ready,"disconnect recovery first reconnects and then inspects virtual hardware");
            await resumed.EndAsync("alice",created.Id,default);
            var ended=await resumed.DetailAsync("alice",created.Id,default);
            Check(ended.Session.Status=="ended"&&ended.Conversation.Status=="ended","ending retires existing provider transport and persists completion");
            await resumed.DeleteAsync("alice",created.Id,default);
            Check(resumed.Sessions("alice").Length==0&&resumed.Characters("alice").Length==3,"session deletion removes exact test data and preserves explicit character library");
            var otherOwner=service.Create("bob",request with {OperationId=Guid.NewGuid().ToString()});
            await resumed.DeleteAccountAsync("alice");
            Check(resumed.Characters("alice").Length==0&&resumed.Sessions("alice").Length==0
                &&resumed.Read("bob",otherOwner.Id).Status=="open","account deletion purges only the exact test owner's documents");
            var memoryDevice="test-memory-"+TestingSessionStore.OwnerKey("alice");
            var memoryAccess=new Kufr.Backend.Features.Memory.MemoryAccess(memoryDevice,
                resources.MemoryStore.GetStatus(memoryDevice).EveningId,created.Id,PersonalityCatalog.OllivanderId,
                resources.MemoryStore.GuestPermission(memoryDevice,created.Id));
            Check(resources.MemoryStore.LocalMemory(memoryAccess).Count==0,
                "test-account deletion removes personal and evening memory rather than retaining authorized returning facts");
            try{resumed.Create("alice",request with {OperationId=Guid.NewGuid().ToString()});throw new Exception("deleted test account resurrected");}
            catch(TestingException e)when(e.Code=="test_account_deleted"){}
            Check(!File.Exists(Path.Combine(root,"production.sqlite")),"full journey never creates or mutates production database");
            Console.WriteLine("Testing sessions create/resume, multiple characters, provider-double generation, canonical memory, native acknowledged effects, real archive jobs, isolation, modality and lifecycle checks passed (no paid provider or physical acceptance).");
        }
        finally{try{Directory.Delete(root,true);}catch(IOException){}}
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class CharacterDouble:ICharacterEnrichmentProvider
    {
        internal bool Fail { get; set; }
        internal int Calls { get; private set; }
        public Task<CharacterEnrichment> EnrichAsync(CharacterJob job,CancellationToken token)
        {
            Calls++;
            if(Fail)throw new InvalidDataException("controlled failure");
            return Task.FromResult(new CharacterEnrichment(job.UserInput.Name,
                job.UserInput.World,"Clock repairer",[],[],[],false,[],"controlled-character-double","test-v1",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),job.Revision,"synthetic"));
        }
    }
    private sealed class WandDouble:IWandGenerationProvider
    {
        public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job,CancellationToken token)=>Task.FromResult(
            WandGenerationValidation.Slots(job.Request.ReadyMask).Select(slot=>new WandCandidateContent(job.Request.Profile.UserInput.Name+" — Astrální ozvěna "+
                new[]{"Jitřní","Modrá","Zelená","Stříbrná","Rudá","Večerní"}[slot-1],"Fictional moonwood","Imagined constellation","Patient",
                "A fiction about a lost constellation.","A digital wand woven from star charts","Stars","Cloudy skies",
                new[]{"strike","trick","shield"}.Select(a=>new WandSpell(a,job.Request.Profile.UserInput.Name+slot+a,"Fictional magical stars")).ToArray())).ToArray());
    }
}
