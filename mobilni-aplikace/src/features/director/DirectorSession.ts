import { secureUuid } from '../../application/identifiers.mjs';
import { DirectorClient, DirectorRequestError, type ShowState } from './DirectorClient';
import { DuelGrantMemory } from './DuelLinks';
import type { Duel, Identity, Prophecy, RemoteStopOperation } from './directorState';
import type { OfflineCommandKind, OfflineCommandOptions } from '../device_connection/SimulatorDeviceConnection';
export interface DirectorLocal {
  matches(deviceId: string): boolean;
  offline(kind: OfflineCommandKind, compartment?: number, commandId?: string, options?: OfflineCommandOptions): Promise<unknown>;
  nextVisit(): boolean | undefined; stop(): void; cancelSpeech(): void;
}
const terminal = (duel: Duel) => ['completed', 'cancelled', 'expired', 'interrupted'].includes(duel.state.toLowerCase());
/** View-owned state. A context epoch fences every await, including local outbox acknowledgement. */
export class DirectorSession {
  state?: ShowState;
  selectedDeviceId = '';
  identities: Identity[] = [];
  prophecies: Prophecy[] = [];
  duel?: Duel;
  duelGrants = new DuelGrantMemory();
  remoteStop?: RemoteStopOperation;
  operation?: {id: string; label: string; status: string};
  feedback = '';
  offlineState = '';
  pending = false;
  authorized = false;
  refreshedAt = 0;
  private generation = 0;
  private controlEpoch = 0;
  private readSequence = 0;
  private mutation = 0;
  constructor(readonly client: DirectorClient, readonly local: DirectorLocal, private visible = true) {}
  online(): boolean { return this.authorized && !!this.state && !this.pending && !this.remoteStop?.pending && this.visible && Date.now() - this.refreshedAt < 12000; }
  canRemoteStop(): boolean { return this.authorized && this.visible && !!this.state && this.state.deviceId === this.client.deviceId && !this.remoteStop?.pending; }
  clear(): void {
    ++this.generation; ++this.controlEpoch; ++this.readSequence; ++this.mutation;
    this.client.forget(); this.selectedDeviceId = ''; this.state = undefined; this.identities = []; this.prophecies = []; this.duel = undefined;
    this.duelGrants.clear(); this.remoteStop = undefined; this.operation = undefined; this.authorized = false;
    this.pending = false; this.refreshedAt = 0; this.feedback = ''; this.offlineState = '';
  }
  bind(deviceId: string): void { this.clear(); this.selectedDeviceId=deviceId; this.client.bind(deviceId); }
  leaveView(): void { if(this.remoteStop?.pending){this.remoteStop.pending=false;this.remoteStop.status='unknown';} ++this.generation; ++this.readSequence; ++this.controlEpoch; ++this.mutation; this.pending=false; this.identities=[]; this.prophecies=[]; this.duel=undefined; this.duelGrants.clear(); this.feedback=""; this.offlineState=""; }
  suspend(): void { this.visible = false; this.clear(); }
  resume(): void { this.visible = true; }
  private guard(): () => void {
    const generation = this.generation, controlEpoch = this.controlEpoch, deviceId = this.client.deviceId;
    return () => { if (!this.visible || generation !== this.generation || controlEpoch !== this.controlEpoch || deviceId !== this.client.deviceId) throw new DOMException('Kontext režie se změnil; povel se neopakuje.', 'AbortError'); };
  }
  private async request<T>(suffix = '', body?: unknown, confirmed = false): Promise<T> {
    const current = this.guard(); current(); const result = await this.client.request<T>(suffix, body, confirmed); current(); return result;
  }
  private accept(state: ShowState): void {
    if (!state || state.deviceId !== this.client.deviceId || !Number.isInteger(state.revision) || !state.mood || typeof state.eveningId !== 'string') throw new Error('Neplatný stav serveru.');
    if(this.state?.eveningId===state.eveningId && state.revision<this.state.revision)throw new Error('Server vrátil starší revizi. Obnovte stav.');
    if (this.state?.eveningId !== state.eveningId) { this.identities = []; this.prophecies = []; this.duel = undefined; this.duelGrants.clear(); }
    this.state = state; this.authorized = true; this.refreshedAt = Date.now();
  }
  async refresh(): Promise<void> {
    if (!this.client.deviceId || !this.visible || this.pending || this.remoteStop?.pending) return;
    const sequence = ++this.readSequence, generation = this.generation;
    try {
      const state = await this.request<ShowState>(); if (sequence !== this.readSequence) return;
      this.accept(state);
      if (this.duel && !terminal(this.duel)) { const duel = await this.request<Duel>(`/duels/${this.duel.id}`); if (sequence === this.readSequence) this.setDuel(duel); }
    } catch (error) {
      if (generation !== this.generation || sequence !== this.readSequence) return;
      this.refreshedAt = 0; this.feedback = this.error(error);
      if (error instanceof DirectorRequestError && [401,403].includes(error.status)) { this.clear(); this.feedback = this.error(error); }
    }
  }
  private error(error: unknown): string { return error instanceof Error ? error.message : 'Výsledek není ověřený.'; }
  async perform(label: string, work: () => Promise<void>): Promise<void> {
    if (this.pending || this.remoteStop?.pending) return;
    const current = this.guard(), mutation = ++this.mutation; ++this.readSequence;
    this.pending = true; this.feedback = `${label} · čekám…`;
    try { current(); await work(); current(); this.feedback = `${label}: výsledek přijat. ${this.operation?.status ?? ''}`; }
    catch (error) {
      try { current(); this.refreshedAt = 0; this.feedback = this.error(error);
        if (this.operation) this.operation.status = error instanceof DirectorRequestError ? 'Odmítnuto. Obnovte stav.' : 'Výsledek neznámý. Povel se automaticky neopakuje.';
      } catch { /* Previous context cannot restore private state or feedback. */ }
    } finally { if (mutation === this.mutation) this.pending = false; }
  }
  async stopThroughBackend(): Promise<void> {
    if (!this.canRemoteStop() || !this.state) return;
    const generation = this.generation, deviceId = this.client.deviceId, state = this.state;
    this.remoteStop = {id: secureUuid(), pending: true, status: 'sending'};
    const operation = this.remoteStop; ++this.controlEpoch; ++this.readSequence; ++this.mutation; this.pending = false;
    if (this.operation) this.operation.status = 'Zneplatněno povelem Stop. Pozdní odpověď se nepoužije.';
    const current = () => generation === this.generation && deviceId === this.client.deviceId && this.remoteStop === operation && this.visible;
    try {
      const result = await this.client.request<{status: string; state: ShowState}>('/commands', {commandId: operation.id, expectedStateRevision: state.revision, kind: 'stop', ttlSeconds: 30});
      if (!current()) return;
      if (!['accepted','replayed'].includes(result.status)) throw new Error('Neplatné potvrzení Stop.');
      this.accept(result.state); operation.status = 'accepted'; operation.expectedDeviceRevision = result.state.deviceStopRevision; operation.acceptedAt = result.state.updatedAt;
    } catch (error) { if (current()) { operation.status = error instanceof DirectorRequestError ? 'rejected' : 'unknown'; this.refreshedAt = 0; } }
    finally { if (current()) operation.pending = false; }
  }
  async command(kind: string, value?: string): Promise<void> {
    if (!this.online() || !this.state) return;
    const state = this.state;
    const active = !!state.sessionId && !!state.sessionGeneration;
    if (['quick-finale','guest-leave','scene-twist','cancel-speech','tone','tempo','mood','override'].includes(kind) && !active) return;
    if (kind === 'override' && (state.selectionLocked !== false || !/^[1-6]$/.test(value ?? '') || !((state.readyMask ?? 0) & (1 << (Number(value)-1))))) return;
    if (kind === 'next-visit' && !this.local.matches(this.client.deviceId)) return;
    await this.perform(kind, async () => {
      const current = this.guard(); const id = secureUuid(); this.operation = {id, label: kind, status: 'Odesláno; výsledek zatím neznámý.'};
      const result = await this.request<{status: string; state: ShowState}>('/commands', {commandId:id, expectedStateRevision:state.revision, kind, value, ttlSeconds:30, sessionId:state.sessionId, sessionGeneration:state.sessionGeneration});
      if (!['accepted','replayed'].includes(result.status)) throw new Error('Neplatné přijetí povelu.');
      this.accept(result.state); this.operation!.status = 'Backend přijal povel. Přehrání ani fyzický účinek nejsou potvrzené.';
      if (kind === 'next-visit') { current(); if (!this.local.matches(this.client.deviceId) || this.local.nextVisit() !== true) this.operation!.status = 'Backend přijal návštěvu, místní start nepotvrzen. Ověřte kufr; neopakujte automaticky.'; }
      if (['quiet','ambient'].includes(kind)) {
        if (!this.local.matches(this.client.deviceId)) { this.operation!.status = 'Backend přijal nastavení; místní kufr není připojený. Fyzický účinek neověřen.'; return; }
        await this.local.offline(kind === 'quiet' ? 'quiet' : 'offline_ambient', undefined, undefined, {enabled:value === 'on'}); current();
      }
    });
  }
  async offline(kind: OfflineCommandKind, compartment?: number, options?: OfflineCommandOptions): Promise<void> {
    const selected=this.selectedDeviceId || this.client.deviceId;
    if(selected && !this.local.matches(selected)){this.feedback='Místně připojený kufr se neshoduje s vybraným kufrem. Povel nebyl odeslán.';return;}
    await this.perform('Místní režim', async () => { const current = this.guard(); const result = await this.local.offline(kind, compartment, undefined, options); current(); this.offlineState = JSON.stringify(result); });
  }
  async syncOffline(confirmed: boolean): Promise<void> {
    if (!confirmed || !this.online() || !this.local.matches(this.client.deviceId)) return;
    await this.perform('Synchronizace', async () => {
      const current = this.guard(); const outbox = await this.local.offline('offline_outbox') as {records: Array<{operationId:string;deviceId:string;operationEpoch?:string}>}; current();
      if (!Array.isArray(outbox.records) || outbox.records.length > 8) throw new Error('Neplatný outbox.');
      let count = 0;
      for (const outcome of outbox.records) {
        current(); if (outcome.deviceId !== this.client.deviceId || !this.local.matches(this.client.deviceId)) throw new Error('Výsledek patří jinému kufru.');
        const operationEpoch = outcome.operationEpoch ?? '0';
        const result = await this.request<{operationId:string;operationEpoch?:string}>('/offline-outcomes', {schemaVersion:1,outcome}, true);
        if (result.operationId !== outcome.operationId) throw new Error('Server nepotvrdil tento výsledek.');
        const acknowledgedEpoch = result.operationEpoch;
        if (operationEpoch !== '0' && (typeof acknowledgedEpoch !== 'string' ||
            !/^[1-9][0-9]{0,19}$/.test(acknowledgedEpoch) ||
            (acknowledgedEpoch.length === 20 && acknowledgedEpoch > '18446744073709551615') ||
            acknowledgedEpoch !== operationEpoch)) {
          throw new Error('Server nepotvrdil původní epochu tohoto výsledku.');
        }
        current(); if (!this.local.matches(this.client.deviceId)) throw new Error('Místní kufr se změnil.');
        await this.local.offline('offline_sync_ack', undefined, outcome.operationId, { operationEpoch }); current(); ++count;
      }
      this.offlineState = `Server přijal ${count} výsledků; místní ACK následovalo až po jeho potvrzení. Provenience: operator_relay.`;
    });
  }
  async loadGuests(): Promise<void> {
    if (!this.online() || !this.state) return;
    await this.perform('Identity', async () => {
      const evening = this.state!.eveningId;
      const identities = await this.request<Identity[]>(`/identities?eveningId=${encodeURIComponent(evening)}`);
      const duels = await this.request<Duel[]>(`/duels?eveningId=${encodeURIComponent(evening)}`);
      if (!Array.isArray(identities) || identities.length > 256 || !Array.isArray(duels) || duels.length > 256) throw new Error('Neplatný přehled identit.');
      this.identities = identities; this.setDuel(duels[0]);
    });
  }
  setDuel(duel?: Duel): void {
    if (duel && (!this.state || duel.deviceId !== this.client.deviceId || duel.eveningId !== this.state.eveningId)) throw new Error('Souboj patří jinému kufru nebo večeru.');
    if (!duel || duel.id !== this.duel?.id || terminal(duel)) this.duelGrants.clear();
    this.duel = duel; if (duel && this.state) this.duelGrants.bind(this.state.eveningId,duel.id);
  }
  async createDuel(leftIdentityId:string,rightIdentityId:string,mode:'autoplay'|'interactive',consent:boolean):Promise<void> {
    if (!this.online() || !this.state || !consent || leftIdentityId === rightIdentityId || ![leftIdentityId,rightIdentityId].every(id => this.identities.some(identity=>identity.identityId === id))) return;
    await this.perform('Nový souboj',async()=>this.setDuel(await this.request<Duel>('/duels',{eveningId:this.state!.eveningId,leftIdentityId,rightIdentityId,mode,consentConfirmed:true,operationKey:secureUuid()})));
  }
  async duelAction(action:'start'|'cancel'):Promise<void> {
    if (!this.online() || !this.duel || terminal(this.duel)) return;
    const duel=this.duel;
    await this.perform('Souboj',async()=>this.setDuel(await this.request<Duel>(`/duels/${duel.id}/${action}`,{revision:duel.revision,operationKey:secureUuid()})));
  }
  async grant(side:'left'|'right',revoke:boolean,confirmed:boolean):Promise<void> {
    if (!confirmed || !this.online() || !this.duel || terminal(this.duel) || this.duel.mode !== 'interactive') return;
    const duel=this.duel; this.duelGrants.remove(side);
    await this.perform('Herní odkaz',async()=>{
      const result=await this.request<{token:string;side:'left'|'right';expiresAt:string;duel:Duel} | Duel>(`/duels/${duel.id}/grants${revoke?'/revoke':''}`,{side,revision:duel.revision,operationKey:secureUuid()},true);
      if (revoke) { if ((result as Duel).id !== duel.id) throw new Error('Neplatné odvolání.'); this.setDuel(result as Duel); }
      else { const issued=result as {token:string;side:'left'|'right';expiresAt:string;duel:Duel}; if (issued.side !== side || issued.duel?.id !== duel.id) throw new Error('Neplatný herní odkaz.'); this.setDuel(issued.duel); this.duelGrants.remember(side,{token:issued.token,expiresAt:issued.expiresAt}); }
    });
  }
  async loadProphecies():Promise<void> {
    if (!this.online() || !this.state) return;
    await this.perform('Proroctví',async()=>{ const values=await this.request<Prophecy[]>(`/prophecies?eveningId=${encodeURIComponent(this.state!.eveningId)}`); if (!Array.isArray(values) || values.length>256) throw new Error('Neplatná proroctví.'); this.prophecies=values.map(({outcomeId,omen,unlockAt,state})=>({outcomeId,omen,unlockAt,state})); });
  }
  async reveal(outcomeId:string,confirmed:boolean):Promise<void> {
    if (!confirmed || !this.online() || !this.prophecies.some(value=>value.outcomeId===outcomeId)) return;
    await this.perform('Odhalení',async()=>{ await this.request(`/prophecies/${encodeURIComponent(outcomeId)}/reveal`,{eveningId:this.state!.eveningId},true); this.prophecies=[]; });
  }
  async endEvening(confirmed:boolean):Promise<void> {
    if (!confirmed || !this.online() || !this.state) return;
    await this.perform('Ukončení večera',async()=>{ await this.request('/end-evening',{eveningId:this.state!.eveningId,expectedStateRevision:this.state!.revision},true); this.identities=[];this.prophecies=[];this.duel=undefined;this.duelGrants.clear(); });
    await this.refresh();
  }
}
