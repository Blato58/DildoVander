using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.TestingSessions;

internal sealed partial class TestingSessionService
{
    internal async Task<string> DelegateAsync(LabSession conversation,CancellationToken token)
    {
        var s=RequireOpen(conversation.OwnerId,conversation.Testing!.ScopeId);
        var result=await DecideAsync(s.OwnerId,s.Id,token);
        if(result.Status==WandDecisionStatus.ValidProposal&&result.Compartment is {} slot)
            await PresentDecisionAsync(s,slot,token);
        var selected=resources.Wands.Read(s.OwnerId,s.Id)?.SelectedWand;
        return JsonSerializer.Serialize(new {status=result.Status.ToString(),result.Compartment,result.Reasons,
            selectedWand=selected is null?null:new {selected.WandId,selected.Name,selected.Lore,selected.Material,selected.Core},
            result.MissingInformation,evidence="real_backend_decision",hardware="virtual_only"});
    }
    internal async Task AcceptDecisionAsync(LabSession conversation,string decision,int? compartment,CancellationToken token)
    {
        var s=RequireOpen(conversation.OwnerId,conversation.Testing!.ScopeId);
        if(decision is not("select" or "selected" or "propose")||compartment is null)return;
        // Realtime's tool proposal cannot manufacture candidate authority. A real
        // narrow decision, with original user evidence, is required for both engines.
        var validated=await DecideAsync(s.OwnerId,s.Id,token);
        if(validated.Status!=WandDecisionStatus.ValidProposal||validated.Compartment!=compartment)
            throw new TestingException(409,"test_decision_not_accepted");
        await PresentDecisionAsync(s,compartment.Value,token);
    }
    internal async Task<TestingSessionDetail> SelectAsync(string owner,string id,CancellationToken token)
    {
        var s=RequireOpen(owner,id);
        using var lifetime=BeginOperation(id,token);
        if(runtime.IsActive(s.LabSessionId))throw new TestingException(409,"test_conversation_busy");
        var result=await DecideAsync(owner,id,lifetime.Token);
        if(result.Status!=WandDecisionStatus.ValidProposal||result.Compartment is not {} slot)
            throw new TestingException(409,"test_decision_"+result.Status.ToString().ToLowerInvariant());
        await PresentDecisionAsync(s,slot,lifetime.Token);
        return await DetailAsync(owner,id,token);
    }
    private async Task<WandDecisionResult> DecideAsync(string owner,string id,CancellationToken token)
    {
        var s=RequireOpen(owner,id);
        if(s.CharacterId is not {} cid)throw new TestingException(409,"test_character_required");
        var conversation=runtime.Detail(owner,s.LabSessionId);
        var captured=s.CharacterSnapshot??throw new TestingException(409,"test_character_required");
        var evidence=conversation.Turns.Where(t=>t.Role=="user"&&t.Text.Length>0).TakeLast(24)
            .Select(t=>new DecisionEvidence("turn-"+t.Id,LiveTextBudget.Bound(t.Text,1000))).ToList();
        evidence.Insert(0,new("character-profile",LiveTextBudget.Bound(JsonSerializer.Serialize(captured),1000)));
        var generation=await PrepareCandidatesAsync(s,token);
        if(generation.SelectedWand is {} prior)return new(WandDecisionStatus.ValidProposal,prior.PhysicalSlot,[],"",s.Revision,0);
        if(!resources.Decisions.Available)throw new TestingException(503,"test_decision_provider_unavailable");
        var result=await resources.Decisions.DecideAsync(new(s.Revision,63,0,evidence,new(2,3,true),
            evidence.Count,true,MemoryContext:resources.HybridMemory.BuildContext(Access(s)),GeneratedWands:generation.Candidates),token);
        token.ThrowIfCancellationRequested();RequireOpen(owner,id);
        lab.Change(owner,s.LabSessionId,value=>value with {Events=[..value.Events,
            LabContract.Event("wand_decision",result.Status.ToString(),decision:result.OperationState,compartment:result.Compartment,costMicroUsd:result.CostMicroUsd)]});
        return result;
    }
    private async Task<WandGenerationSession> PrepareCandidatesAsync(TestingSession s,CancellationToken token)
    {
        var captured=s.CharacterSnapshot??throw new TestingException(409,"test_character_required");
        var conversation=runtime.Detail(s.OwnerId,s.LabSessionId);
        var evidence=conversation.Turns.Where(t=>t.Role=="user"&&t.Text.Length>0).TakeLast(24)
            .Select(t=>LiveTextBudget.Bound(t.Text,1000)).Prepend(LiveTextBudget.Bound(JsonSerializer.Serialize(captured),1000)).ToArray();
        resources.Wands.Begin(s.OwnerId,s.Id,captured,63,s.OpenAiConsent);
        var generated=await resources.WandService.GenerateAsync(s.OwnerId,s.Id,_=>Task.FromResult(IsOpen(s.OwnerId,s.Id)),token,
            retry:true,evidence:evidence);
        if(generated.Candidates.Length==0)throw new TestingException(502,generated.ErrorCode??"test_wand_generation_failed");
        lab.Change(s.OwnerId,s.LabSessionId,value=>value with {Testing=value.Testing! with {Candidates=generated.Candidates}});
        return generated;
    }
    private async Task PresentDecisionAsync(TestingSession original,int slot,CancellationToken token)
    {
        var s=RequireOpen(original.OwnerId,original.Id);
        var generated=resources.Wands.Read(s.OwnerId,s.Id)??throw new TestingException(409,"test_wand_candidates_unavailable");
        var selected=resources.Wands.Select(s.OwnerId,s.Id,generated.ProfileId,generated.ProfileRevision,slot);
        var operation=Guid.ParseExact(s.Id,"N").ToString("D");
        var acknowledged=await hardware.PresentAsync(s.Id,s.VirtualDeviceId,operation,slot,token);
        lab.Change(s.OwnerId,s.LabSessionId,value=>value with {Events=[..value.Events,
            LabContract.Event("virtual_hardware",acknowledged.State,decision:"proposal_accepted_simulation",compartment:slot)]});
        // Archive issuance is reconciled from a terminal native receipt below.
        // Merely accepting a proposal never proves completed movement.
        await ReconcileOutcomeAsync(s,selected,token);
    }
    private bool IsOpen(string owner,string id)=>!store.IsFenced(owner)&&store.TryRead<TestingSession>(owner,"session",id)?.Status=="open";
    private OperationLease BeginOperation(string id,CancellationToken token)
    {
        var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        if(!operations.TryAdd(id,lifetime)){lifetime.Dispose();throw new TestingException(409,"test_operation_busy");}
        return new(lifetime,()=>operations.TryRemove(id,out _));
    }
    private sealed class OperationLease(CancellationTokenSource source,Action release):IDisposable
    { internal CancellationToken Token=>source.Token;public void Dispose(){release();source.Dispose();} }
    internal async Task<TestingSessionDetail> GenerateAsync(string owner,string id,CancellationToken token)
    {
        var s=RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);
        if(s.KeepsakeId is not {} k)throw new TestingException(409,"test_accepted_outcome_required");
        var view=resources.Keepsakes.Read(k,Actor(s));
        if(!view.Jobs.Any(j=>j.State=="queued")&&view.Jobs.Any(j=>j.State is "awaiting_reconciliation" or "failed"))
            throw new TestingException(409,"test_artifact_retry_required");
        await resources.GenerateKeepsakeAsync(k,lifetime.Token);
        return await DetailAsync(owner,id,token);
    }
    internal async Task<TestingSessionDetail> RetryGenerationAsync(string owner,string id,TestingGenerationRetryRequest request,CancellationToken token)
    {
        var s=RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);
        if(s.KeepsakeId is not {} k)throw new TestingException(409,"test_accepted_outcome_required");
        var view=resources.Keepsakes.Read(k,Actor(s));
        var job=view.Jobs.SingleOrDefault(j=>j.Id==request.JobId)??throw new TestingException(404,"test_job_unavailable");
        if(job.State=="awaiting_reconciliation"&&!request.ConfirmAmbiguous)throw new TestingException(409,"test_ambiguous_retry_confirmation_required");
        if(job.State is not("awaiting_reconciliation" or "failed" or "cancelled"))throw new TestingException(409,"test_job_not_retryable");
        if(job.Kind=="wand_artwork")resources.Keepsakes.RetryWandArtwork(k,Actor(s),Guid.NewGuid().ToString());
        else
        {
            resources.Keepsakes.Cancel(k,Actor(s),job.Id,Guid.NewGuid().ToString());
            if(job.Kind=="base")resources.Keepsakes.EnqueueBase(k,Actor(s),new(),Guid.NewGuid().ToString());
            else if(job.Kind=="portrait"&&job.UploadId is {} upload&&job.EditionId is {} edition)
                resources.Keepsakes.EnqueuePortrait(k,Actor(s),upload,edition,"portrait_only",true,Guid.NewGuid().ToString());
            else throw new TestingException(409,"test_job_not_retryable");
        }
        await resources.GenerateKeepsakeAsync(k,lifetime.Token);RequireOpen(owner,id);return await DetailAsync(owner,id,token);
    }
    internal async Task<TestingSessionDetail> FaultAsync(string owner,string id,string fault,CancellationToken token)
    {
        var s=RequireOpen(owner,id);
        var action=fault switch {"disconnect"=>"disconnect","servo_failure"=>"fault","missing_camera"=>"camera", "recover"=>"recover","stop"=>"stop",_=>throw new TestingException(400,"invalid_test_fault")};
        CancelOperation(id);runtime.Cancel(owner,s.LabSessionId);
        if(fault=="recover")await hardware.CommandAsync(id,s.VirtualDeviceId,"reconnect",null,null,token);
        await hardware.CommandAsync(id,s.VirtualDeviceId,action,fault=="servo_failure"?"servo":fault=="missing_camera"?"missing":null,
            fault=="servo_failure"?1:null,token);
        return await DetailAsync(owner,id,token);
    }
    private DuelIdentitySnapshot? ResolveDuelIdentity(string identity,string device)
    {
        // The identity key is an exact test-session ID. The owning session is
        // looked up only through a previously authorized device binding.
        foreach(var owner in duelOwners.Values.Distinct())
        {
            var s=store.TryRead<TestingSession>(owner,"session",identity);
            if(s is null||device!=MemoryDevice(owner)||s.CharacterId is not {} cid)continue;
            var character=CharacterFor(s);
            var wand=resources.Wands.Read(owner,s.Id)?.SelectedWand;
            if(character is null||wand is null||s.KeepsakeId is null)return null;
            return DuelRules.CharacterSnapshot(identity,character.Profile.UserInput.Name,wand.ToPublic());
        }
        return null;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> duelOwners=new();
    internal Task<DuelView[]> DuelsAsync(string owner,string id,CancellationToken token)
    {
        Read(owner,id);var device=MemoryDevice(owner);duelOwners[device]=owner;
        resources.Duels.Tick();var result=resources.Duels.List(device,resources.MemoryStore.GetStatus(device).EveningId).ToArray();
        return Task.FromResult(result);
    }
    internal DuelView CreateDuel(string owner,string id,TestingDuelRequest request)
    {
        RequireOpen(owner,id);Read(owner,request.OtherSessionId);var device=MemoryDevice(owner);duelOwners[device]=owner;
        return resources.Duels.Create(device,new(resources.MemoryStore.GetStatus(device).EveningId,id,request.OtherSessionId,
            request.Mode,true,request.OperationId));
    }
    internal async Task<DuelView> ReadDuelAsync(string owner,string id,string duelId,CancellationToken token)
    { Read(owner,id);resources.Duels.Tick();var result=resources.Duels.Read(duelId,MemoryDevice(owner));return await Task.FromResult(result); }
    internal DuelView StartDuel(string owner,string id,string duelId,DuelCommand command)
    {
        RequireOpen(owner,id);var device=MemoryDevice(owner);duelOwners[device]=owner;
        var duel=resources.Duels.Read(duelId,device);
        if(duel.RequiresParticipantConsent)
        {
            foreach(var side in new[]{"left","right"})
            {
                var grant=resources.Duels.Grant(device,duelId,new(side,duel.Revision,Guid.NewGuid().ToString()));
                duel=resources.Duels.Join(duelId,grant.Token,new(side,true,Guid.NewGuid().ToString()));
            }
            command=command with {Revision=duel.Revision};
        }
        return resources.Duels.Start(device,duelId,command);
    }
    internal async Task<DuelView> DuelMoveAsync(string owner,string id,string duelId,DuelChoiceRequest choice,CancellationToken token)
    {
        RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);var device=MemoryDevice(owner);var duel=resources.Duels.Read(duelId,device);
        var grant=resources.Duels.Grant(device,duelId,new(choice.Side,duel.Revision,Guid.NewGuid().ToString()));
        resources.Duels.Join(duelId,grant.Token,new(choice.Side,true,Guid.NewGuid().ToString()));
        resources.Duels.Choose(duelId,grant.Token,choice);
        resources.Duels.Tick();var result=resources.Duels.Read(duelId,device);
        await DispatchDuelEffectsAsync(owner,result,id,lifetime.Token);return result;
    }
    internal async Task<DuelView> AdvanceDuelAsync(string owner,string id,string duelId,CancellationToken token)
    {
        RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);resources.Duels.Read(duelId,MemoryDevice(owner));
        resources.Duels.Tick();var result=resources.Duels.Read(duelId,MemoryDevice(owner));
        await DispatchDuelEffectsAsync(owner,result,id,lifetime.Token);return result;
    }
    private async Task DispatchDuelEffectsAsync(string owner,DuelView duel,string callerId,CancellationToken token)
    {
        foreach(var round in duel.Rounds)
        foreach(var participant in new[]{duel.Left.IdentityId,duel.Right.IdentityId})
        {
            var key=duel.Id+":"+round.Number+":"+participant;
            if(store.TryRead<string>(owner,"duel-effect",key) is not null)continue;
            var session=Read(owner,participant);
            if(session.Status!="open")continue;
            var hash=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            var operation=new Guid(hash.AsSpan(0,16)).ToString("D");
            TestingHardware acknowledged;
            try
            {
                using var participantLease=participant==callerId?null:BeginOperation(participant,token);
                RequireOpen(owner,participant);
                acknowledged=await hardware.CommandAsync(session.Id,session.VirtualDeviceId,"duel",operation,null,participantLease?.Token??token);
            }
            catch(TestingException failure)
            {
                lab.Change(owner,session.LabSessionId,current=>current with {Events=[..current.Events,
                    LabContract.Event("duel_virtual_effect_failed",failure.Code)]});
                continue;
            }
            lab.Change(owner,session.LabSessionId,current=>current with {Events=[..current.Events,
                LabContract.Event("duel_round", "real_domain_round_virtual_effect_acknowledged",compartment:acknowledged.SelectedCompartment)]});
            store.Put(owner,"duel-effect",key,operation);
        }
    }
    internal async Task<DuelView> NarrateDuelAsync(string owner,string id,string duelId,CancellationToken token)
    {RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);var result=await resources.GenerateDuelCommentaryAsync(MemoryDevice(owner),duelId,lifetime.Token);RequireOpen(owner,id);return result;}
    internal KeepsakeAssetContent Asset(string owner,string id,string keepsakeId,string assetId)
    {
        var s=Read(owner,id);if(s.KeepsakeId!=keepsakeId)throw new TestingException(404,"test_resource_unavailable");
        return resources.Keepsakes.ReadAsset(keepsakeId,Actor(s),assetId);
    }
    internal async Task<KeepsakeUpload> PhotoAsync(string owner,string id,string keepsakeId,Stream stream,string mime,bool consent,CancellationToken token)
    {
        var s=RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);if(s.KeepsakeId!=keepsakeId)throw new TestingException(404,"test_resource_unavailable");
        var consentVersion=resources.Keepsakes.SetPortraitConsent(keepsakeId,Actor(s,true),consent,"dv10-portrait-v1",Guid.NewGuid().ToString());
        return await resources.Keepsakes.AcceptPhotoAsync(keepsakeId,Actor(s,true),stream,mime,Guid.NewGuid().ToString(),consentVersion,lifetime.Token);
    }
    internal async Task<TestingSessionDetail> PortraitAsync(string owner,string id,string uploadId,string editionId,CancellationToken token)
    {
        var s=RequireOpen(owner,id);using var lifetime=BeginOperation(id,token);
        if(s.KeepsakeId is not {} k)throw new TestingException(409,"test_accepted_outcome_required");
        resources.Keepsakes.EnqueuePortrait(k,Actor(s),uploadId,editionId,"portrait_only",true,Guid.NewGuid().ToString());
        await resources.GenerateKeepsakeAsync(k,lifetime.Token);RequireOpen(owner,id);return await DetailAsync(owner,id,token);
    }
}

internal sealed partial class TestingSessionService
{
    private async Task ReconcileOutcomeAsync(TestingSession s,GeneratedWandSnapshot selected,CancellationToken token)
    {
        var operation=Guid.ParseExact(s.Id,"N").ToString("D");
        var deadline=clock.GetUtcNow().AddSeconds(60);
        while(true)
        {
            token.ThrowIfCancellationRequested();RequireOpen(s.OwnerId,s.Id);
            var observation=await hardware.SnapshotAsync(s.Id,s.VirtualDeviceId,token);
            PersistHardware(s.OwnerId,s.Id,observation);
            if(observation.Events.Any(e=>e.OperationId==operation&&e.Type=="presentation_failed"))
                throw new TestingException(409,"test_virtual_presentation_failed");
            if(observation.Events.Any(e=>e.OperationId==operation&&e.Type=="presentation_completed"&&e.Code=="completed_by_timing"))
            {
                lock(gate)
                {
                    var current=RequireOpen(s.OwnerId,s.Id);
                    if(current.KeepsakeId is null)
                    {
                        var issued=resources.Keepsakes.Issue(new(operation,s.VirtualDeviceId,selected.PhysicalSlot,
                            selected.WandId,selected.Name,selected.Lore,"SIMULATION: production native runtime acknowledged completed_by_timing; no physical measurements.",
                            "generated-character-v1",Access(s).EveningId,Guid.ParseExact(s.Id,"N").ToString("D"),selected.ToPublic()),new(s.OwnerId,s.Id));
                        store.Put(s.OwnerId,"session",s.Id,current with {KeepsakeId=issued.Id,Revision=current.Revision+1});
                    }
                }
                return;
            }
            if(!observation.Connected)throw new TestingException(409,"test_virtual_hardware_disconnected");
            if(clock.GetUtcNow()>=deadline)throw new TestingException(504,"test_virtual_presentation_timeout");
            await Task.Delay(150,token);
        }
    }
}
