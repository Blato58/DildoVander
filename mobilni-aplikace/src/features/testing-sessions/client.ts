import type { Duel, DuelAction, DuelSide } from '../archive/showContracts';
import { accountTransport } from '../identity/session';
import { labClientPlatform } from '../persona-lab/platform';
import type { LabEngine } from '../persona-lab/contracts';
import type { CharacterInput, InputMode, TestCharacter, TestingCatalog, TestSession, TestSessionDetail } from './contracts';

export class TestingError extends Error {
  constructor(readonly code: string, readonly status: number) { super(code); }
}

/** Account transport supplies CSRF/native cancellation; no device credential is accepted. */
export class TestingSessionsClient {
  private generation = 0;
  private controller = new AbortController();
  constructor(private identity: () => string) {}
  invalidate(): void { this.generation++; this.controller.abort(); this.controller = new AbortController(); }
  private async request<T>(path = '', method = 'GET', body?: unknown): Promise<T> {
    const owner = this.identity(); const generation = this.generation; const signal = this.controller.signal;
    const current = () => {
      if (!owner || owner !== this.identity() || generation !== this.generation || signal.aborted)
        throw new DOMException('Testing account changed.', 'AbortError');
    };
    current();
    const response = await accountTransport('/app/v1/testing-sessions' + path, {
      method, signal, redirect: 'error', headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    current();
    const value = await response.json().catch(() => null); current();
    if (!response.ok) throw new TestingError(typeof value?.code === 'string' ? value.code : 'testing_request_failed', response.status);
    if (value === null && response.status !== 204) throw new TestingError('testing_invalid_response', 502);
    return value as T;
  }
  catalog() { return this.request<TestingCatalog>('/catalog'); }
  sessions() { return this.request<TestSession[]>(); }
  session(id: string) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}`); }
  create(engine: LabEngine, inputMode: InputMode, consent: boolean, operationId: string) {
    const platform = labClientPlatform();
    if(!platform)return Promise.reject(new TestingError('client_platform_unsupported',0));
    return this.request<TestSession>('', 'POST', { engine, inputMode, openAiConsent: consent, recordContent: true, operationId, platform });
  }
  characters() { return this.request<TestCharacter[]>('/characters'); }
  createCharacter(userInput: CharacterInput, openAiConsent: boolean, longTermConsent = false) { return this.request<TestCharacter>('/characters', 'POST', { userInput, openAiConsent, longTermConsent }); }
  editCharacter(id: string, userInput: CharacterInput, expectedRevision: number, openAiConsent: boolean, longTermConsent = false) {
    return this.request<TestCharacter>(`/characters/${encodeURIComponent(id)}`, 'PUT', { userInput, expectedRevision, openAiConsent, longTermConsent });
  }
  enrich(id: string) { return this.request<TestCharacter>(`/characters/${encodeURIComponent(id)}/enrich`, 'POST', { openAiConsent: true }); }
  select(id: string, characterId: string, expectedRevision: number) {
    return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/character`, 'POST', { characterId, expectedRevision });
  }
  mode(id: string, inputMode: InputMode, expectedRevision: number) { return this.request<TestSession>(`/${encodeURIComponent(id)}/mode`, 'PUT', { inputMode, expectedRevision }); }
  selectWand(id: string) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/select`, 'POST', {}); }
  generate(id: string) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/generate`, 'POST', {}); }
  fault(id: string, fault: string) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/fault`, 'POST', { fault }); }
  duel(id: string, otherSessionId: string, mode: string, operationId: string) { return this.request<Duel>(`/${encodeURIComponent(id)}/duels`, 'POST', { otherSessionId, mode, operationId }); }
  startDuel(id: string, duelId: string, revision: number, operationKey: string) { return this.request<Duel>(`/${encodeURIComponent(id)}/duels/${encodeURIComponent(duelId)}/start`, 'POST', { revision, operationKey }); }
  move(id: string, duelId: string, side: DuelSide, round: number, revision: number, action: DuelAction, operationKey: string) { return this.request<Duel>(`/${encodeURIComponent(id)}/duels/${encodeURIComponent(duelId)}/move`, 'POST', { side, round, revision, action, operationKey }); }
  async asset(id: string, keepsakeId: string, assetId: string): Promise<Blob> {
    const owner = this.identity(); const generation = this.generation; const signal = this.controller.signal;
    const response = await accountTransport(`/app/v1/testing-sessions/${encodeURIComponent(id)}/keepsakes/${encodeURIComponent(keepsakeId)}/assets/${encodeURIComponent(assetId)}`, { signal, redirect:'error' });
    const blob = await response.blob();
    if (!owner || owner !== this.identity() || generation !== this.generation || signal.aborted) throw new DOMException('Testing account changed.', 'AbortError');
    if (!response.ok || !['image/png','image/jpeg','image/webp'].includes(blob.type)) throw new TestingError('testing_asset_unavailable', response.status);
    return blob;
  }
  duels(id: string) { return this.request<Duel[]>(`/${encodeURIComponent(id)}/duels`); }
  readDuel(id: string, duelId: string) { return this.request<Duel>(`/${encodeURIComponent(id)}/duels/${encodeURIComponent(duelId)}`); }
  narrateDuel(id: string, duelId: string) { return this.request<Duel>(`/${encodeURIComponent(id)}/duels/${encodeURIComponent(duelId)}/narrate`, 'POST', {}); }
  photo(id: string, keepsakeId: string, dataBase64: string) { return this.request<{id:string}>(`/${encodeURIComponent(id)}/keepsakes/${encodeURIComponent(keepsakeId)}/photo`, 'POST', {dataBase64,mimeType:'image/jpeg',consent:true}); }
  portrait(id:string, uploadId:string, editionId:string) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/portrait`, 'POST', {uploadId,editionId}); }
  deleteCharacter(id:string) { return this.request<void>(`/characters/${encodeURIComponent(id)}`, 'DELETE'); }
  retryGeneration(id:string,jobId:string,confirmAmbiguous:boolean) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/retry-generation`, 'POST', {jobId,confirmAmbiguous}); }
  advanceDuel(id:string,duelId:string) { return this.request<Duel>(`/${encodeURIComponent(id)}/duels/${encodeURIComponent(duelId)}/advance`, 'POST', {}); }
  end(id: string) { return this.request<TestSession>(`/${encodeURIComponent(id)}/end`, 'POST', {}); }
  cancel(id: string) { return this.request<TestSessionDetail>(`/${encodeURIComponent(id)}/cancel`, 'POST', {}); }
  delete(id: string) { return this.request<void>(`/${encodeURIComponent(id)}`, 'DELETE'); }
}
