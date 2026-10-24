import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
const api = vi.hoisted(() => ({ transport: vi.fn(), setAccount: vi.fn(), microphone: vi.fn(), connect: vi.fn(), dispose: vi.fn(), sendText: vi.fn(), output: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref({ id: 'tester-1', displayName: 'Tester One', roles: ['Tester'] });
  api.setAccount.mockImplementation((value: typeof account.value) => { account.value = value; });
  return { account, accountOrigin: ref('https://backend.test'), accountTransport: api.transport };
});
vi.mock('../persona-lab/voice', () => ({ LabVoice: class {
  constructor(private observer: {phase:(value:string)=>void}){}
  connect = (...args:unknown[]) => { api.connect(...args); this.observer.phase('listening'); return Promise.resolve(); };  dispose = api.dispose; setOutputEnabled = api.output; sendText = api.sendText; interrupt = vi.fn();
} }));
import TestingSessionsView from './TestingSessionsView.vue';
const person = { id: 'character-1', profile: { profileId: 'profile-1', revision: 1, userInput: { name: 'Alena', world: 'Vlastní svět', description: 'Knihovnice.' }, status: 'ready', enrichment: { recognizedName: 'Alena', summary: 'Skutečně vrácené obohacení.', model: 'character-model', evidenceKind: 'provider', conversationIdeas: [], ambiguities: [] } } };
function fixture() {
  return { session: { id: 'session-1', ownerId: 'tester-1', virtualDeviceId: 'virtual-1', labSessionId: 'lab-1', engine: 'gpt-live-1', inputMode: 'text', status: 'open', characterId: person.id, createdAt: '2026-10-24T10:00:00Z', endedAt: null, revision: 1 },
    conversation: { id: 'lab-1', ownerId: 'tester-1', label: 'Test', engine: 'gpt-live-1', status: 'open', createdAt: '2026-10-24T10:00:00Z', endedAt: null, turns: [] as {id:string;role:string;text:string;createdAt:string}[], events: [] as {id:string;type:string;code:string;createdAt:string}[], config: {id:'baseline'}, recordContent: true },
    character: person, hardware: { connected: true, ready: true, state: 'ready', currentAction: 'idle', compartments: Array.from({length:6},(_,index)=>({slot:index+1,state:'retracted',wandName:null})), selectedCompartment: null, ledPattern: 'idle', servoCommand:null,vibrationCommand:null,cameraAvailable:true,events:[] }, wand:null,keepsakes:[],memory:{status:'empty',records:[]},duels:[] };
}
let detail: ReturnType<typeof fixture>; let wrappers: VueWrapper[] = [];
async function render(path = '/testing-sessions?session=session-1') {
  const router = createRouter({ history: createMemoryHistory(), routes: [{path:'/testing-sessions',component:TestingSessionsView},{path:'/persona-lab',component:{template:'<div>Lab</div>'}}] });
  await router.push(path); await router.isReady();
  const wrapper = mount(TestingSessionsView, {global:{plugins:[router]}}); wrappers.push(wrapper); await flushPromises();
  return {wrapper,router};
}
function button(wrapper: VueWrapper, text: string) { const result = wrapper.findAll('button').find(item=>item.text()===text); if(!result)throw new Error(`Missing button ${text}`); return result; }
beforeEach(() => {
  vi.clearAllMocks(); api.sendText.mockReturnValue(true); detail = fixture(); api.setAccount({id:'tester-1',displayName:'Tester One',roles:['Tester']});
  Object.defineProperty(navigator,'mediaDevices',{configurable:true,value:{getUserMedia:api.microphone}});
  api.transport.mockImplementation(async (path:string,init:RequestInit={}) => {
    const method = init.method ?? 'GET';
    if (path === '/app/v1/testing-sessions/characters') return Response.json([person]);
    if (path === '/app/v1/testing-sessions' && method === 'POST') return Response.json(detail.session);
    if (path === '/app/v1/testing-sessions') return Response.json([detail.session]);
    if (path === '/app/v1/testing-sessions/session-1/mode') { detail.session = {...detail.session,...JSON.parse(String(init.body)),revision:detail.session.revision+1};return Response.json(detail); }
    if (path === '/app/v1/testing-sessions/characters/character-1/enrich')return Response.json(person);
    if (path === '/app/v1/testing-sessions/session-1/character') { detail.session.revision++;return Response.json(detail); }
    if (path === '/app/v1/testing-sessions/session-1/duels') return Response.json([]);
    if (path === '/app/v1/testing-sessions/session-1') return Response.json(detail);
    if (path === '/app/v1/persona-lab/sessions/lab-1/turns') {
      const text = JSON.parse(String(init.body)).text;
      detail.conversation.turns.push({id:'input',role:'user',text,createdAt:'2026-10-24T10:01:00Z'},{id:'output',role:'assistant',text:'Odpověď skutečného adaptéru.',createdAt:'2026-10-24T10:01:02Z'});
      detail.conversation.events.push({id:'provider-event',type:'provider_session',code:'ready',createdAt:'2026-10-24T10:01:01Z'});
      return Response.json(detail.conversation);
    }
    throw new Error(`Unexpected ${method} ${path}`);
  });
});
afterEach(()=>{ for(const wrapper of wrappers)wrapper.unmount();wrappers=[];vi.useRealTimers(); });

describe('hardware-free testing journey', () => {
  it('resumes the exact durable session without BLE, voice setup or microphone permission', async () => {
    const {wrapper,router}=await render();
    expect(wrapper.text()).toContain('Alena');expect(wrapper.text()).toContain('Virtuální — žádné ESP32 ani BLE');
    expect(router.currentRoute.value.query.session).toBe('session-1');
    expect(api.microphone).not.toHaveBeenCalled();expect(api.connect).not.toHaveBeenCalled();
    expect(wrapper.findAll('.virtual-compartments li')).toHaveLength(6);
    expect(wrapper.text()).toContain('Zatím bez potvrzeného spojení');
  });
  it('never treats an established transport socket as a confirmed provider handshake', async () => {
    detail.conversation.events.push({id:'socket',type:'connection_established',code:'connected',createdAt:'2026-10-24T10:00:01Z'});
    const {wrapper}=await render();
    expect(wrapper.text()).toContain('Zatím bez potvrzeného spojení');
    expect(wrapper.text()).not.toContain('Potvrzené živé spojení');
  });
  it('submits typed input through the existing backend conversation client and displays acknowledged turns', async () => {
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);
    await wrapper.get('#testing-message').setValue('Jsem knihovnice.');await wrapper.get('.testing-chat-form').trigger('submit');await flushPromises();
    const call=api.transport.mock.calls.find(([path])=>path==='/app/v1/persona-lab/sessions/lab-1/turns');
    expect(JSON.parse(call![1].body)).toMatchObject({text:'Jsem knihovnice.',platform:'web'});
    expect(wrapper.text()).toContain('Odpověď skutečného adaptéru.');expect(wrapper.text()).toContain('Provedeno — viz potvrzené požadavky');
    expect(api.microphone).not.toHaveBeenCalled();expect(api.connect).not.toHaveBeenCalled();
  });
  it('keeps provider errors as failures without adding an assistant reply or simulated provider confirmation', async () => {
    const original=api.transport.getMockImplementation()!;
    api.transport.mockImplementation((path:string,init:RequestInit)=>path.endsWith('/turns')?Promise.resolve(Response.json({code:'lab_provider_unavailable'},{status:503})):original(path,init));
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);await wrapper.get('#testing-message').setValue('Ahoj');await wrapper.get('.testing-chat-form').trigger('submit');await flushPromises();
    expect(wrapper.text()).toContain('lab_provider_unavailable');expect(wrapper.findAll('.testing-chat li')).toHaveLength(0);
    expect(wrapper.text()).toContain('Zatím bez potvrzeného spojení');
  });
  it('requires explicit voice activation and preserves the same conversation when switching input modes', async () => {
    detail.conversation.turns.push({id:'earlier',role:'user',text:'Dříve uložená zpráva',createdAt:'2026-10-24T10:00:00Z'});
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);
    await wrapper.get('#testing-input').setValue('hybrid');await flushPromises();
    expect(api.connect).not.toHaveBeenCalled();expect(wrapper.text()).toContain('Dříve uložená zpráva');
    await button(wrapper,'Zapnout mikrofon a hlasové spojení').trigger('click');await flushPromises();expect(api.connect).toHaveBeenCalledWith(expect.any(Function),'lab-1','https://backend.test');
    await wrapper.get('#testing-input').setValue('text');await flushPromises();expect(api.dispose).toHaveBeenCalled();expect(wrapper.text()).toContain('Dříve uložená zpráva');
    expect(api.transport.mock.calls.filter(([path,init])=>path==='/app/v1/testing-sessions'&&init?.method==='POST')).toHaveLength(0);
  });
  it('refreshes the frozen character context after real enrichment before the first turn',async()=>{
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);
    await button(wrapper,'Spustit skutečné AI obohacení').trigger('click');await flushPromises();
    expect(api.transport.mock.calls.some(([path])=>path==='/app/v1/testing-sessions/session-1/character')).toBe(true);
  });
  it('retains active conversation context and explains when enrichment applies to future sessions',async()=>{
    detail.conversation.turns.push({id:'earlier',role:'user',text:'Už mluvíme.',createdAt:'2026-10-24T10:00:00Z'});
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);
    await button(wrapper,'Spustit skutečné AI obohacení').trigger('click');await flushPromises();
    expect(api.transport.mock.calls.some(([path])=>path==='/app/v1/testing-sessions/session-1/character')).toBe(false);
    expect(wrapper.text()).toContain('Probíhající rozhovor zachovává původní kontext.');
  });
  it('uses the maintained playback socket with capture disabled for explicitly requested spoken typed output',async()=>{
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);
    await wrapper.get('.testing-conversation .testing-consent input').setValue(true);
    await wrapper.get('#testing-message').setValue('Přečti skutečnou odpověď.');await wrapper.get('.testing-chat-form').trigger('submit');await flushPromises();
    expect(api.connect).toHaveBeenCalledWith(expect.any(Function),'lab-1','https://backend.test',{capture:false});
    expect(api.sendText).toHaveBeenCalledWith('Přečti skutečnou odpověď.');
    expect(api.microphone).not.toHaveBeenCalled();
    expect(api.transport.mock.calls.some(([path])=>path.endsWith('/turns'))).toBe(false);
    expect(wrapper.text()).not.toContain('Potvrzené živé spojení');
  });
  it('waits for authoritative retirement of the previous microphone execution before submitting HTTP text once',async()=>{
    const {wrapper}=await render();await wrapper.get('input[type=checkbox]').setValue(true);
    await wrapper.get('#testing-input').setValue('hybrid');await flushPromises();
    await button(wrapper,'Zapnout mikrofon a hlasové spojení').trigger('click');await flushPromises();
    const original=api.transport.getMockImplementation()!;let reads=0;
    api.transport.mockImplementation((path:string,init:RequestInit)=>path==='/app/v1/testing-sessions/session-1' ? Promise.resolve(Response.json({...detail,conversationActive:reads++===0})) : original(path,init));
    vi.useFakeTimers();await wrapper.get('#testing-message').setValue('Pokračuj v tématu.');await wrapper.get('.testing-chat-form').trigger('submit');await flushPromises();
    expect(api.transport.mock.calls.filter(([path])=>path.endsWith('/turns'))).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(220);await flushPromises();
    expect(api.transport.mock.calls.filter(([path])=>path.endsWith('/turns'))).toHaveLength(1);
    expect(wrapper.text()).toContain('Odpověď skutečného adaptéru.');
  });
  it('clears private characters and transcript immediately when the account loses Tester access', async () => {
    const {wrapper}=await render();expect(wrapper.text()).toContain('Alena');
    api.setAccount({id:'customer-2',displayName:'Other',roles:['Customer']});await flushPromises();
    expect(wrapper.text()).not.toContain('Alena');expect(wrapper.text()).toContain('vyžaduje účet Tester');expect(wrapper.find('#testing-message').exists()).toBe(false);
  });
  it('starts with an idempotent operation and puts the returned session ID into the resumable route', async () => {
    const {wrapper,router}=await render('/testing-sessions');await wrapper.get('input[type=checkbox]').setValue(true);
    await button(wrapper,'Start New Test Session').trigger('click');await flushPromises();
    const call=api.transport.mock.calls.find(([path,init])=>path==='/app/v1/testing-sessions'&&init?.method==='POST');
    expect(JSON.parse(call![1].body)).toMatchObject({engine:'gpt-live-1',inputMode:'text',openAiConsent:true,recordContent:true,operationId:expect.any(String)});
    expect(router.currentRoute.value.query.session).toBe('session-1');expect(api.microphone).not.toHaveBeenCalled();
  });
});
