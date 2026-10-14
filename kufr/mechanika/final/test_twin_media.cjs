const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const media=require('./twin_media.js');
const BOOT='00000000-0000-4000-8000-000000000001', SESSION='00000000-0000-4000-8000-000000000002';
const source={active:true,replay:false,role:'control',instanceId:'sim-integration',mode:'simulation',profile:'integration',bootId:BOOT,status:'online'};
const configuration={revision:'9007199254740993',engine:'fake',voice:'marin',speed:1,photoConsent:true,presentationVariant:'main',
  availability:{fake:true,realtime:true,gptLive:true,mic:true,speaker:true,camera:true}};
const JPEG=Uint8Array.from([255,216,255,218,0,8,1,1,0,0,63,0,1,255,0,2,255,208,3,255,217]);
function output(kind,sequence='1',fields={}) {
  return {sessionId:SESSION,bootId:BOOT,epoch:'9007199254740993',inputEpoch:'1',outputEpoch:'1',sequence,kind,byteLength:0,phase:kind,inputEnabled:kind==='listening',drainedRequested:false,...fields};
}
function mounted() {
  const elements=new Map(), requests=[], permissions=[], streams=[], contexts=[], worklets=[], timers=[], canvases=[];
  let pendingRequest=null, pendingPermission=null, pendingResume=null, currentState=null;
  class Element {
    constructor(tag='div') { this.tag=tag;this.children=[];this.value='';this.checked=false;this.hidden=false;this.disabled=false;this.videoWidth=1280;this.videoHeight=720; }
    set innerHTML(value) {
      for(const match of value.matchAll(/<(\w+)[^>]*\bdata-media-([a-z-]+)[^>]*>/g)) elements.set(match[2],new Element(match[1]));
      elements.get('engine').value='fake';elements.get('voice').value='marin';elements.get('speed').value='1';elements.get('variant').value='main';
    }
    querySelector(selector) { return elements.get(selector.match(/data-media-([^\]]+)/)[1]); }
    append(element) { this.children.push(element);element.parent=this; }
    add(element) { this.append(element); }
    replaceChildren(...children) { this.children=[...children]; }
    setAttribute(key,value) { this[key]=value; }
    removeAttribute(key) { delete this[key]; }
    get firstElementChild() { return this.children[0]; }
    remove() { if(this.parent)this.parent.children=this.parent.children.filter(item=>item!==this); }
    play() { return Promise.resolve(); }
    getContext() { return {drawImage(){}}; }
    toBlob(callback,type) { callback(new Blob([JPEG],{type})); }
  }
  class Node { connect() {} disconnect() {} }
  class Audio extends Node {
    constructor(options) {
      super();this.sampleRate=options.sampleRate;this.state='suspended';this.currentTime=0;this.destination={};this.nodes=[];
      this.audioWorklet={addModule:async()=>{}};contexts.push(this);
    }
    async resume() {
      if(pendingResume){const pending=pendingResume;pendingResume=null;await pending;}
      this.state='running';this.onstatechange?.();
    }
    async close() { this.state='closed'; }
    async setSinkId(value) { this.sinkId=value; }
    createGain() { return Object.assign(new Node(),{gain:{value:1}}); }
    createMediaStreamSource() { return new Node(); }
    createBuffer(channels,length,rate) { return {length,rate,copyToChannel(samples){this.samples=samples;}}; }
    createBufferSource() {
      const audio=this,node=new Node();node.start=time=>{node.startAt=time;};node.stop=()=>node.onended?.();
      node.finish=()=>{audio.currentTime=node.startAt+node.buffer.length/node.buffer.rate;node.onended?.();};this.nodes.push(node);return node;
    }
  }
  class Worklet extends Node { constructor(){super();this.port={};worklets.push(this);} }
  const document={createElement:tag=>{const element=new Element(tag);if(tag==='canvas')canvases.push(element);return element;}};
  const navigator={mediaDevices:{addEventListener(){},enumerateDevices:async()=>[],getUserMedia:async constraints=>{
    permissions.push(constraints);if(pendingPermission){const pending=pendingPermission;pendingPermission=null;await pending;}
    const track={stopped:false,addEventListener(){},stop(){this.stopped=true;}};
    const stream={getTracks:()=>[track]};streams.push(stream);return stream;
  }}};
  const context={window:null,document,navigator,AudioContext:Audio,AudioWorkletNode:Worklet,isSecureContext:true,
    Uint8Array,Float32Array,DataView,Blob,AbortController,console,URL:{createObjectURL:()=> 'blob:test',revokeObjectURL(){}},
    atob:text=>Buffer.from(text,'base64').toString('binary'),crypto:{randomUUID:()=>BOOT},
    Option:function(text,value){this.textContent=text;this.value=value;},
    addEventListener(){},setTimeout:callback=>{timers.push(callback);return timers.length;},clearTimeout(){}};
  context.window=context;vm.runInNewContext(fs.readFileSync(require.resolve('./twin_media.js'),'utf8'),context);
  const api=context.TwinMedia.mount(document,new Element(),{request:async(id,path,options={})=>{
    const request={id,path,...options};requests.push(request);
    if(pendingRequest?.path===path){const pending=pendingRequest;pendingRequest=null;await pending.promise;}
    if(path==='/settings')return configuration;
    if(path==='/media/start')return {sessionId:SESSION,bootId:BOOT,epoch:'9007199254740993',inputEpoch:'0',outputEpoch:'0',phase:'starting',active:true};
    if(path==='/media/state')return currentState;
    return null;
  }});
  const get=key=>elements.get(key),flush=async()=>{for(let i=0;i<20;i++)await Promise.resolve();};
  const activate=async()=>{api.sync({...source});await flush();await get('speaker').onclick();await get('mic').onclick();};
  return {api,get,requests,permissions,streams,contexts,worklets,timers,canvases,flush,activate,
    hold:path=>{let resolve,reject;const promise=new Promise((done,fail)=>{resolve=done;reject=fail;});pendingRequest={path,promise};resolve.reject=reject;return resolve;},
    holdResume:()=>{let resolve;pendingResume=new Promise(done=>{resolve=done;});return resolve;},
    setMediaState:state=>{currentState=state;},
    poll:async()=>{const callback=timers.shift();assert.ok(callback);await callback();await flush();},
    holdPermission:()=>{let resolve;pendingPermission=new Promise(done=>{resolve=done;});return resolve;}};
}

test('PCM conversion is signed16 little-endian and capture queue is bounded to400ms',()=>{
  const bytes=media.pcm16(new Float32Array([-1,0,1]));assert.deepEqual([...bytes],[0,128,0,0,255,127]);
  const queue=new media.CaptureQueue();queue.push(new Uint8Array(9600));queue.push(new Uint8Array(9600));
  assert.throws(()=>queue.push(new Uint8Array(2)),/400 ms/);assert.equal(queue.take().length,9600);assert.equal(queue.bytes,9600);
  assert.throws(()=>media.decimal('01'));
});
test('settings preserve exact revisions, reject invalid gpt speed and never expose extra fields',()=>{
  const result=media.settings({...configuration,providerKey:'secret'});assert.equal(result.revision,'9007199254740993');assert.equal(result.providerKey,undefined);
  assert.throws(()=>media.settings({...configuration,engine:'gpt-live',speed:1.2}));
  assert.throws(()=>media.settings({...configuration,revision:9007199254740993}));
});

test('JPEG adapter removes APP metadata/comments and preserves encoded scans, stuffing and restart markers',()=>{
  const segment=(marker,payload)=>[255,marker,0,payload.length+2,...payload];
  const jfif=segment(224,[74,70,73,70,0,1,1,0,0,1,0,1,0,0]);
  const metadata=[...segment(225,[69,120,105,102]),...segment(226,[73,67,67,0]),...segment(239,[1,2]),...segment(254,[3,4])];
  const input=Uint8Array.from([255,216,...jfif,...metadata,...JPEG.subarray(2)]);
  const expected=Uint8Array.from([255,216,...jfif,...JPEG.subarray(2)]);
  assert.deepEqual(media.stripJpegMetadata(input),expected);assert.deepEqual(media.stripJpegMetadata(expected),expected);
  assert.deepEqual(media.stripJpegMetadata(Uint8Array.from([255,216,...segment(224,[1,2]),...JPEG.subarray(2)])),JPEG);
  assert.throws(()=>media.stripJpegMetadata(Uint8Array.from([255,216,255,226,255,255,...JPEG.subarray(2)])),/délka/);
  assert.throws(()=>media.stripJpegMetadata(Uint8Array.from([255,216,255,218,0,8,1,1,0,0,63,0,255,217])),/Prázdný/);
  assert.throws(()=>media.stripJpegMetadata(new Uint8Array(262145)));
  assert.deepEqual(input,Uint8Array.from([255,216,...jfif,...metadata,...JPEG.subarray(2)]));
});
test('settings/source sync never prompts permissions; observer/live/replay/deterministic cannot activate/start',async()=>{
  const app=mounted();app.api.sync({...source});await app.flush();assert.equal(app.permissions.length,0);assert.equal(app.requests[0].path,'/settings');
  for(const change of [{role:'observe'},{mode:'live'},{replay:true},{profile:'deterministic'}]){
    app.api.sync({...source,...change});await app.flush();const count=app.requests.length;
    await app.get('mic').onclick();await app.get('speaker').onclick();await app.get('start').onclick();
    assert.equal(app.permissions.length,0);assert.equal(app.requests.length,count);
  }
});
test('actual activation/start handlers send native admission only, input begins only at native listening',async()=>{
  const app=mounted();await app.activate();assert.equal(app.permissions.length,1);assert.equal(app.contexts[0].sampleRate,24000);
  app.worklets[0].port.onmessage({data:new Float32Array(4800)});await app.flush();assert.equal(app.requests.filter(item=>item.path==='/media/input').length,0);
  await app.get('start').onclick();await app.flush();
  const body=JSON.parse(app.requests.find(item=>item.path==='/media/start').body);
  assert.equal(body.expectedBootId,BOOT);assert.equal(body.audioConsent,true);assert.equal(body.micAvailable,true);assert.equal(body.cameraAvailable,true);
  assert.equal(body.slot,undefined);assert.equal(body.voice,undefined);assert.equal(body.providerKey,undefined);
  await app.api.handle(output('listening'));app.worklets[0].port.onmessage({data:new Float32Array(4800)});await app.flush();
  const input=app.requests.find(item=>item.path==='/media/input');assert.equal(input.body.length,9600);
  assert.equal(input.headers['Input-Sequence'],'0');assert.equal(input.headers['Media-Epoch'],'9007199254740993');assert.equal(input.headers['Boot-Id'],BOOT);
  assert.equal(input.headers.deviceId,undefined);
});
test('in-flight plus queued capture cannot exceed400ms and overflow immediately closes local devices',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();await app.api.handle(output('listening'));
  const finish=app.hold('/media/input');app.worklets[0].port.onmessage({data:new Float32Array(4800)});await app.flush();
  app.worklets[0].port.onmessage({data:new Float32Array(4800)});assert.equal(app.api.state().captureBytes,19200);
  app.worklets[0].port.onmessage({data:new Float32Array(128)});await app.flush();
  assert.equal(app.api.state().actor,null);assert.equal(app.streams[0].getTracks()[0].stopped,true);
  assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,1);finish();await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/input').length,1);
});
test('PCM enqueue sends no ACK before actual end; drain confirms consumed bytes not audible sound',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:4,pcmBase64:Buffer.from([0,0,0,0]).toString('base64')}));
  await app.api.handle(output('drain','2',{pcmBytes:'4',drainedRequested:true}));await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/played').length,0);
  app.contexts[0].nodes[0].finish();await app.flush();
  const acknowledgements=app.requests.filter(item=>item.path==='/media/played').map(item=>JSON.parse(item.body));
  assert.equal(acknowledgements.length,2);assert.equal(acknowledgements[0].drained,false);assert.equal(acknowledgements[1].drained,true);
  assert.equal(acknowledgements[1].pcmBytes,'4');assert.equal(acknowledgements[1].outputEpoch,'1');
});
test('cancel and suspended context never acknowledge scheduled output; old epoch callbacks cannot revive',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:4,pcmBase64:'AAAAAA==',drainedRequested:true}));
  const node=app.contexts[0].nodes[0];await app.get('stop').onclick();node.finish();await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/played').length,0);assert.equal(app.api.state().speaker,false);
  await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:4,pcmBase64:'AAAAAA=='}));
  app.contexts.at(-1).state='suspended';app.contexts.at(-1).onstatechange();await app.flush();
  assert.equal(app.api.state().actor,null);assert.equal(app.requests.filter(item=>item.path==='/media/played').length,0);
});

test('natural ended acknowledges playback even if exposed currentTime lags one render quantum',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:14400,pcmBase64:Buffer.alloc(14400).toString('base64')}));
  await app.api.handle(output('drain','2',{pcmBytes:'14400',drainedRequested:true}));
  const audio=app.contexts[0],node=audio.nodes[0];audio.currentTime=node.startAt+node.buffer.length/node.buffer.rate-128/24000;
  node.onended();await app.flush();
  const played=app.requests.filter(item=>item.path==='/media/played').map(item=>JSON.parse(item.body));
  assert.equal(played.length,2);assert.equal(played[1].pcmBytes,'14400');assert.equal(played[1].drained,true);assert.ok(app.api.state().actor);
  await app.api.handle(output('pcm','3',{byteLength:4,pcmBase64:'AAAAAA=='}));
  const cancelled=audio.nodes[1];await app.get('stop').onclick();cancelled.onended();await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/played').length,2);
});
test('normal completed/closed ends HAL without cancelling native presentation admission',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('completed'));await app.flush();assert.equal(app.api.state().actor,null);
  assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,0);assert.equal(app.streams[0].getTracks()[0].stopped,true);
});
test('Stop during pending start cancels exact admission, and late start response cannot attach',async()=>{
  const app=mounted();await app.activate();const finish=app.hold('/media/start');const starting=app.get('start').onclick();await app.flush();
  await app.get('stop').onclick();const cancellation=JSON.parse(app.requests.find(item=>item.path==='/media/cancel').body);
  assert.deepEqual(cancellation,{operationId:BOOT,expectedBootId:BOOT});finish();await starting;await app.flush();
  assert.equal(app.api.state().actor,null);assert.equal(app.requests.filter(item=>item.path==='/media/output').length,0);
});
test('late permission resolves after source switch with stopped track and no private new-source requests',async()=>{
  const app=mounted();app.api.sync({...source});await app.flush();const finish=app.holdPermission();const activating=app.get('mic').onclick();await app.flush();
  app.api.sync({...source,instanceId:'sim-other',role:'observe'});finish();await activating;
  assert.equal(app.streams[0].getTracks()[0].stopped,true);assert.equal(app.api.state().mic,false);
  assert.equal(app.requests.some(item=>item.id==='sim-other'),false);
});
test('native photo request prompts no permissions/upload; explicit preview and accept sends one boundedJPEG',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.get('camera').onclick();assert.equal(app.permissions.length,1);
  await app.api.handle(output('photo_request'));assert.equal(app.permissions.length,1);
  await app.get('camera').onclick();assert.equal(app.permissions.length,2);assert.equal(app.permissions[1].audio,false);
  await app.get('capture').onclick();assert.equal(app.requests.filter(item=>item.path==='/media/photo').length,0);
  assert.equal(app.canvases.at(-1).width,640);assert.equal(app.canvases.at(-1).height,360);
  await app.get('accept').onclick();await app.get('accept').onclick();
  const photos=app.requests.filter(item=>item.path==='/media/photo');assert.equal(photos.length,1);
  assert.equal(photos[0].body.type,'image/jpeg');assert.ok(photos[0].body.size<=262144);assert.equal(photos[0].headers['Input-Epoch'],'1');
  assert.equal(app.streams[1].getTracks()[0].stopped,true);
});
test('settings actual handler uses expected revision and GPT-Live fixes speed before PUT',async()=>{
  const app=mounted();app.api.sync({...source});await app.flush();app.get('engine').value='gpt-live';app.get('speed').value='1.2';app.get('engine').onchange();
  assert.equal(app.get('speed').value,'1');await app.get('settings').onsubmit({preventDefault(){}});
  const saved=app.requests.find(item=>item.method==='PUT');const body=JSON.parse(saved.body);
  assert.equal(body.expectedRevision,'9007199254740993');assert.equal(body.speed,1);assert.equal(body.engine,'gpt-live');
  assert.equal(app.permissions.length,0);
});
test('capture follows native inputEnabled rather than listening/thinking phase labels',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('listening','1',{inputEnabled:false}));app.worklets[0].port.onmessage({data:new Float32Array(4800)});await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/input').length,0);
  await app.api.handle(output('thinking','2',{inputEnabled:true}));app.worklets[0].port.onmessage({data:new Float32Array(4800)});await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/input').length,1);
});
test('explicit photo refusal sends empty JPEG unavailable signal, not a fabricated successful image',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();await app.api.handle(output('photo_request'));
  await app.get('skip-photo').onclick();const photo=app.requests.find(item=>item.path==='/media/photo');
  assert.equal(photo.body.length,0);assert.equal(photo.headers['Content-Type'],'image/jpeg');assert.equal(photo.headers['Input-Epoch'],'1');
  assert.equal(app.permissions.length,1);assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,0);
  const state=media.session({sessionId:SESSION,bootId:BOOT,epoch:'1',inputEpoch:'1',outputEpoch:'1',phase:'listening',inputEnabled:false,photoWarning:'photo_unavailable'},BOOT);
  assert.equal(state.inputEnabled,false);assert.equal(state.photoWarning,'photo_unavailable');
});

test('newer native state gate allows chronological older HAL descriptors without reopening input',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  app.setMediaState({...output('thinking'),inputEpoch:'2',inputEnabled:false,active:true});
  for(let i=0;i<9;i++)await app.poll();
  assert.equal(app.api.state().actor.inputEpoch,'2');
  await app.api.handle(output('listening','1',{inputEpoch:'1',inputEnabled:true}));
  app.worklets[0].port.onmessage({data:new Float32Array(4800)});await app.flush();
  assert.equal(app.api.state().actor.inputEpoch,'2');assert.equal(app.requests.filter(item=>item.path==='/media/input').length,0);
  app.setMediaState({...output('completed'),inputEpoch:'3',inputEnabled:false,active:false});
  for(let i=0;i<10;i++)await app.poll();
  await app.api.handle(output('pcm','2',{inputEpoch:'2',byteLength:4,pcmBase64:Buffer.from([0,0,0,0]).toString('base64')}));
  app.contexts[0].nodes[0].finish();await app.flush();
  assert.equal(app.api.state().actor.inputEpoch,'3');assert.equal(app.requests.filter(item=>item.path==='/media/played').length,1);
  assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,0);
});

test('late rejected playback ACK from cancelled output epoch cannot cancel current media',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:4,pcmBase64:Buffer.from([0,0,0,0]).toString('base64')}));
  const finish=app.hold('/media/played');app.contexts[0].nodes[0].finish();await app.flush();
  await app.api.handle(output('audio_cancel','2',{outputEpoch:'2',inputEpoch:'2'}));
  finish.reject(new Error('409 old output epoch'));await app.flush();
  assert.equal(app.api.state().actor.outputEpoch,'2');assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,0);
});

test('old source ACK rejection cannot cancel a new pending media admission',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:4,pcmBase64:Buffer.from([0,0,0,0]).toString('base64')}));
  const finishAck=app.hold('/media/played');app.contexts[0].nodes[0].finish();await app.flush();
  app.api.sync({...source,instanceId:'sim-other'});await app.flush();
  await app.get('speaker').onclick();await app.get('mic').onclick();
  const finishStart=app.hold('/media/start');const starting=app.get('start').onclick();await app.flush();
  finishAck.reject(new Error('old request aborted'));await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/cancel'&&item.id==='sim-other').length,0);
  finishStart();await starting;await app.flush();assert.ok(app.api.state().actor);
});

test('source switch while AudioContext resumes never starts a late microphone permission request',async()=>{
  const app=mounted();app.api.sync({...source});await app.flush();
  const finish=app.holdResume();const activating=app.get('mic').onclick();await app.flush();
  app.api.sync({...source,instanceId:'sim-other',role:'observe'});finish();await activating;await app.flush();
  assert.equal(app.permissions.length,0);assert.equal(app.api.state().mic,false);
});

test('native photo timeout closes preview and rejects late old-epoch photo without cancelling current voice',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('photo_request'));await app.get('camera').onclick();await app.get('capture').onclick();
  const finish=app.hold('/media/photo');const accepting=app.get('accept').onclick();await app.flush();
  assert.equal(app.streams[1].getTracks()[0].stopped,true);
  await app.api.handle(output('listening','2',{inputEpoch:'2'}));finish.reject(new Error('409 photo timed out'));await accepting;await app.flush();
  assert.equal(app.api.state().actor.inputEpoch,'2');assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,0);
  await app.api.handle(output('photo_request','3',{inputEpoch:'1'}));await app.get('camera').onclick();
  assert.equal(app.permissions.length,2);
  await app.api.handle(output('photo_request','4',{inputEpoch:'3'}));await app.get('camera').onclick();
  await app.api.handle(output('listening','5',{inputEpoch:'4'}));
  assert.equal(app.streams[2].getTracks()[0].stopped,true);assert.equal(app.api.state().actor.inputEpoch,'4');
});

for(const error of ['media_failed','media_boot_changed'])test(`failed native state ${error} closes HAL without awaiting an output frame or physical Stop`,async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  app.setMediaState({...output('failed'),active:false,error,inputEnabled:false});
  const outputs=app.requests.filter(item=>item.path==='/media/output').length;
  for(let i=0;i<9;i++)await app.poll();
  assert.equal(app.api.state().actor,null);assert.equal(app.streams[0].getTracks()[0].stopped,true);
  assert.equal(app.contexts[0].state,'closed');assert.equal(app.get('error').textContent,error);assert.equal(app.get('mic').disabled,false);
  assert.equal(app.requests.filter(item=>item.path==='/media/output').length,outputs+8);
  assert.equal(app.requests.some(item=>item.path==='/media/cancel'||item.path==='/commands'),false);
  await app.get('speaker').onclick();await app.get('mic').onclick();assert.equal(app.get('start').disabled,false);
});

test('old source failed state HTTP completion cannot close a new media actor',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  for(let i=0;i<8;i++)await app.poll();
  const finish=app.hold('/media/state');const oldPoll=app.poll();await app.flush();
  app.api.sync({...source,instanceId:'sim-other'});await app.flush();
  await app.get('speaker').onclick();await app.get('mic').onclick();await app.get('start').onclick();await app.flush();
  app.setMediaState({...output('failed'),active:false,error:'media_boot_changed',inputEnabled:false});
  finish();await oldPoll;await app.flush();
  assert.ok(app.api.state().actor);assert.equal(app.streams[1].getTracks()[0].stopped,false);
  assert.equal(app.requests.filter(item=>item.path==='/media/cancel'&&item.id==='sim-other').length,0);
});

test('new state output epoch immediately cancels old PCM and discards retired FIFO frames',async()=>{
  const app=mounted();await app.activate();await app.get('start').onclick();await app.flush();
  await app.api.handle(output('pcm','1',{byteLength:4,pcmBase64:'AAAAAA=='}));
  const audio=app.contexts[0],oldNode=audio.nodes[0];
  app.setMediaState({...output('listening'),active:true,inputEpoch:'2',outputEpoch:'2'});
  for(let i=0;i<9;i++)await app.poll();
  assert.equal(app.api.state().actor.outputEpoch,'2');oldNode.finish();await app.flush();
  assert.equal(app.requests.filter(item=>item.path==='/media/played').length,0);
  await app.api.handle(output('pcm','2',{byteLength:4,pcmBase64:'AAAAAA=='}));
  assert.equal(audio.nodes.length,1);assert.ok(app.api.state().actor);
  await app.api.handle(output('audio_cancel','3',{inputEpoch:'2',outputEpoch:'2'}));
  await app.api.handle(output('pcm','4',{inputEpoch:'2',outputEpoch:'2',byteLength:4,pcmBase64:'AAAAAA=='}));
  audio.nodes[1].finish();await app.flush();
  const played=app.requests.filter(item=>item.path==='/media/played').map(item=>JSON.parse(item.body));
  assert.equal(played.length,1);assert.equal(played[0].outputEpoch,'2');assert.equal(played[0].sequence,'4');
  assert.equal(app.requests.filter(item=>item.path==='/media/cancel').length,0);
});
