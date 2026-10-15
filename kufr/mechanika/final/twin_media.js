/* Browser platform HAL. Native C++ owns dialogue, gates, proposals and effects. */
(function(root) {
  'use strict';
  const RATE = 24000, CHUNK_BYTES = 9600, CAPTURE_BYTES = RATE * 2 * .4;
  const OUTPUT_BYTES = RATE * 2 * 4, UINT64_MAX = 18446744073709551615n;
  const KINDS = new Set(['pcm','listening','thinking','speaking','drain','audio_cancel','photo_request','dialogue','completed','failed','closed','idle']);
  function decimal(value) {
    if (typeof value !== 'string' || !/^(0|[1-9][0-9]*)$/.test(value) || BigInt(value) > UINT64_MAX) throw new Error('Neplatná media epoch/sequence');
    return BigInt(value);
  }
  function uuid(value) {
    if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) throw new Error('Neplatná media identita');
    return value;
  }
  function pcm16(samples) {
    const bytes = new Uint8Array(samples.length * 2), view = new DataView(bytes.buffer);
    for (let i=0;i<samples.length;i++) {
      const sample = Math.max(-1, Math.min(1, Number.isFinite(samples[i]) ? samples[i] : 0));
      view.setInt16(i*2, Math.round(sample * (sample < 0 ? 32768 : 32767)), true);
    }
    return bytes;
  }
  function floatPcm(bytes) {
    if (!(bytes instanceof Uint8Array) || !bytes.length || bytes.length % 2) throw new Error('Neplatný PCM16 frame');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength), samples = new Float32Array(bytes.length/2);
    for (let i=0;i<samples.length;i++) samples[i] = view.getInt16(i*2,true)/32768;
    return samples;
  }
  function stripJpegMetadata(bytes) {
    if (!(bytes instanceof Uint8Array) || bytes.length<12 || bytes.length>262144 ||
        bytes[0]!==255 || bytes[1]!==216 || bytes.at(-2)!==255 || bytes.at(-1)!==217) throw new Error('Neplatný JPEG náhled');
    const parts=[bytes.subarray(0,2)]; let position=2, scan=false;
    while (position<bytes.length-2) {
      const start=position;
      if (bytes[position++]!==255) throw new Error('Neplatná JPEG struktura');
      while (bytes[position]===255) position++;
      const marker=bytes[position++];
      if (!marker || marker===216 || marker===217 || marker>=208 && marker<=215 || position+2>bytes.length-2) throw new Error('Neplatný JPEG marker');
      const length=(bytes[position]<<8)|bytes[position+1], end=position+length;
      if (length<2 || end>bytes.length-2) throw new Error('Neplatná JPEG délka');
      // Canvas can emit an ICC APP2 profile. Remove application metadata and
      // comments without touching quantization, scan tables or encoded pixels.
      const jfif=marker===224 && length===16 && bytes[position+2]===74 && bytes[position+3]===70 &&
        bytes[position+4]===73 && bytes[position+5]===70 && bytes[position+6]===0 && bytes[position+14]===0 && bytes[position+15]===0;
      if (!(marker>=224 && marker<=239 || marker===254) || jfif) parts.push(bytes.subarray(start,end));
      position=end;
      if (marker===218) {
        const entropy=position; scan=true;
        while (position<bytes.length-2) {
          if (bytes[position]!==255) { position++; continue; }
          if (bytes[position+1]===0 || bytes[position+1]>=208 && bytes[position+1]<=215) { position+=2; continue; }
          break;
        }
        if (position===entropy) throw new Error('Prázdný JPEG scan');
        parts.push(bytes.subarray(entropy,position));
      }
    }
    if (!scan || position!==bytes.length-2) throw new Error('Neúplný JPEG scan');
    parts.push(bytes.subarray(-2));
    const result=new Uint8Array(parts.reduce((size,part)=>size+part.length,0)); let offset=0;
    for (const part of parts) { result.set(part,offset); offset+=part.length; }
    return result;
  }
  function settings(value) {
    decimal(value?.revision);
    if (!['fake','realtime','gpt-live'].includes(value.engine) || !['cedar','marin','coral'].includes(value.voice) ||
        !Number.isFinite(value.speed) || value.speed < .8 || value.speed > 1.2 || value.engine === 'gpt-live' && value.speed !== 1 ||
        typeof value.photoConsent !== 'boolean' || !['main','short'].includes(value.presentationVariant) || !value.availability) {
      throw new Error('Neplatné nastavení média');
    }
    return {revision:value.revision,engine:value.engine,voice:value.voice,speed:value.speed,
      photoConsent:value.photoConsent,presentationVariant:value.presentationVariant,
      availability:Object.fromEntries(['fake','realtime','gptLive','mic','speaker','camera'].map(key=>[key,value.availability[key] === true]))};
  }
  function session(value, bootId) {
    uuid(value?.sessionId); uuid(value.bootId);
    for (const key of ['epoch','inputEpoch','outputEpoch']) decimal(value[key]);
    if (value.bootId !== bootId || typeof value.phase !== 'string') throw new Error('Media session neodpovídá bootu');
    if (value.error !== undefined && value.error !== null && (typeof value.error !== 'string' || value.error.length>4096)) throw new Error('Neplatná nativní media chyba');
    return {sessionId:value.sessionId,bootId:value.bootId,epoch:value.epoch,inputEpoch:value.inputEpoch,
      outputEpoch:value.outputEpoch,phase:value.phase,active:value.active !== false,inputEnabled:value.inputEnabled===true,error:value.error || null,
      photoWarning:['photo_unavailable','photo_failed'].includes(value.photoWarning)?value.photoWarning:null};
  }
  function frame(value, actor) {
    if (!value || !KINDS.has(value.kind) || !Number.isInteger(value.byteLength) || value.byteLength < 0 || value.byteLength > 32768) throw new Error('Neplatný nativní HAL frame');
    for (const key of ['epoch','inputEpoch','outputEpoch','sequence']) decimal(value[key]);
    if (value.sessionId !== actor.sessionId || value.bootId !== actor.bootId || value.epoch !== actor.epoch) throw new Error('HAL frame patří jiné session');
    if (value.pcmBytes !== undefined) decimal(value.pcmBytes);
    if (value.kind === 'pcm' && (value.byteLength === 0 || value.byteLength % 2 || typeof value.pcmBase64 !== 'string' || value.pcmBase64.length > 43692)) throw new Error('Neplatná PCM attachment');
    if (value.text !== undefined && (typeof value.text !== 'string' || value.text.length > 4096)) throw new Error('Neplatný transientní dialog');
    return value;
  }
  class CaptureQueue {
    constructor() { this.reset(); }
    reset() { this.parts = []; this.bytes = 0; }
    push(bytes) {
      if (bytes.length + this.bytes > CAPTURE_BYTES) throw new Error('Mikrofon překročil 400 ms frontu; session se zastavuje');
      this.parts.push(bytes); this.bytes += bytes.length;
    }
    take() {
      if (this.bytes < CHUNK_BYTES) return null;
      const result = new Uint8Array(CHUNK_BYTES); let offset=0;
      while (offset < result.length) {
        const part = this.parts[0], count=Math.min(part.length,result.length-offset);
        result.set(part.subarray(0,count),offset); offset+=count; this.bytes-=count;
        if (count === part.length) this.parts.shift(); else this.parts[0]=part.subarray(count);
      }
      return result;
    }
  }

  function mount(document, container, transport) {
    const section = document.createElement('section'); section.className='twin-media'; section.id='twin-media';
    section.setAttribute('aria-labelledby','twin-media-heading');
    section.innerHTML=`<h3 id="twin-media-heading">Normální AI kufr · browser HAL</h3>
      <p data-media-scope></p><p data-media-status role="status"></p><p data-media-error role="alert"></p>
      <details><summary>Nastavení nativního rozhovoru</summary><form data-media-settings>
        <label>Engine<select data-media-engine><option value="fake">Fake služby</option><option value="realtime">Realtime</option><option value="gpt-live">GPT-Live</option></select></label>
        <label>Hlas<select data-media-voice><option>cedar</option><option>marin</option><option>coral</option></select></label>
        <label>Rychlost<select data-media-speed><option>0.8</option><option selected>1</option><option>1.2</option></select></label>
        <label>Prezentace<select data-media-variant><option>main</option><option>short</option></select></label>
        <label class="twin-media-checkbox"><input data-media-consent type="checkbox"> Souhlasím s jednou náhledovou fotkou</label>
        <button data-media-save>Uložit nastavení</button><button data-media-reload type="button">Načíst nastavení</button>
        <p data-media-revision></p>
      </form></details>
      <div class="twin-media-devices"><label>Mikrofon<select data-media-mic-device><option value="">Systémový mikrofon</option></select></label>
        <label>Reproduktor<select data-media-speaker-device><option value="">Systémový výstup</option></select></label>
        <label>Kamera<select data-media-camera-device><option value="">Systémová kamera</option></select></label>
        <button data-media-devices>Načíst lokální zařízení</button><button data-media-mic>Aktivovat mikrofon</button><button data-media-speaker>Aktivovat reproduktor</button>
      </div>
      <div class="twin-media-actions"><button data-media-start>Spustit rozhovor</button><button data-media-stop class="aw-button aw-button--danger">Stop médií</button></div>
      <p>Zvuk ukazuje software enqueue/drain evidence, nikoli potvrzení slyšitelnosti. Rozhoduje nativní core.</p>
      <div data-media-photo hidden><p>Nativní core žádá jednu fotku. Aktivujte kameru, prohlédněte náhled a výslovně přijměte.</p>
        <button data-media-camera>Aktivovat kameru</button><video data-media-video autoplay playsinline muted></video>
        <button data-media-capture>Pořídit náhled</button><img data-media-preview alt="Lokální náhled fotky před předáním" hidden>
        <button data-media-accept>Přijmout a předat jednu fotku</button><button data-media-retake>Vymazat náhled</button><button data-media-skip-photo>Pokračovat bez fotky</button></div>
      <ol data-media-dialogue aria-label="Transientní nativní dialog" tabindex="0"></ol>`;
    container.append(section);
    const get=key=>section.querySelector(`[data-media-${key}]`);
    let context=null, sourceKey='', configuration=null, actor=null, generation=0, failure='', notice='Média nejsou aktivovaná.';
    let audio=null, mic=null, camera=null, captureNode=null, captureSource=null, silentGain=null, speakerReady=false, workletReady=false;
    let listen=false, uploading=false, inFlightBytes=0, inputSequence=0n, retiredInputEpoch=null, lastSequence=null, timer=null, reading=false, pollCount=0;
    let queue=new CaptureQueue(), queuedOutputBytes=0, consumedBytes=0n, scheduledUntil=0, drainFrame=null;
    let photoRequest=null, photoBlob=null, photoUrl=null, pendingStart=false, admission=null, saving=false, activationBusy=false, ackTail=Promise.resolve();
    const sources=new Set(), controllers=new Set();
    function control() { return context?.active && !context.replay && context.role==='control' && context.mode==='simulation' && !!context.bootId; }
    function eligible() { return control() && context.profile==='integration' && context.status==='online'; }
    function render() {
      section.hidden=!control();
      get('scope').textContent=context?.profile==='integration' ? 'Soukromá integrační simulace · wall-clock · skutečný nativní voice owner.' : 'Deterministický profil používá syntetické povely; browser voice vyžaduje integrační instanci.';
      get('status').textContent=`${actor ? `Nativní phase: ${actor.phase}. ` : ''}${notice}`;
      get('error').textContent=failure;
      const busy=!!actor || pendingStart || saving || activationBusy;
      get('save').disabled=!eligible() || busy || !configuration;
      get('reload').disabled=!control() || busy;
      for (const key of ['engine','voice','variant','consent']) get(key).disabled=!eligible() || busy || !configuration;
      get('speed').disabled=!eligible() || busy || !configuration || get('engine').value==='gpt-live';
      get('mic').disabled=!eligible() || busy || !!mic || configuration?.availability.mic!==true;
      get('speaker').disabled=!eligible() || busy || speakerReady || configuration?.availability.speaker!==true;
      get('devices').disabled=!eligible() || busy;
      for (const key of ['mic-device','speaker-device','camera-device']) get(key).disabled=!eligible() || busy || key==='speaker-device' && !audio?.setSinkId;
      get('start').disabled=!eligible() || busy || !mic || !speakerReady || !configuration || audio?.state!=='running';
      get('stop').disabled=!actor && !pendingStart && !activationBusy && !mic && !camera && !audio;
      get('photo').hidden=!photoRequest;
      get('camera').disabled=!eligible() || !photoRequest || !!camera || activationBusy || configuration?.availability.camera!==true;
      get('capture').disabled=!photoRequest || !camera;
      get('accept').disabled=!photoRequest || !photoBlob;
      get('retake').disabled=!photoBlob;
      get('skip-photo').disabled=!photoRequest;
      get('revision').textContent=configuration ? `Revision ${configuration.revision}; providers fake=${configuration.availability.fake}, Realtime=${configuration.availability.realtime}, GPT-Live=${configuration.availability.gptLive}. Zařízení jsou aktivována samostatně.` : 'Nastavení není načtené.';
    }
    async function request(id,path,options={}) {
      const controller=new AbortController(); controllers.add(controller);
      try { return await transport.request(id,path,{...options,signal:controller.signal}); }
      finally { controllers.delete(controller); }
    }
    function json(body) { return {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}; }
    function headers(selected, inputEpoch, sequence) {
      return {'Session-Id':selected.sessionId,'Boot-Id':selected.bootId,'Media-Epoch':selected.epoch,
        'Input-Epoch':inputEpoch,'Input-Sequence':String(sequence)};
    }
    function clearPhoto() {
      photoBlob=null;
      if (photoUrl) root.URL.revokeObjectURL(photoUrl);
      photoUrl=null; get('preview').hidden=true; get('preview').removeAttribute('src');
    }
    function stopCamera() {
      const previous=camera; camera=null;
      for (const track of previous?.getTracks() || []) track.stop(); get('video').srcObject=null;
    }
    function stopOutput() {
      for (const item of sources) { item.cancelled=true; try { item.node.stop(); } catch {} item.node.disconnect(); }
      sources.clear(); queuedOutputBytes=0; scheduledUntil=0; consumedBytes=0n; drainFrame=null;
    }
    function localStop() {
      generation++; listen=false; queue.reset(); inputSequence=0n; retiredInputEpoch=null; lastSequence=null; photoRequest=null; clearPhoto();
      if (timer!==null) root.clearTimeout(timer); timer=null;
      for (const controller of controllers) controller.abort(); controllers.clear();
      stopOutput(); captureNode?.disconnect(); captureSource?.disconnect(); silentGain?.disconnect();
      captureNode=null; captureSource=null; silentGain=null;
      for (const stream of [mic,camera]) for (const track of stream?.getTracks() || []) track.stop();
      mic=null; camera=null; get('video').srcObject=null;
      if (audio) { audio.onstatechange=null; void audio.close().catch(()=>{}); }
      audio=null; speakerReady=false; workletReady=false; actor=null; admission=null; pendingStart=false; saving=false; activationBusy=false; uploading=false; inFlightBytes=0; reading=false; pollCount=0;
      ackTail=Promise.resolve();
    }
    async function cancel(reason='Média zastavena.', keepalive=false) {
      const previous=actor, pendingAdmission=admission, instance=context?.instanceId;
      localStop(); notice=reason; render();
      if ((previous || pendingAdmission) && instance) {
        const body=previous ? {sessionId:previous.sessionId,bootId:previous.bootId,epoch:previous.epoch} : pendingAdmission;
        // Unload sends only bounded, correlated IDs; no audio/photo bytes.
        const options=json(body); if (keepalive) options.keepalive=true;
        try { await transport.request(instance,'/media/cancel',options); }
        catch { /* Local capture/playback already ended; server epochs/deadlines remain authoritative. */ }
      }
    }
    function fail(error) { failure=error?.message || 'Média selhala.'; void cancel('Média selhala a lokálně se zastavila.'); }
    async function loadSettings() {
      if (!control()) return;
      const own=generation, id=context.instanceId;
      try {
        const value=settings(await request(id,'/settings'));
        if (own!==generation) return;
        configuration=value;
        get('engine').value=value.engine; get('voice').value=value.voice; get('speed').value=String(value.speed);
        get('variant').value=value.presentationVariant; get('consent').checked=value.photoConsent; failure='';
      } catch (error) { if (own===generation) failure=error.message; }
      render();
    }
    function ensureAudio() {
      if (!audio) {
        const Audio=root.AudioContext || root.webkitAudioContext;
        if (!Audio || !root.isSecureContext) throw new Error('Audio HAL vyžaduje podporovaný HTTPS browser');
        audio=new Audio({sampleRate:RATE});
        if (audio.sampleRate!==RATE || !audio.audioWorklet) throw new Error('Browser nepodporuje PCM24k AudioWorklet');
        audio.onstatechange=()=>{ if (actor && audio?.state!=='running') fail(new Error('AudioContext je suspended/disconnected; drain nelze potvrdit')); };
      }
      return audio;
    }
    async function enumerate() {
      if (!eligible()) return;
      const own=generation;
      try {
        const devices=await root.navigator.mediaDevices.enumerateDevices();
        if (own!==generation) return;
        for (const [key,kind,label] of [['mic-device','audioinput','Mikrofon'],['speaker-device','audiooutput','Výstup'],['camera-device','videoinput','Kamera']]) {
          const select=get(key), chosen=select.value; select.replaceChildren(new root.Option('Systémové zařízení',''));
          devices.filter(device=>device.kind===kind).forEach((device,index)=>select.add(new root.Option(device.label || `${label} ${index+1}`,device.deviceId)));
          select.value=chosen;
        }
        notice='Výběr zařízení zůstává pouze v browseru.'; render();
      } catch (error) { if (own===generation) { failure=error.message; render(); } }
    }
    async function acknowledge(selected, output) {
      const own=generation, consumed=consumedBytes;
      const current=()=>own===generation && actor===selected && selected.outputEpoch===output.outputEpoch;
      if (!current()) return;
      try {
        if (output.pcmBytes!==undefined && decimal(output.pcmBytes)!==consumed) throw new Error('Drain neodpovídá skutečně přehrané délce');
        const work=ackTail.then(async()=>{
          if (!current() || !eligible() || audio?.state!=='running') return;
          await request(context.instanceId,'/media/played',json({sessionId:selected.sessionId,bootId:selected.bootId,epoch:selected.epoch,
            outputEpoch:output.outputEpoch,sequence:output.sequence,pcmBytes:String(consumed),drained:output.kind==='drain' || output.drainedRequested===true}));
        });
        ackTail=work.catch(()=>{}); await work;
      } catch(error) { if (current()) fail(error); }
    }
    function play(output) {
      if (!audio || !speakerReady || audio.state!=='running') throw new Error('Reproduktor není aktivovaný; audio se nepřehrává');
      const decoded=root.atob(output.pcmBase64), bytes=Uint8Array.from(decoded,character=>character.charCodeAt(0));
      if (bytes.length!==output.byteLength || queuedOutputBytes+bytes.length>OUTPUT_BYTES) throw new Error('PCM output překročil omezenou frontu');
      const samples=floatPcm(bytes), buffer=audio.createBuffer(1,samples.length,RATE); buffer.copyToChannel(samples,0);
      const node=audio.createBufferSource(); node.buffer=buffer; node.connect(audio.destination);
      // A small initial buffer absorbs network jitter. It is never playback ACK.
      const lead=scheduledUntil>audio.currentTime ? .01 : .15;
      const start=Math.max(audio.currentTime+lead,scheduledUntil), end=start+samples.length/RATE;
      const selected=actor, own=generation, item={node,cancelled:false}; sources.add(item); queuedOutputBytes+=bytes.length; scheduledUntil=end;
      node.onended=()=>{
        node.disconnect(); sources.delete(item);
        if (item.cancelled || own!==generation || actor!==selected || selected.outputEpoch!==output.outputEpoch) return;
        queuedOutputBytes-=bytes.length;
        // Natural onended is graph completion evidence; currentTime may expose
        // an earlier render quantum or reduced timer precision. Explicit stop
        // and stale callbacks were already excluded above.
        if (audio?.state!=='running') { fail(new Error('Přehrávání se nedokončilo; drain unknown')); return; }
        consumedBytes+=BigInt(bytes.length);
        if (output.drainedRequested && queuedOutputBytes) drainFrame=output;
        else void acknowledge(selected,output);
        if (!queuedOutputBytes && drainFrame) { const drain=drainFrame; drainFrame=null; void acknowledge(selected,drain); }
      };
      node.start(start);
    }
    async function handle(output) {
      frame(output,actor);
      // New /state cancellation can overtake the FIFO delivery of old audio.
      // Retired output epochs must never enqueue playback again.
      if (decimal(output.outputEpoch)<decimal(actor.outputEpoch)) return;
      if (lastSequence!==null && decimal(output.sequence)<=lastSequence) throw new Error('Starý nativní HAL sequence');
      lastSequence=decimal(output.sequence); actor.phase=output.phase || output.kind;
      if (output.outputEpoch!==actor.outputEpoch) {
        stopOutput(); actor.outputEpoch=output.outputEpoch;
      }
      inputGate(output);
      if (output.kind==='pcm') play(output);
      if (output.kind==='audio_cancel') stopOutput();
      if (output.kind==='drain') {
        if (output.pcmBytes!==undefined && decimal(output.pcmBytes)<consumedBytes) throw new Error('Neplatný drain byte total');
        if (queuedOutputBytes) drainFrame=output; else await acknowledge(actor,output);
      }
      if (output.kind==='photo_request' && output.inputEpoch===actor.inputEpoch) { clearPhoto(); photoRequest={...output}; }
      if (output.text) {
        const item=document.createElement('li'); item.textContent=output.text; get('dialogue').append(item);
        while (get('dialogue').children.length>20) get('dialogue').firstElementChild.remove();
      }
      if (['completed','failed','closed'].includes(output.kind)) {
        const message=output.kind==='failed' ? output.error || 'Nativní workflow selhal.' : 'Nativní workflow skončil.';
        // Native owns terminal admission/presentation. A normal end is not Stop.
        localStop(); notice=message;
      }
      render();
    }
    function inputGate(value) {
      // /state can already describe a newer gate while older FIFO HAL outputs
      // still need playback/display. Preserve the newest gate without cancelling.
      if (decimal(value.inputEpoch)<decimal(actor.inputEpoch)) return;
      if (value.inputEpoch!==actor.inputEpoch) {
        queue.reset(); inputSequence=0n; actor.inputEpoch=value.inputEpoch;
        if (photoRequest && photoRequest.inputEpoch!==value.inputEpoch) {
          photoRequest=null; clearPhoto(); stopCamera();
        }
      }
      // Phase labels are not VoiceAudioPort.Listen evidence (notably GPT-Live).
      listen=value.inputEnabled===true && (retiredInputEpoch===null || decimal(value.inputEpoch)>decimal(retiredInputEpoch));
      actor.inputEnabled=listen;
      if (!listen) queue.reset();
    }
    async function poll(own) {
      if (own!==generation || !actor || !eligible() || reading) return;
      reading=true;
      let received=false;
      try {
        if (++pollCount%10===0) {
          const state=await request(context.instanceId,'/media/state');
          if (own!==generation || !actor) return;
          if (state) {
            const current=session(state,actor.bootId);
            if (current.sessionId!==actor.sessionId || current.epoch!==actor.epoch) throw new Error('Nativní media session se změnila');
            if (current.phase==='failed' || !current.active && current.error) {
              // Failed HTTP actors may have no remaining HAL frames. Preserve
              // normal completed FIFO playback, but close local failed media now.
              localStop(); failure=current.error || 'Nativní workflow selhal.';
              notice='Nativní média selhala; lokální zařízení zastavena.'; render(); return;
            }
            if (decimal(current.outputEpoch)>decimal(actor.outputEpoch)) {
              stopOutput(); actor.outputEpoch=current.outputEpoch;
            }
            actor.phase=current.phase; actor.photoWarning=current.photoWarning; inputGate(current);
            if (current.photoWarning) notice='Fotka není dostupná; nativní core pokračuje podle photo fallback pravidel.';
            render();
          }
        }
        if (queuedOutputBytes<=OUTPUT_BYTES-32768) {
          const value=await request(context.instanceId,'/media/output');
          if (own!==generation || !actor) return;
          if (value) { received=true; await handle(value); }
        }
      } catch (error) { if (own===generation) fail(error); }
      finally { if (own===generation) reading=false; }
      if (own===generation && actor) timer=root.setTimeout(()=>poll(own),received ? 0 : 50);
    }
    async function upload() {
      if (uploading || !listen || !actor || !eligible()) return;
      const bytes=queue.take(); if (!bytes) return;
      const own=generation, selected=actor, inputEpoch=selected.inputEpoch, sequence=inputSequence++;
      uploading=true; inFlightBytes=bytes.length;
      try {
        await request(context.instanceId,'/media/input',{method:'POST',headers:{...headers(selected,inputEpoch,sequence),'Content-Type':'application/octet-stream'},body:bytes});
      } catch (error) {
        if (own===generation && selected===actor && inputEpoch===actor.inputEpoch) {
          if (error.status===409 && error.code==='media-input-retired') {
            // VAD may close the native gate while a captured block is in flight.
            // Drop it; only a newer native gate may enable capture again.
            retiredInputEpoch=inputEpoch; listen=false; actor.inputEnabled=false;
            queue.reset(); pollCount=9;
          } else fail(error);
        }
      }
      finally { if (own===generation) { uploading=false; inFlightBytes=0; void upload(); } }
    }
    function capture(samples) {
      if (!listen || !actor || !eligible()) return;
      try {
        const bytes=pcm16(samples);
        if (queue.bytes+inFlightBytes+bytes.length>CAPTURE_BYTES) throw new Error('Mikrofon překročil 400 ms frontu; session se zastavuje');
        queue.push(bytes); void upload();
      }
      catch (error) { fail(error); }
    }
    get('reload').onclick=loadSettings;
    get('devices').onclick=enumerate;
    get('speaker-device').onchange=()=>{ if (!actor && !pendingStart) { speakerReady=false; render(); } };
    get('mic-device').onchange=()=>{
      if (actor || pendingStart || activationBusy) return;
      captureNode?.disconnect(); captureSource?.disconnect(); silentGain?.disconnect();
      captureNode=null; captureSource=null; silentGain=null;
      const previous=mic; mic=null; for (const track of previous?.getTracks() || []) track.stop(); render();
    };
    get('engine').onchange=()=>{ if (get('engine').value==='gpt-live') get('speed').value='1'; render(); };
    get('settings').onsubmit=async event=>{
      event.preventDefault(); if (!eligible() || actor || pendingStart || saving || !configuration) return;
      const selectedEngine=get('engine').value, capability=selectedEngine==='gpt-live'?'gptLive':selectedEngine;
      if (!configuration.availability[capability]) { failure='Zvolený provider není dostupný.'; render(); return; }
      const own=generation; saving=true; render();
      try {
        const value=await request(context.instanceId,'/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:configuration.revision,
          engine:selectedEngine,voice:get('voice').value,speed:+get('speed').value,photoConsent:get('consent').checked,presentationVariant:get('variant').value})});
        if (own===generation) { configuration=settings(value); failure=''; notice='Nativní nastavení uloženo.'; }
      } catch(error) { if (own===generation) failure=error.message; }
      finally { if (own===generation) { saving=false; render(); } }
    };
    get('speaker').onclick=async()=>{
      if (!eligible() || actor || pendingStart || activationBusy || speakerReady || configuration?.availability.speaker!==true) return;
      const own=generation; activationBusy=true; render();
      try {
        const selectedAudio=ensureAudio(); await selectedAudio.resume();
        if (own!==generation) return;
        const sink=get('speaker-device').value;
        if (sink) { if (!selectedAudio.setSinkId) throw new Error('Browser neumí vybrat reproduktor; zvolte systémový výstup'); await selectedAudio.setSinkId(sink); }
        if (own!==generation || selectedAudio.state!=='running') return;
        speakerReady=true; notice='Reproduktor aktivován; software drain není potvrzení slyšitelnosti.'; failure=''; render();
      } catch(error) { if (own===generation) { failure=error.message; render(); } }
      finally { if (own===generation) { activationBusy=false; render(); } }
    };
    get('mic').onclick=async()=>{
      if (!eligible() || actor || pendingStart || activationBusy || mic || configuration?.availability.mic!==true) return;
      const own=generation; activationBusy=true; render();
      try {
        const selectedAudio=ensureAudio(); await selectedAudio.resume();
        if (own!==generation) return;
        const device=get('mic-device').value;
        const stream=await root.navigator.mediaDevices.getUserMedia({audio:{channelCount:1,echoCancellation:true,noiseSuppression:true,autoGainControl:true,...(device?{deviceId:{exact:device}}:{})},video:false});
        if (own!==generation) { stream.getTracks().forEach(track=>track.stop()); return; }
        mic=stream; stream.getTracks().forEach(track=>track.addEventListener('ended',()=>{ if (mic===stream) fail(new Error('Mikrofon se odpojil')); }));
        const code=`class TwinCapture extends AudioWorkletProcessor { process(inputs) { const pcm=inputs[0]?.[0]; if(pcm) this.port.postMessage(pcm.slice()); return true; } } registerProcessor('twin-capture',TwinCapture);`;
        if (!workletReady) {
          const url=root.URL.createObjectURL(new Blob([code],{type:'text/javascript'}));
          try { await selectedAudio.audioWorklet.addModule(url); } finally { root.URL.revokeObjectURL(url); }
          if (own===generation) workletReady=true;
        }
        if (own!==generation) return;
        captureSource=selectedAudio.createMediaStreamSource(stream);
        captureNode=new root.AudioWorkletNode(selectedAudio,'twin-capture',{channelCount:1,channelCountMode:'explicit'});
        captureNode.port.onmessage=event=>{ if (own===generation) capture(event.data); };
        silentGain=selectedAudio.createGain(); silentGain.gain.value=0;
        captureSource.connect(captureNode); captureNode.connect(silentGain); silentGain.connect(selectedAudio.destination);
        notice='Mikrofon aktivován; PCM odchází pouze při nativním listening gate.'; failure=''; render();
      } catch(error) { if (own===generation) fail(error); }
      finally { if (own===generation) { activationBusy=false; render(); } }
    };
    get('start').onclick=async()=>{
      if (!eligible() || actor || pendingStart || !mic || !speakerReady || !configuration || audio?.state!=='running') return;
      const capability=configuration.engine==='gpt-live'?'gptLive':configuration.engine;
      if (!configuration.availability[capability]) { failure='Provider není dostupný.'; render(); return; }
      const own=generation; admission={operationId:root.crypto.randomUUID(),expectedBootId:context.bootId}; pendingStart=true; render();
      try {
        const cameraCapable=configuration.photoConsent && configuration.availability.camera && !!root.navigator?.mediaDevices?.getUserMedia &&
          typeof document.createElement('canvas').getContext==='function';
        const value=await request(context.instanceId,'/media/start',json({...admission,audioConsent:true,photoConsent:configuration.photoConsent,
          micAvailable:true,speakerAvailable:true,cameraAvailable:cameraCapable}));
        if (own!==generation) return;
        actor=session(value,context.bootId); inputGate(actor); admission=null; failure=''; notice='Nativní workflow přijat; phase určuje skutečný start. Kamera je jen dostupná API schopnost; oprávnění a fotku potvrzujete samostatně.';
        pendingStart=false; render(); void poll(own);
      } catch(error) { if (own===generation) { failure=error.message; void cancel('Přijetí media start není potvrzené; ruším jen tuto admission.'); } }
    };
    get('stop').onclick=()=>cancel();
    get('camera').onclick=async()=>{
      if (!eligible() || !photoRequest || camera || activationBusy || configuration?.availability.camera!==true) return;
      const own=generation, requested=photoRequest; activationBusy=true; render();
      try {
        const device=get('camera-device').value;
        const stream=await root.navigator.mediaDevices.getUserMedia({audio:false,video:{width:{ideal:640},height:{ideal:640},...(device?{deviceId:{exact:device}}:{})}});
        if (own!==generation || photoRequest!==requested) { stream.getTracks().forEach(track=>track.stop()); return; }
        camera=stream; stream.getTracks().forEach(track=>track.addEventListener('ended',()=>{ if (camera===stream) void declinePhoto('Kamera se odpojila.'); }));
        get('video').srcObject=stream; await get('video').play();
        if (own!==generation || photoRequest!==requested) return;
        notice='Kamera je pouze lokální náhled; nic se neodeslalo.'; render();
      } catch(error) { if (own===generation && photoRequest===requested) { failure=error.message; await declinePhoto('Kamera není dostupná; předávám unavailable nativnímu core.'); } }
      finally { if (own===generation) { activationBusy=false; render(); } }
    };
    get('capture').onclick=async()=>{
      if (!photoRequest || !camera) return;
      const own=generation, requested=photoRequest, video=get('video'), canvas=document.createElement('canvas');
      try {
        if (!video.videoWidth || !video.videoHeight) throw new Error('Kamera zatím nedodala obraz');
        const ratio=Math.min(1,640/Math.max(video.videoWidth,video.videoHeight));
        canvas.width=Math.max(1,Math.floor(video.videoWidth*ratio)); canvas.height=Math.max(1,Math.floor(video.videoHeight*ratio));
        canvas.getContext('2d').drawImage(video,0,0,canvas.width,canvas.height);
        let blob=null;
        for (const quality of [.85,.65,.45,.3]) {
          blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',quality));
          if (blob && blob.size<=262144) break;
        }
        if (!blob || blob.size>262144 || blob.type!=='image/jpeg') throw new Error('JPEG náhled překročil povolenou velikost');
        const bytes=stripJpegMetadata(new Uint8Array(await blob.arrayBuffer()));
        if (own!==generation || photoRequest!==requested) return;
        blob=new Blob([bytes],{type:'image/jpeg'});
        clearPhoto(); photoBlob=blob; photoUrl=root.URL.createObjectURL(blob); get('preview').src=photoUrl; get('preview').hidden=false;
        notice='Prohlédněte lokální fotku a potvrďte předání.'; render();
      } catch(error) { if (own===generation) { failure=error.message; render(); } }
    };
    get('retake').onclick=()=>{ clearPhoto(); render(); };
    async function declinePhoto(message='Fotka odmítnuta; nativní core dostane unavailable.') {
      if (!eligible() || !actor || !photoRequest) return;
      const selected=actor, requested=photoRequest, own=generation; photoRequest=null; clearPhoto();
      stopCamera();
      notice=message; render();
      try { await request(context.instanceId,'/media/photo',{method:'POST',headers:{...headers(selected,requested.inputEpoch,'0'),'Content-Type':'image/jpeg'},body:new Uint8Array(0)}); }
      catch(error) { if (own===generation && actor===selected && selected.inputEpoch===requested.inputEpoch) fail(error); }
    }
    get('skip-photo').onclick=()=>declinePhoto();
    get('accept').onclick=async()=>{
      if (!eligible() || !actor || !photoRequest || !photoBlob) return;
      const selected=actor, own=generation, bytes=photoBlob, requested=photoRequest; photoRequest=null; clearPhoto(); stopCamera(); render();
      try {
        await request(context.instanceId,'/media/photo',{method:'POST',headers:{...headers(selected,requested.inputEpoch,'0'),'Content-Type':'image/jpeg'},body:bytes});
        if (own===generation && actor===selected && selected.inputEpoch===requested.inputEpoch) { notice='Jedna schválená fotka předána nativnímu validátoru.'; render(); }
      } catch(error) { if (own===generation && actor===selected && selected.inputEpoch===requested.inputEpoch) fail(error); }
    };
    root.addEventListener('pagehide',()=>{ void cancel('Stránka zavřena; média zastavena.',true); });
    root.navigator?.mediaDevices?.addEventListener?.('devicechange',async()=>{
      if (!actor) return;
      const own=generation;
      try {
        const devices=await root.navigator.mediaDevices.enumerateDevices();
        if (own!==generation || !actor) return;
        const selected=[...(mic?.getTracks() || []),...(camera?.getTracks() || [])].map(track=>track.getSettings?.().deviceId).filter(Boolean);
        const sink=typeof audio?.sinkId==='string' ? audio.sinkId : '';
        if (selected.some(id=>!devices.some(device=>device.deviceId===id)) || sink && sink!=='default' && !devices.some(device=>device.deviceId===sink)) {
          fail(new Error('Aktivní media zařízení se odpojilo; spusťte novou session'));
        }
        // Granting camera permission can reveal devices without changing a route.
      } catch(error) { if (own===generation) fail(error); }
    });
    render();
    return {
      sync(next) {
        const key=JSON.stringify([next.instanceId,next.bootId,next.role,next.replay,next.mode]);
        if (key!==sourceKey) {
          void cancel('Zdroj/session změněna; média čekají na výslovnou aktivaci.'); configuration=null; get('dialogue').replaceChildren(); sourceKey=key;
          context=next; if (control()) void loadSettings();
        } else {
          context=next; if (actor && !eligible()) void cancel('Zdroj není online; média zastavena.');
        }
        render();
      },
      invalidate(reason) { sourceKey=''; return cancel(reason); },
      state:()=>({actor:actor?{...actor}:null,mic:!!mic,speaker:speakerReady,camera:!!camera,captureBytes:queue.bytes+inFlightBytes,queuedOutputBytes,pendingStart,generation}),
      capture, handle
    };
  }
  const api={RATE,CHUNK_BYTES,CAPTURE_BYTES,CaptureQueue,decimal,pcm16,floatPcm,stripJpegMetadata,settings,session,frame,mount};
  if (typeof module!=='undefined' && module.exports) module.exports=api;
  else root.TwinMedia=api;
})(typeof window==='undefined'?globalThis:window);
