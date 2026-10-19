const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const twin = require('./twin_viewer.js');
test('HTTP diagnostics retain only bounded technical code and route, never raw response details',async()=>{
  const error=await twin.httpFailure({status:409,json:async()=>({code:'media_epoch_or_playback_mismatch',detail:'private payload',credential:'secret'})},'/v1/twin/instances/sim/media/played?credential=secret');
  assert.equal(error.status,409);assert.equal(error.code,'media_epoch_or_playback_mismatch');
  assert.match(error.message,/media\/played/);assert.match(error.message,/media_epoch_or_playback_mismatch/);
  assert.equal(error.message.includes('secret'),false);assert.equal(error.message.includes('private payload'),false);
});
test('HTTP diagnostics tolerate proxy HTML and reject arbitrary server error text',async()=>{
  const proxy=await twin.httpFailure({status:502,json:async()=>{throw new Error('HTML');}},'/media/output');
  assert.match(proxy.message,/502/);assert.equal(proxy.code,'');
  const unsafe=await twin.httpFailure({status:409,json:async()=>({code:'token: private-value'})},'/media/input');
  assert.equal(unsafe.code,'');assert.equal(unsafe.message.includes('private-value'),false);
});
const manifest = {revision:'cad-1', hardwareProfile:'B-effects', kinematics:{liftMaxMm:16,angleMinDegrees:0,angleMaxDegrees:160,
  samples:[{angleDegrees:0,liftMm:0},{angleDegrees:80,liftMm:8},{angleDegrees:160,liftMm:16}]}};
function frame(sequence='1', timeMs='0', options={}) {
  return {ageMs:0,receivedAtUtc:'2026-10-14T12:00:00Z',snapshot:{schemaVersion:1,
    source:{instanceId:'sim',deviceId:'sim',mode:'simulation',bootId:'boot-1',cadManifestRevision:'cad-1',hardwareProfile:'B-effects',...options.source},
    sequence,timeMs,revision:sequence,reported:{physicalVerification:'unavailable',servo:[{slot:1,estimate:'rest',pulseUs:null}],generation:'0',...options.reported},
    model:options.model === undefined ? {evidence:'simulated',slots:[{slot:1,position:.5,jammed:false}]} : options.model,
    trace:options.trace || {events:[]},transport:{droppedSnapshots:'0'},capabilities:['observe']}};
}
test('decimal clocks retain exact values above Number precision and reject coercions',()=>{
  assert.equal(twin.decimal('18446744073709551615','clock'),18446744073709551615n);
  for (const invalid of [1,'01','-1','1.0','18446744073709551616']) assert.throws(()=>twin.decimal(invalid,'clock'));
  const projection = new twin.Projection(manifest); projection.select('sim');
  assert.equal(projection.accept(frame('9007199254740993','9007199254740993'),0),true);
  assert.equal(projection.accept(frame('9007199254740992','9007199254740993'),1),false);
});
test('virtual model drives cam-law projection and Stop freezes its recorded pose',()=>{
  const projection = new twin.Projection(manifest); projection.select('sim');
  projection.accept(frame(),0); assert.equal(projection.angle(1),80);
  projection.accept(frame('2','250',{reported:{state:'stopped',outputsEnabled:false,servo:[{slot:1,estimate:'unknown'}]}}),250);
  assert.equal(projection.angle(1),80);
  assert.equal(projection.status(2251),'stale'); assert.equal(projection.status(10251),'offline');
  assert.equal(projection.angle(1),80);
});
test('live endpoint is estimated, moving freezes, unknown boot clears and retired boot cannot return',()=>{
  const projection = new twin.Projection(manifest); projection.select('sim');
  projection.accept(frame('1','0',{source:{mode:'live'},model:null,reported:{servo:[{slot:1,estimate:'raised'}]}}),0);
  assert.equal(projection.angle(1),160);
  projection.accept(frame('2','100',{source:{mode:'live'},model:null,reported:{servo:[{slot:1,estimate:'moving',pulseUs:1700}]}}),100);
  assert.equal(projection.angle(1),160);
  projection.accept(frame('1','0',{source:{mode:'live',bootId:'boot-2'},model:null,reported:{servo:[{slot:1,estimate:'unknown'}]}}),101);
  assert.equal(projection.angle(1),null);
  assert.equal(projection.accept(frame('3','101',{source:{mode:'live'},model:null}),102),false);
});
test('source switch discards previous state and rejects in-flight old source',()=>{
  const projection = new twin.Projection(manifest); projection.select('sim'); projection.accept(frame(),0);
  projection.select('other'); assert.equal(projection.current,null); assert.equal(projection.angle(1),null);
  assert.equal(projection.accept(frame(),1),false); assert.equal(projection.status(1),'waiting');
});
test('duplicate conflict, time regression and fabricated live model are rejected',()=>{
  const projection = new twin.Projection(manifest); projection.select('sim'); projection.accept(frame('4','100'),0);
  assert.equal(projection.accept(frame('4','100',{model:{evidence:'simulated',slots:[{slot:1,position:.9}]}}),1),false);
  assert.equal(projection.accept(frame('5','99'),2),false);
  assert.throws(()=>twin.validateSnapshot(frame('1','0',{source:{mode:'live'}}).snapshot));
  assert.throws(()=>twin.validateSnapshot(frame('1','0',{model:{evidence:'simulated',slots:[{slot:1,position:2}]}}).snapshot));
});
test('bounded replay preserves full uint64 trace and source versions; invalid records fail',()=>{
  const record = {schemaVersion:1,snapshots:[frame('9007199254740993','9007199254740993',{trace:{events:[{sequence:'18446744073709551615',timeMs:'9007199254740993',kind:'servo'}]}})]};
  const parsed = twin.parseRecording(JSON.stringify(record));
  assert.equal(parsed.snapshots[0].snapshot.sequence,'9007199254740993');
  assert.equal(parsed.snapshots[0].snapshot.trace.events[0].sequence,'18446744073709551615');
  assert.throws(()=>twin.parseRecording(JSON.stringify({schemaVersion:1,snapshots:[]})));
  assert.throws(()=>twin.parseRecording(' '.repeat(twin.MAX_BYTES+1)));
  const wrong = frame('2','1',{source:{instanceId:'other'}});
  assert.throws(()=>twin.parseRecording(JSON.stringify({schemaVersion:1,snapshots:[frame(),wrong]})));
});
test('missing and incompatible CAD identities remain visible',()=>{
  const projection = new twin.Projection(manifest); projection.select('sim');
  projection.accept(frame('1','0',{source:{cadManifestRevision:null}}),0); assert.match(projection.compatibility(),/Chybí CAD/);
  projection.accept(frame('2','1',{source:{cadManifestRevision:'other'}}),1); assert.match(projection.compatibility(),/NESHODA/);
});
test('native uint32 trace sequences are retained and drop/gaps/bundle mismatch remain visible',()=>{
  const trace={droppedEvents:3,bundleHash:'old',events:[{sequence:4,timeMs:'0'},{sequence:7,timeMs:'10'}]};
  const parsed=twin.parseRecording(JSON.stringify({schemaVersion:1,snapshots:[frame('1','10',{trace})]}));
  assert.equal(parsed.snapshots[0].snapshot.trace.events[0].sequence,4);
  assert.match(twin.traceSummary(trace,{bundleHash:'new'}),/droppedEvents 3.*gaps 5–6.*NESHODA/);
});
test('accepted vibration and audio map only declared CAD parts, without physical motion claims',()=>{
  const mapping={slots:Array.from({length:6},(_,index)=>({slot:index+1,channels:{vibrationPca:index+6},components:{u059:`u059_${index+1}`}})),
    audio:{speakers:['speaker_left','speaker_right'],amplifiers:['amplifier_left']}};
  const active=twin.reportedOutputs(mapping,{outputsEnabled:true,vibrationMask:36,audio:'tone-enqueue-accepted'});
  assert.deepEqual(active.vibrationMeshes,['u059_3','u059_6']);
  assert.deepEqual(active.audioMeshes,['speaker_left','speaker_right','amplifier_left']); assert.equal(active.audioKind,'enqueue');
  assert.deepEqual(twin.reportedOutputs(mapping,{outputsEnabled:false,vibrationMask:36}).vibrationMeshes,[]);
  assert.equal(twin.reportedOutputs(mapping,{audio:'tone-cancel-requested'}).audioKind,'cancel');
  assert.equal(twin.reportedOutputs(mapping,{audio:'voice-busy-reported'}).audioKind,'voice-busy');
  assert.deepEqual(twin.reportedOutputs(mapping,{audio:'simulated-track-drained'}).audioMeshes,[]);
  assert.deepEqual(twin.reportedOutputs({}, {audio:'simulated-tone-enqueued'}).audioMeshes,[]);
});

/* Minimal DOM models real event handlers; layout and WebGL remain browser checks. */
function mounted(role='observer', location={origin:'https://twin.example',protocol:'https:',hostname:'twin.example',pathname:'/twin'}, profile='deterministic') {
  const elements = new Map(), actions = [], posts = [], applies = [], grbFrames = [], reports = [], selections = [], timers = [];
  let current = frame(), now = 0, sourceMode='simulation', sourceStatus='online', unavailable=false, postHold=null;
  class Element {
    constructor(tag='div') { this.tag=tag; this.children=[]; this.dataset={}; this.value=''; this.files=[]; this.hidden=false; }
    set id(value) { this.identifier=value; elements.set(value,this); }
    get id() { return this.identifier; }
    set innerHTML(value) {
      this.textContent = value.replace(/<[^>]*>/g, '');
      for (const match of value.matchAll(/<(\w+)[^>]*\bid="([^"]+)"([^>]*)>/g)) {
        const element = new Element(match[1]); element.id=match[2]; element.hidden=match[3].includes('hidden');
      }
      for (const match of value.matchAll(/data-twin-action="([^"]+)"/g)) {
        const button = new Element('button'); button.dataset.twinAction=match[1]; actions.push(button);
      }
      if (value.includes('id="twin-session"')) {
        elements.get('twin-slot').value='1'; elements.get('twin-variant').value='main';
        elements.get('twin-speed').value='1'; elements.get('twin-replay-speed').value='1';
      }
    }
    append(child) { this.children.push(child); }
    add(child) { this.append(child); }
    replaceChildren(...children) { this.children=[...children]; }
    setAttribute(key,value) { this[key]=value; }
    addEventListener(key,handler) { this[key]=handler; }
    querySelectorAll(selector) {
      if (selector === '[data-twin-action]') return actions;
      if (selector === 'button') return actions;
      return [];
    }
    querySelector() { return {focus(){}}; }
    click() {}
  }
  const document = {body:new Element('body'),createElement:tag=>new Element(tag),getElementById:id=>elements.get(id)};
  const context = {document,location,TextEncoder,Date,JSON,BigInt,Number,Set,Array,Blob,URL,console,
    Option:function(text,value) { this.textContent=text; this.value=value; },
    performance:{now:()=>now},crypto:{randomUUID:()=> '00000000-0000-4000-8000-000000000001'},
    addEventListener(){},setInterval:callback=>{timers.push(callback); return timers.length;},clearInterval(){},
    setTimeout:callback=>{timers.push(callback); return timers.length;},clearTimeout(){},
    requestAnimationFrame:callback=>{timers.push(callback); return timers.length;},cancelAnimationFrame(){},
    fetch:async(url,options)=>{
      if (options.method==='POST') {
        posts.push({url,body:JSON.parse(options.body)});
        const hold=postHold; postHold=null; if (hold) await hold;
        return {ok:true,json:async()=> url.endsWith('/session') ? {role} : {accepted:true}};
      }
      if (unavailable && !url.endsWith('/instances')) return {ok:false,status:404};
      return {ok:true,json:async()=> url.endsWith('/instances') ? {role,instances:[{instanceId:'sim',mode:sourceMode,profile,status:sourceStatus,online:sourceStatus==='online'}]} : current};
    }};
  context.window=context;
  require('./ui_test_runtime.cjs')(context);
  for(const file of ['./twin_core.js','./twin_viewer.js']) vm.runInNewContext(fs.readFileSync(require.resolve(file),'utf8'),context);
  const api=context.DigitalTwin.mount(document,{twinManifest:{...manifest,slots:[{slot:1,components:{tray:'tray_1'},liftComponents:['tray_1'],rotationComponents:['cam_1']}]}},
    {activate(){},deactivate(){},clear(){},apply:(angles,grb,reported)=>{applies.push(angles);grbFrames.push(grb);reports.push(reported);},select:name=>selections.push(name)});
  const get=id=>elements.get(id);
  const flush=async()=>{ for (let i=0;i<6;i++) await Promise.resolve(); };
  return {get,api,posts,actions,applies,grbFrames,reports,selections,timers,flush,setFrame:value=>{current=value;},setNow:value=>{now=value;},
    setMode:value=>{sourceMode=value;},setStatus:value=>{sourceStatus=value;},setUnavailable:value=>{unavailable=value;},
    holdNextPost:()=>{let resolve;postHold=new Promise(done=>{resolve=done;});return resolve;}};
}
test('mounted observer cannot command even if disabled handler is invoked; stale only updates badge',async()=>{
  const app=mounted(); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  assert.equal(app.api.active(),true); assert.equal(app.get('twin-controls').hidden,true);
  app.actions.find(button=>button.dataset.twinAction==='text').onclick(); await app.flush();
  assert.equal(app.posts.length,0);
  const geometryUpdates=app.applies.length;
  app.setNow(2500); app.timers[0]();
  assert.match(app.get('twin-badge').textContent,/STALE/); assert.equal(app.applies.length,geometryUpdates);
});
test('control commands use typed route; live and replay handlers never send commands',async()=>{
  const app=mounted('control'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  app.actions.find(button=>button.dataset.twinAction==='stop').onclick(); await app.flush();
  assert.equal(app.posts.length,1); assert.equal(app.posts[0].body.action,'stop'); assert.match(app.posts[0].url,/\/commands$/);
  const record={schemaVersion:1,snapshots:[frame('1','0'),frame('2','100',{trace:{events:[{sequence:'1',timeMs:'100',kind:'servo',slot:1}]}})]};
  const text=JSON.stringify(record); app.get('twin-import').files=[{size:text.length,text:async()=>text}];
  await app.get('twin-import').onchange(); assert.equal(app.api.state().replay,true);
  app.actions.find(button=>button.dataset.twinAction==='text').onclick(); app.get('twin-pause').onclick(); await app.flush();
  assert.equal(app.posts.length,1); assert.equal(app.get('twin-controls').hidden,true);
  app.get('twin-seek').value='1'; app.get('twin-seek').oninput();
  app.get('twin-trace').children[0].children[0].onclick(); assert.equal(app.selections[0],'tray_1');
  app.get('twin-exit-replay').onclick(); assert.equal(app.api.active(),false);
  app.setFrame(frame('1','0',{source:{mode:'live'},model:null}));
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  app.actions.find(button=>button.dataset.twinAction==='stop').onclick(); await app.flush();
  assert.equal(app.posts.length,1); assert.equal(app.get('twin-controls').hidden,true);
  assert.equal(app.reports.at(-1).audio,undefined);
});
test('private same-origin session clears credential; public and file paths cannot authenticate',async()=>{
  const app=mounted('control'); app.get('twin-credential').value='scoped-twin-secret';
  await app.get('twin-session').onsubmit({preventDefault(){}});
  assert.equal(app.get('twin-credential').value,''); assert.equal(app.posts[0].url,'https://twin.example/v1/twin/session');
  const published=mounted('observer',{origin:'https://twin.example',protocol:'https:',hostname:'twin.example',pathname:'/workshop'});
  published.get('twin-credential').value='secret'; await published.get('twin-session').onsubmit({preventDefault(){}});
  assert.equal(published.posts.length,0); assert.equal(published.get('twin-session').hidden,true);
  const offline=mounted('observer',{origin:'null',protocol:'file:',hostname:'',pathname:'/viewer.html'});
  await offline.get('twin-refresh').onclick(); assert.match(offline.get('twin-error').textContent,/Offline export/);
  assert.equal(offline.posts.length,0);
});
test('integration profile requires a client upgrade for retired text and never commands fake clock',async()=>{
  const app=mounted('control',undefined,'integration'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  assert.equal(app.api.state().profile,'integration'); assert.match(app.get('twin-badge').textContent,/integration/);
  assert.match(app.get('twin-capabilities').textContent,/wall-clock.*backend/);
  for (const id of ['twin-speed','twin-pause','twin-resume','twin-step']) assert.equal(app.get(id).disabled,true);
  app.get('twin-pause').onclick(); app.get('twin-step').onclick(); app.get('twin-speed').onchange(); await app.flush();
  assert.equal(app.posts.length,0);
  app.get('twin-text').value='  Vyber mi hůlku.  ';
  app.actions.find(button=>button.dataset.twinAction==='text').onclick(); await app.flush();
  assert.equal(app.posts.length,0);
  assert.equal(app.actions.find(button=>button.dataset.twinAction==='text').disabled,true);
  assert.equal(app.get('twin-text').disabled,true);
  assert.match(app.get('twin-error').textContent,/conversation_engine_upgrade_required/);
  await app.get('twin-refresh').onclick(); assert.equal(app.api.state().profile,'integration');
  app.actions.find(button=>button.dataset.twinAction==='release').onclick(); await app.flush();
  assert.equal(app.posts[0].body.action,'release'); assert.match(app.get('twin-capabilities').textContent,/Ovládání uvolněno/);
});
test('deterministic profile retains explicit slot/variant synthetic input',async()=>{
  const app=mounted('control'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  app.get('twin-slot').value='4'; app.get('twin-variant').value='short';
  app.actions.find(button=>button.dataset.twinAction==='text').onclick(); await app.flush();
  assert.equal(app.posts[0].body.action,'text'); assert.equal(app.posts[0].body.slot,4); assert.equal(app.posts[0].body.variant,'short');
  assert.equal(app.get('twin-pause').disabled,false);
});
test('known live source retains LIVE identity before any snapshot and never falls back',async()=>{
  const app=mounted('control'); app.setMode('live'); app.setUnavailable(true);
  await app.get('twin-refresh').onclick(); app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  assert.match(app.get('twin-badge').textContent,/ŽIVÝ KUFR · LIVE · ODHAD/);
  assert.doesNotMatch(app.get('twin-badge').textContent,/SIMULACE/); assert.equal(app.applies.length,0);
  app.actions.find(button=>button.dataset.twinAction==='stop').onclick(); await app.flush(); assert.equal(app.posts.length,0);
});
test('late previous-source command completion cannot clear current-source pending command',async()=>{
  const app=mounted('control'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  const first=app.holdNextPost(); app.actions.find(button=>button.dataset.twinAction==='stop').onclick(); await app.flush();
  assert.equal(app.api.state().pending,true);
  app.get('twin-source').onchange(); await app.flush(); assert.equal(app.api.state().pending,false);
  const second=app.holdNextPost(); app.actions.find(button=>button.dataset.twinAction==='stop').onclick(); await app.flush();
  first(); await app.flush(); assert.equal(app.api.state().pending,true);
  second(); await app.flush(); assert.equal(app.api.state().pending,false);
});
test('live observer and replay pass reported output evidence to renderer without commands',async()=>{
  const app=mounted(); const reported={outputsEnabled:true,vibrationMask:4,audio:'voice-busy-reported'};
  app.setMode('live'); app.setFrame(frame('1','0',{source:{mode:'live'},model:null,reported}));
  await app.get('twin-refresh').onclick(); app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  assert.equal(app.reports.at(-1).vibrationMask,4); assert.equal(app.reports.at(-1).audio,'voice-busy-reported');
  const text=JSON.stringify({schemaVersion:1,snapshots:[frame('1','0',{source:{mode:'live'},model:null,reported:{...reported,audio:'tone-cancel-requested'}})]});
  app.get('twin-import').files=[{size:text.length,text:async()=>text}]; await app.get('twin-import').onchange();
  assert.equal(app.reports.at(-1).audio,'tone-cancel-requested'); assert.equal(app.api.state().replay,true); assert.equal(app.posts.length,0);
  assert.match(app.get('twin-audio-status').textContent,/nikdy slyšitelný zvuk/);
});
test('v1 snapshot and untrusted replay reject claimed physical measurement',()=>{
  const measured=frame('1','0',{reported:{physicalVerification:'measured'}});
  assert.throws(()=>twin.validateSnapshot(measured.snapshot),/fyzické měření/);
  assert.throws(()=>twin.parseRecording(JSON.stringify({schemaVersion:1,snapshots:[measured]})),/fyzické měření/);
});
test('retired integration text never posts guest content or starts a pending operation',async()=>{
  const app=mounted('control',undefined,'integration'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  const submit=app.actions.find(button=>button.dataset.twinAction==='text').onclick;
  for (const value of ['Guest private detail', 'a'.repeat(1025), '界'.repeat(700)]) {
    app.get('twin-text').value=value; submit(); await app.flush();
    assert.equal(app.posts.length,0);
    assert.equal(app.api.state().pending,false);
    assert.match(app.get('twin-error').textContent,/conversation_engine_upgrade_required/);
  }
});
test('nullable unknown LED frame remains unknown rather than assumed accepted zero frame',async()=>{
  const app=mounted(); app.setFrame(frame('1','0',{reported:{ledGrb:null}}));
  await app.get('twin-refresh').onclick(); app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  assert.equal(app.reports.at(-1).ledGrb,null); assert.equal(app.grbFrames.at(-1).length,0); assert.equal(app.posts.length,0);
  const parsed=twin.parseRecording(JSON.stringify({schemaVersion:1,snapshots:[frame('1','0',{reported:{ledGrb:null}})]}));
  assert.equal(parsed.snapshots[0].snapshot.reported.ledGrb,null);
});
test('urgent Stop posts during an unresolved deterministic command, without duplicate Stop flood',async()=>{
  const app=mounted('control'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  const text=app.actions.find(button=>button.dataset.twinAction==='text');
  const stop=app.actions.find(button=>button.dataset.twinAction==='stop');
  const finishIntegration=app.holdNextPost(); app.get('twin-text').value='Vyber hůlku.'; text.onclick(); await app.flush();
  assert.equal(app.posts[0].body.action,'text'); assert.equal(app.api.state().pendingOrdinary,true);
  assert.equal(text.disabled,true); assert.equal(stop.disabled,false);
  // Stop remains reachable even when a stalled integration lets receipt age expire.
  app.setNow(2500); app.timers[0](); assert.equal(stop.disabled,false);
  const finishStop=app.holdNextPost(); stop.onclick(); await app.flush();
  assert.equal(app.posts.length,2); assert.equal(app.posts[1].body.action,'stop');
  assert.equal(app.api.state().pendingOrdinary,true); assert.equal(app.api.state().pendingStop,true);
  stop.onclick(); text.onclick(); await app.flush(); assert.equal(app.posts.length,2);
  finishIntegration(); await app.flush();
  assert.equal(app.api.state().pendingOrdinary,false); assert.equal(app.api.state().pendingStop,true); assert.equal(stop.disabled,true);
  finishStop(); await app.flush(); assert.equal(app.api.state().pending,false); assert.equal(stop.disabled,false);
});
test('previous-source ordinary and urgent Stop completions cannot clear new-source pending flags',async()=>{
  const app=mounted('control'); await app.get('twin-refresh').onclick();
  app.get('twin-source').value='sim'; app.get('twin-source').onchange(); await app.flush();
  const text=app.actions.find(button=>button.dataset.twinAction==='text');
  const stop=app.actions.find(button=>button.dataset.twinAction==='stop'); app.get('twin-text').value='Text';
  const oldIntegration=app.holdNextPost(); text.onclick(); await app.flush();
  const oldStop=app.holdNextPost(); stop.onclick(); await app.flush();
  app.get('twin-source').onchange(); await app.flush(); assert.equal(app.api.state().pending,false);
  const newIntegration=app.holdNextPost(); text.onclick(); await app.flush();
  const newStop=app.holdNextPost(); stop.onclick(); await app.flush();
  oldIntegration(); oldStop(); await app.flush();
  assert.equal(app.api.state().pendingOrdinary,true); assert.equal(app.api.state().pendingStop,true);
  newStop(); await app.flush();
  assert.equal(app.api.state().pendingOrdinary,true); assert.equal(app.api.state().pendingStop,false);
  newIntegration(); await app.flush(); assert.equal(app.api.state().pending,false);
});
test('source picker shows server connecting/stale/offline status instead of boolean online fallback',async()=>{
  const app=mounted();
  for (const status of ['connecting','stale','offline']) {
    app.setStatus(status); await app.get('twin-refresh').onclick();
    const option=app.get('twin-source').children.find(item=>item.value==='sim');
    assert.ok(option.textContent.endsWith(' · '+status));
  }
});
