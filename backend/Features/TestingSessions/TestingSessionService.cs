using System.Collections.Concurrent;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.TestingSessions;

internal sealed partial class TestingSessionService
{
    private readonly TestingSessionStore store;
    private readonly TestingResources resources;
    private readonly PersonaLabStore lab;
    private readonly PersonaLabRuntime runtime;
    private readonly TestingHardwareAdapter hardware;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string,CancellationTokenSource> operations=new();
    public TestingSessionService(TestingResources resources,PersonaLabStore lab,PersonaLabRuntime runtime,
        TestingHardwareAdapter hardware,TimeProvider clock)
    {
        this.resources=resources;this.lab=lab;this.runtime=runtime;this.hardware=hardware;this.clock=clock;
        store=resources.SessionsStore;
        resources.ResolveDuelIdentity=ResolveDuelIdentity;
    }
    internal TestingSession[] Sessions(string owner)=>store.List<TestingSession>(owner,"session").OrderByDescending(x=>x.CreatedAt).ToArray();
    internal TestingCharacter[] Characters(string owner)=>store.List<TestingCharacter>(owner,"character");
    internal async Task<TestingCharacter[]> CharactersAsync(string owner,CancellationToken token)
    {
        var characters=Characters(owner);
        foreach(var character in characters)
        {
            var profile=await resources.Profiles(owner,character.Id).ReadAsync(owner,token);
            if(profile is not null)StoreEnrichedCharacter(owner,character.Id,profile);
        }
        return Characters(owner);
    }
    internal TestingSession Read(string owner,string id)=>store.Read<TestingSession>(owner,"session",id);
    internal TestingSession RequireOpen(string owner,string id)
    {
        var s=Read(owner,id);
        if(store.IsFenced(owner)||s.Status!="open")throw new TestingException(409,"test_session_closed");
        return s;
    }
    internal TestingSession Create(string owner,TestingSessionRequest request)
    {
        if(!LabContract.Engine(request.Engine)||request.InputMode is not("text" or "voice" or "hybrid")
            ||request.Platform is not("web" or "ios")||!Guid.TryParse(request.OperationId,out _))
            throw new TestingException(400,"invalid_test_session");
        if(!request.OpenAiConsent||!request.RecordContent)throw new TestingException(400,"test_content_consent_required");
        lock(gate)
        {
            if(store.IsFenced(owner))throw new TestingException(409,"test_account_deleted");
            if(Sessions(owner).FirstOrDefault(x=>x.OperationId==request.OperationId) is {} old)
            {
                if(old.Engine!=request.Engine||old.InputMode!=request.InputMode)throw new TestingException(409,"test_operation_conflict");
                return old;
            }
            if(Sessions(owner).Length>=200)throw new TestingException(429,"test_session_capacity");
            var id=Guid.NewGuid().ToString("N");
            var conversation=lab.CreateSession(owner,new(lab.ActiveRevisionId,request.Engine,"first-visit","empty",
                "Testing session "+id[..8],RecordContent:true,Platform:request.Platform));
            lab.Change(owner,conversation.Id,value=>value with {Testing=new(id,"","",[])});
            var s=new TestingSession(id,owner,"test-"+id,conversation.Id,
                request.Engine,request.InputMode,"open",null,clock.GetUtcNow(),null,1,true,OperationId:request.OperationId);
            store.Put(owner,"session",id,s);
            return s;
        }
    }
    internal async Task<TestingCharacter> SaveCharacterAsync(string owner,string? id,TestingCharacterRequest request,CancellationToken token)
    {
        id??=Guid.NewGuid().ToString("N");
        if(!Guid.TryParseExact(id,"N",out _))throw new TestingException(400,"invalid_test_character");
        var old=store.TryRead<TestingCharacter>(owner,"character",id);
        if(old is null&&request.ExpectedRevision!=0)throw new TestingException(409,"character_revision_conflict");
        var profiles=resources.Profiles(owner,id);
        var current=await profiles.ReadAsync(owner,token);
        var profile=await profiles.SaveAsync(owner,new(request.ExpectedRevision,request.UserInput,
            current?.ProfileId,OpenAiConsent:request.OpenAiConsent),token);
        var value=new TestingCharacter(id,owner,profile,request.LongTermConsent);
        lock(gate)
        {
            var latest=store.TryRead<TestingCharacter>(owner,"character",id);
            if(latest is not null&&latest.Profile.Revision>value.Profile.Revision)return latest;
            store.Put(owner,"character",id,value);
            if(old?.LongTermConsent==true&&!request.LongTermConsent)
            {
                foreach(var captured in Sessions(owner).Where(s=>s.CharacterId==id&&s.LongTermConsent))
                {
                    var active=Read(owner,captured.Id);
                    if(!active.LongTermConsent)continue;
                    CancelOperation(active.Id);runtime.Cancel(owner,active.LabSessionId);
                    var access=Access(active);var permission=resources.MemoryStore.GuestPermission(access.DeviceId,active.Id);
                    if(permission is not null)resources.MemoryStore.SetGuestPermission(access.DeviceId,active.Id,permission.GuestId,
                        "authenticated_test_consent_withdrawal",true,false,false,permission.Version);
                    var updated=active with {LongTermConsent=false,Revision=active.Revision+1};
                    store.Put(owner,"session",active.Id,updated);
                    resources.HybridMemory.ClearCache();
                    var memory=resources.HybridMemory.BuildContext(Access(updated));
                    lab.Change(owner,active.LabSessionId,session=>session with {Testing=session.Testing! with {MemoryContext=memory}});
                }
            }
        }
        return value;
    }
    internal async Task<TestingCharacter> EnrichAsync(string owner,string id,bool consent,CancellationToken token)
    {
        if(!consent)throw new TestingException(400,"openai_consent_required");
        _=store.Read<TestingCharacter>(owner,"character",id);
        var profiles=resources.Profiles(owner,id);
        var previous=await profiles.ReadAsync(owner,token)??throw new TestingException(404,"test_character_unavailable");
        if(previous.Status is not("queued" or "running"))
        {
            previous=await profiles.CorrectAsync(owner,new(previous.Revision,previous.ProfileId,true),token);
        }
        return StoreEnrichedCharacter(owner,id,previous);
    }
    internal TestingCharacter StoreEnrichedCharacter(string owner,string id,CharacterProfile profile)
    {
        lock(gate)
        {
            var latest=store.Read<TestingCharacter>(owner,"character",id);
            if(latest.Profile.Revision>profile.Revision)return latest;
            if(latest.Profile.Revision==profile.Revision&&IsTerminalProfile(latest.Profile.Status)
                &&!IsTerminalProfile(profile.Status))return latest;
            var updated=latest with {Profile=profile};
            store.Put(owner,"character",id,updated);return updated;
        }
    }
    private static bool IsTerminalProfile(string status)=>status is "saved" or "ready" or "needs_clarification" or "failed";
    internal async Task<TestingSessionDetail> SelectCharacterAsync(string owner,string id,TestingCharacterSelection request,CancellationToken token)
    {
        lock(gate)
        {
            var s=RequireOpen(owner,id);var c=store.Read<TestingCharacter>(owner,"character",request.CharacterId);
            if(s.Revision!=request.ExpectedRevision)throw new TestingException(409,"test_revision_conflict");
            var conversation=runtime.Detail(owner,s.LabSessionId);
            if(runtime.IsActive(s.LabSessionId)||conversation.Turns.Length>0)throw new TestingException(409,"test_conversation_started");
            s=s with {CharacterId=c.Id,LongTermConsent=c.LongTermConsent,CharacterSnapshot=new(c.Profile.ProfileId,c.Profile.Revision,c.Profile.UserInput,c.Profile.Enrichment,clock.GetUtcNow().ToUnixTimeSeconds()),Revision=s.Revision+1};
            store.Put(owner,"session",id,s);
            var access=Access(s);
            resources.MemoryStore.RegisterMemoryContext(access);
            resources.MemoryStore.SetGuestPermission(access.DeviceId,id,"test-"+c.Id,"authenticated_test_account_character",
                true,c.LongTermConsent,false,resources.MemoryStore.GuestPermission(access.DeviceId,id)?.Version??0);
            var context=JsonSerializer.Serialize(new {c.Profile.UserInput,c.Profile.Enrichment},new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var memory=resources.HybridMemory.BuildContext(Access(s));
            lab.Change(owner,s.LabSessionId,value=>value with {Testing=new(id,context,memory,[])});
        }
        return await DetailAsync(owner,id,token);
    }
    internal async Task<TestingSessionDetail> DetailAsync(string owner,string id,CancellationToken token)
    {
        var s=Read(owner,id);
        var snapshot=s.Status=="ended" ? store.TryRead<TestingHardware>(owner,"hardware",id) ?? new(false,false,"ended",[],null,"ended","off",null,null,false,[]) : await hardware.SnapshotAsync(id,s.VirtualDeviceId,token);
        if(s.Status=="open")snapshot=PersistHardware(owner,id,snapshot);
        var wand=resources.Wands.Read(owner,id);
        if(wand is not null)snapshot=snapshot with {Compartments=snapshot.Compartments.Select(slot=>slot with
            {WandName=wand.Candidates.FirstOrDefault(w=>w.PhysicalSlot==slot.Slot)?.Name}).ToArray()};
        var memories=resources.MemoryStore.LocalMemory(Access(s));
        return new(s,runtime.Detail(owner,s.LabSessionId),CharacterFor(s),
            snapshot,wand,s.KeepsakeId is {} k?[resources.Keepsakes.Read(k,Actor(s))]:[],new("real_isolated_local_store",memories),runtime.IsActive(s.LabSessionId));
    }
    internal TestingSession Mode(string owner,string id,TestingModeRequest request)
    {
        lock(gate)
        {
            var s=RequireOpen(owner,id);
            if(request.InputMode is not("text" or "voice" or "hybrid"))throw new TestingException(400,"invalid_input_mode");
            if(s.Revision!=request.ExpectedRevision)throw new TestingException(409,"test_revision_conflict");
            var value=s with {InputMode=request.InputMode,Revision=s.Revision+1};store.Put(owner,"session",id,value);return value;
        }
    }
    internal async Task<TestingSession> EndAsync(string owner,string id,CancellationToken token)
    {
        TestingSession s;
        lock(gate)
        {
            s=Read(owner,id);
            if(s.Status!="ended")
            {
                s=s with {Status="ended",EndedAt=clock.GetUtcNow(),Revision=s.Revision+1};store.Put(owner,"session",id,s);
            }
            CancelOperation(id);runtime.End(owner,s.LabSessionId);
        }
        Exception? memoryFailure=null;
        try
        {
            if(s.CharacterId is not null&&!store.IsFenced(owner))
                await CompleteTurnAsync(runtime.Detail(owner,s.LabSessionId),CancellationToken.None);
        }
        catch(Exception error) when(error is not OutOfMemoryException) { memoryFailure=error; }
        try
        {
            // End owns disposal even if memory finalization or the requesting
            // client failed. The adapter bounds its own shutdown and always kills.
            var shutdown=await hardware.CommandAsync(id,s.VirtualDeviceId,"shutdown",null,null,CancellationToken.None);
            PersistHardware(owner,id,shutdown);
        }
        catch(Exception) when(memoryFailure is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(memoryFailure).Throw();throw; }
        if(memoryFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(memoryFailure).Throw();
        return s;
    }
    internal async Task CancelAsync(string owner,string id,CancellationToken token)
    {
        var s=RequireOpen(owner,id);CancelOperation(id);
        runtime.Cancel(owner,s.LabSessionId);
        await hardware.CommandAsync(id,s.VirtualDeviceId,"stop",null,null,token);
    }
    private void CancelOperation(string id){if(operations.TryGetValue(id,out var operation))operation.Cancel();}
    internal async Task DeleteAsync(string owner,string id,CancellationToken token)
    {
        var s=Read(owner,id);await EndAsync(owner,id,token);
        if(runtime.IsActive(s.LabSessionId)||operations.ContainsKey(id))throw new TestingException(409,"test_operation_cancelling");
        if(s.KeepsakeId is {} k)resources.Keepsakes.Delete(k,Actor(s),KeepsakeDeleteScope.All,Guid.NewGuid().ToString());
        resources.Wands.DeleteOperation(owner,id);
        resources.Duels.PurgeIdentity(id);
        resources.MemoryStore.DeleteScopedMemory(MemoryDevice(owner),"conversation",id,resources.MemoryStore.MemoryEpoch);
        resources.MemoryStore.ScrubAccountConversation(MemoryDevice(owner),id);
        runtime.Delete(owner,s.LabSessionId);await hardware.DeleteAsync(id,s.VirtualDeviceId,token);store.Remove(owner,"session",id);store.Remove(owner,"hardware",id);
    }
    internal async Task DeleteCharacterAsync(string owner,string id,CancellationToken token)
    {
        var character=store.Read<TestingCharacter>(owner,"character",id);
        await resources.Profiles(owner,id).FenceAccountDeletionAsync(owner,CancellationToken.None);
        foreach(var session in Sessions(owner).Where(s=>s.CharacterId==id&&s.Status=="open"))
            await EndAsync(owner,session.Id,token);
        await resources.Profiles(owner,id).DeleteAsync(owner,character.Profile.Revision,character.Profile.ProfileId,token);
        resources.Wands.DeleteProfile(owner,character.Profile.ProfileId);
        resources.MemoryStore.DeleteScopedMemory(MemoryDevice(owner),"guest","test-"+id,resources.MemoryStore.MemoryEpoch);
        store.Remove(owner,"character",id);
    }
    internal async Task DeleteAccountAsync(string owner,CancellationToken token=default)
    {
        store.Fence(owner);
        var characters=Characters(owner);
        foreach(var character in characters)
        {
            await resources.Profiles(owner,character.Id)
                .FenceAccountDeletionAsync(owner,CancellationToken.None);
        }
        foreach(var s in Sessions(owner))
        {
            CancelOperation(s.Id);runtime.End(owner,s.LabSessionId);
            if(s.KeepsakeId is {} k)resources.Keepsakes.Delete(k,Actor(s),KeepsakeDeleteScope.All,Guid.NewGuid().ToString());
            resources.Wands.DeleteOperation(owner,s.Id);
            resources.Duels.PurgeIdentity(s.Id);
            resources.MemoryStore.DeleteScopedMemory(MemoryDevice(owner),"conversation",s.Id,resources.MemoryStore.MemoryEpoch);
            resources.MemoryStore.ScrubAccountConversation(MemoryDevice(owner),s.Id);
            await hardware.DeleteAsync(s.Id,s.VirtualDeviceId,token);
            store.Remove(owner,"session",s.Id);store.Remove(owner,"hardware",s.Id);
        }
        foreach(var c in characters)
        {
            await resources.Profiles(owner,c.Id).DeleteAsync(owner,c.Profile.Revision,c.Profile.ProfileId,token);
            resources.MemoryStore.DeleteScopedMemory(MemoryDevice(owner),"guest","test-"+c.Id,resources.MemoryStore.MemoryEpoch);
            store.Remove(owner,"character",c.Id);
        }
        foreach(var evening in resources.MemoryStore.List(MemoryDevice(owner)).Evenings)
            resources.MemoryStore.DeleteScopedMemory(MemoryDevice(owner),"evening",evening.EveningId,resources.MemoryStore.MemoryEpoch);
        resources.HybridMemory.ClearCache();
        resources.Wands.DeleteAccount(owner);
        store.PurgeOwnerContent(owner);
    }
    private TestingHardware PersistHardware(string owner,string id,TestingHardware snapshot)
    {
        var prior=store.TryRead<TestingHardware>(owner,"hardware",id);
        var combined=snapshot with {Events=(prior?.Events??[]).Concat(snapshot.Events).DistinctBy(e=>e.Id).TakeLast(200).ToArray()};
        store.Put(owner,"hardware",id,combined);return combined;
    }
    private TestingCharacter? CharacterFor(TestingSession s)
    {
        if(s.CharacterId is not {} id||s.CharacterSnapshot is not {} captured)return null;
        return new(id,s.OwnerId,new(captured.ProfileId,captured.Revision,captured.UserInput,
            captured.Enrichment is null?"saved":"ready",captured.Enrichment,null,captured.CapturedAt),s.LongTermConsent);
    }
    private static string MemoryDevice(string owner)=>"test-memory-"+TestingSessionStore.OwnerKey(owner);
    private MemoryAccess Access(TestingSession s)
    {
        var device=MemoryDevice(s.OwnerId);var evening=resources.MemoryStore.GetStatus(device).EveningId;
        return new(device,evening,s.Id,PersonalityCatalog.OllivanderId,resources.MemoryStore.GuestPermission(device,s.Id));
    }
    private KeepsakeActor Actor(TestingSession s,bool requireOpen=false)=>KeepsakeActor.ForAccount(s.OwnerId,"test-"+s.Id,()=>
        store.TryRead<TestingSession>(s.OwnerId,"session",s.Id) is {} current&&(!requireOpen||current.Status=="open"));
    internal async Task ValidateAsync(LabSession conversation,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var s=RequireOpen(conversation.OwnerId,conversation.Testing?.ScopeId??"");
        if(operations.ContainsKey(s.Id))throw new TestingException(409,"test_operation_busy");
        if(s.LabSessionId!=conversation.Id||s.CharacterId is null)throw new TestingException(409,"test_character_required");
        var snapshot=await hardware.SnapshotAsync(s.Id,s.VirtualDeviceId,token);
        if(!snapshot.Connected||!snapshot.Ready)throw new TestingException(409,"virtual_hardware_unavailable");
        if(s.Engine=="gpt-realtime-2.1"&&conversation.Testing!.Candidates.Length==0)
            await PrepareCandidatesAsync(s,token);
    }
    internal async Task CompleteTurnAsync(LabSession conversation,CancellationToken token)
    {
        var s=Read(conversation.OwnerId,conversation.Testing!.ScopeId);
        if(store.IsFenced(s.OwnerId)||s.LabSessionId!=conversation.Id)return;
        var turns=conversation.Turns;
        var access=Access(s);
        var evidence=turns.Where(t=>t.Role=="user"&&t.Text.Length>0).TakeLast(32)
            .Select(t=>new DecisionEvidence("turn-"+t.Id,LiveTextBudget.Bound(t.Text,1000))).ToArray();
        var segments=turns.Where(t=>t.Text.Length>0).TakeLast(60).Select((t,i)=>
            new LiveTranscriptSegment("turn-"+t.Id,t.Role,t.Text,i*1000,(i+1)*1000,t.Id)).ToArray();
        await resources.HybridMemory.PersistContextAsync(access,conversation.Id,segments,evidence,
            resources.HybridMemory.StoreEpochFor(access),token);
        foreach(var item in evidence)
        {
            var intent=LiveConversationProjection.Classify(item.Text);
            if(intent is LiveRequestIntent.Remember or LiveRequestIntent.Forget)
            {
                var key=s.Id+":"+item.Reference;
                if(store.TryRead<string>(s.OwnerId,"memory-event",key) is null)
                {
                    var result=await resources.HybridMemory.ChangePersonalMemoryAsync(access,intent,item,token);
                    store.Put(s.OwnerId,"memory-event",key,result.Status);
                }
            }
        }
        var memory=resources.HybridMemory.BuildContext(access);
        lab.Change(s.OwnerId,s.LabSessionId,value=>value with {Testing=value.Testing! with {MemoryContext=memory}});
    }
}

internal sealed class TestingConversationBridge(IServiceProvider services):ITestingConversationBridge
{
    private TestingSessionService Service=>services.GetRequiredService<TestingSessionService>();
    private static async Task Guard(Func<Task> call)
    {try{await call();}catch(TestingException e){throw new LabException(e.Status,e.Code);}catch(WandGenerationException e){throw new LabException(e.Status,e.Code);}}
    public Task ValidateAsync(LabSession session,CancellationToken token)=>Guard(()=>Service.ValidateAsync(session,token));
    public async Task<string> DelegateAsync(LabSession session,CancellationToken token)
    {try{return await Service.DelegateAsync(session,token);}catch(TestingException e){throw new LabException(e.Status,e.Code);}catch(WandGenerationException e){throw new LabException(e.Status,e.Code);}}
    public Task AcceptDecisionAsync(LabSession session,string decision,int? compartment,CancellationToken token)=>Guard(()=>Service.AcceptDecisionAsync(session,decision,compartment,token));
    public Task CompleteTurnAsync(LabSession session,CancellationToken token)=>Guard(()=>Service.CompleteTurnAsync(session,token));
}
