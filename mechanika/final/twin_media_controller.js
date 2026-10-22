/* Headless browser HAL. Vue owns all controls and presentation. */
(function(root) {
  'use strict';
  const {RATE,CHUNK_BYTES,CAPTURE_BYTES,CaptureQueue,decimal,pcm16,floatPcm,stripJpegMetadata,settings,session,frame}=typeof module!=='undefined' && module.exports ? require('./twin_media_core.js') : root.TwinMediaCore;
  const OUTPUT_BYTES=RATE*2*4;
  // The production application emits this exact source as a same-origin asset.
  // Legacy standalone tools retain their blob fallback without requiring a
  // second processor implementation or changing capture/lifetime semantics.
  const CAPTURE_WORKLET_SOURCE=`class TwinCapture extends AudioWorkletProcessor { process(inputs) { const pcm=inputs[0]?.[0]; if(pcm) this.port.postMessage(pcm.slice()); return true; } } registerProcessor('twin-capture',TwinCapture);`;
  function captureWorkletUrl(installed,baseURI) {
    const base=new root.URL(baseURI), url=new root.URL(installed,base);
    const hosted=['http:','https:'].includes(base.protocol);
    // Capacitor's configured iOS asset origin has an opaque URL.origin. Match
    // its actual scheme/host instead of treating every opaque origin as equal.
    const packaged=base.protocol==='capacitor:' && base.hostname==='localhost' && !base.port;
    if ((!hosted && !packaged) || url.protocol!==base.protocol || url.host!==base.host || url.username || url.password || url.search || url.hash || !/^\/assets\/twin-capture-worklet-[a-f0-9]{16}\.js$/.test(url.pathname)) {
      throw new Error('Worklet musí být publikovaný soubor ze stejného originu aplikace');
    }
    return url.href;
  }
  function createMediaController({transport,onChange=()=>{},video=()=>null,document=root.document,parseEngines}) {
    let context=null, sourceKey='', configuration=null, actor=null, generation=0, failure='', notice='Média nejsou aktivovaná.';
    let audio=null, mic=null, camera=null, captureNode=null, captureSource=null, silentGain=null, speakerReady=false, workletReady=false;
    let listen=false, uploading=false, inFlightBytes=0, inputSequence=0n, retiredInputEpoch=null, lastSequence=null, timer=null, reading=false, pollCount=0;
    let queue=new CaptureQueue(), queuedOutputBytes=0, consumedBytes=0n, scheduledUntil=0, drainFrame=null;
    let photoRequest=null, photoBlob=null, photoUrl=null, pendingStart=false, admission=null, saving=false, activationBusy=false, ackTail=Promise.resolve();
    const sources=new Set(), controllers=new Set();
    function control() { return context?.active && !context.replay && context.role==='control' && context.mode==='simulation' && !!context.bootId; }
    function eligible() { return control() && context.profile==='integration' && context.status==='online'; }
    const selections = {engine:'fake',voice:'cedar',speed:'1',variant:'main',consent:false,micDevice:'',speakerDevice:'',cameraDevice:''};
    let deviceOptions={mic:[],speaker:[],camera:[]}, dialogue=[];
    const actions={}; let disposed=false;
    function readSettings(value) {
      const parsed=settings(value);
      if (parseEngines) parsed.engines=parseEngines(value.engines);
      return parsed;
    }
    function capability(engine) { return configuration?.engines?.engines.find(item=>item.engine===engine); }
    function providerReady(value) {
      const name=value.engine==='gpt-live'?'gptLive':value.engine;
      if (!configuration?.availability[name]) return false;
      if (!parseEngines || value.engine==='fake') return true; // Legacy standalone compatibility; Vue always supplies the canonical parser.
      const selected=capability(value.engine);
      return selected?.ready===true && selected.inputModes.includes('audio') && selected.voices.includes(value.voice) &&
        +value.speed>=selected.speed.min && +value.speed<=selected.speed.max && (selected.speed.supported || +value.speed===selected.speed.default);
    }
    function unsavedSettings() {
      return !configuration || selections.engine!==configuration.engine || selections.voice!==configuration.voice || +selections.speed!==configuration.speed ||
        selections.variant!==configuration.presentationVariant || selections.consent!==configuration.photoConsent;
    }
    function cameraSupported() { return !parseEngines || configuration?.engine==='fake' || capability(configuration?.engine)?.cameraSources.includes('phone')===true; }
    function render() {
      const busy=!!actor || pendingStart || saving || activationBusy;
      const disabled={save:!eligible() || busy || !configuration,reload:!control() || busy,
        engine:!eligible() || busy || !configuration,voice:!eligible() || busy || !configuration,
        variant:!eligible() || busy || !configuration,consent:!eligible() || busy || !configuration,
        speed:!eligible() || busy || !configuration || (parseEngines && selections.engine!=='fake' ? capability(selections.engine)?.speed.supported!==true : selections.engine==='gpt-live'),
        mic:!eligible() || busy || !!mic || configuration?.availability.mic!==true,
        speaker:!eligible() || busy || speakerReady || configuration?.availability.speaker!==true,
        devices:!eligible() || busy,micDevice:!eligible() || busy,cameraDevice:!eligible() || busy,
        speakerDevice:!eligible() || busy || !audio?.setSinkId,
        start:!eligible() || busy || !mic || !speakerReady || !configuration || audio?.state!=='running' || unsavedSettings() || !providerReady(configuration),
        stop:!actor && !pendingStart && !activationBusy && !mic && !camera && !audio,
        camera:!eligible() || !photoRequest || !!camera || activationBusy || configuration?.availability.camera!==true || !cameraSupported(),
        capture:!photoRequest || !camera,accept:!photoRequest || !photoBlob,retake:!photoBlob,skipPhoto:!photoRequest};
      if (!disposed) onChange({visible:control(),eligible:eligible(),disabled,settings:{...selections},devices:deviceOptions,
        configuration,dialogue:[...dialogue],previewUrl:photoUrl,photoRequested:!!photoRequest,
        notice,failure,phase:actor?.phase || null,mic:!!mic,speaker:speakerReady,camera:!!camera,
        captureBytes:queue.bytes+inFlightBytes,queuedOutputBytes,pendingStart,generation});
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
      photoUrl=null;
    }
    function stopCamera() {
      const previous=camera; camera=null;
      for (const track of previous?.getTracks() || []) track.stop(); if (video()) video().srcObject=null;
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
      mic=null; camera=null; if (video()) video().srcObject=null;
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
    function fail(error) { dialogue=[]; configuration=null; failure=error?.message || 'Média selhala.'; void cancel('Média selhala a lokálně se zastavila.'); }
    async function loadSettings() {
      if (!control()) return;
      const own=generation, id=context.instanceId;
      try {
        const value=readSettings(await request(id,'/settings'));
        if (own!==generation) return;
        configuration=value;
        selections.engine=value.engine; selections.voice=value.voice; selections.speed=String(value.speed);
        selections.variant=value.presentationVariant; selections.consent=value.photoConsent; failure='';
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
        deviceOptions=Object.fromEntries([['mic','audioinput','Mikrofon'],['speaker','audiooutput','Výstup'],['camera','videoinput','Kamera']].map(([key,kind,label])=>[key,devices.filter(device=>device.kind===kind).map((device,index)=>({id:device.deviceId,label:device.label || `${label} ${index+1}`}))]));
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
      if (output.text) dialogue=[...dialogue,output.text].slice(-20);
      if (['completed','failed','closed'].includes(output.kind)) {
        const message=output.kind==='failed' ? output.error || 'Nativní workflow selhal.' : 'Nativní workflow skončil.';
        // Native owns terminal admission/presentation. A normal end is not Stop.
        if (output.kind==='failed') { dialogue=[]; configuration=null; }
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
              dialogue=[]; configuration=null; localStop(); failure=current.error || 'Nativní workflow selhal.';
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
        const captured=pcm16(samples), capacity=CAPTURE_BYTES-inFlightBytes;
        const bytes=captured.length>capacity ? captured.slice(captured.length-capacity) : captured;
        // Keep the newest unsent samples during network jitter. In-flight PCM
        // cannot be recalled; sequence numbers belong only to submitted blocks.
        const discarded=queue.trim(capacity-bytes.length)+captured.length-bytes.length;
        queue.push(bytes); void upload();
        const warning='Mikrofonní přenos nestíhá; část neodeslaného zvuku byla přeskočena. Rozhovor pokračuje.';
        if (discarded && notice!==warning) { notice=warning; render(); }
      }
      catch (error) { fail(error); }
    }
    actions['reload']=loadSettings;
    actions['devices']=enumerate;
    actions['speaker-device-change']=()=>{ if (!actor && !pendingStart) { speakerReady=false; render(); } };
    actions['mic-device-change']=()=>{
      if (actor || pendingStart || activationBusy) return;
      captureNode?.disconnect(); captureSource?.disconnect(); silentGain?.disconnect();
      captureNode=null; captureSource=null; silentGain=null;
      const previous=mic; mic=null; for (const track of previous?.getTracks() || []) track.stop(); render();
    };
    actions['engine-change']=()=>{
      const selected=capability(selections.engine);
      if (selected && !selected.speed.supported) selections.speed=String(selected.speed.default);
      else if (selections.engine==='gpt-live') selections.speed='1';
      render();
    };
    actions['save']=async event=>{
      event?.preventDefault?.(); if (!eligible() || actor || pendingStart || saving || !configuration) return;
      const selectedEngine=selections.engine;
      if (!providerReady(selections)) { failure='Zvolený engine, hlas nebo tempo nejsou dostupné. Ověřte schopnosti a autorizaci serveru.'; render(); return; }
      const own=generation; saving=true; render();
      try {
        const value=await request(context.instanceId,'/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:configuration.revision,
          engine:selectedEngine,voice:selections.voice,speed:+selections.speed,photoConsent:selections.consent,presentationVariant:selections.variant})});
        if (own===generation) { configuration=readSettings(value); failure=''; notice='Nativní nastavení uloženo.'; }
      } catch(error) { if (own===generation) failure=error.message; }
      finally { if (own===generation) { saving=false; render(); } }
    };
    actions['speaker']=async()=>{
      if (!eligible() || actor || pendingStart || activationBusy || speakerReady || configuration?.availability.speaker!==true) return;
      const own=generation; activationBusy=true; render();
      try {
        const selectedAudio=ensureAudio(); await selectedAudio.resume();
        if (own!==generation) return;
        const sink=selections.speakerDevice;
        if (sink) { if (!selectedAudio.setSinkId) throw new Error('Browser neumí vybrat reproduktor; zvolte systémový výstup'); await selectedAudio.setSinkId(sink); }
        if (own!==generation || selectedAudio.state!=='running') return;
        speakerReady=true; notice='Reproduktor aktivován; software drain není potvrzení slyšitelnosti.'; failure=''; render();
      } catch(error) { if (own===generation) { failure=error.message; render(); } }
      finally { if (own===generation) { activationBusy=false; render(); } }
    };
    actions['mic']=async()=>{
      if (!eligible() || actor || pendingStart || activationBusy || mic || configuration?.availability.mic!==true) return;
      const own=generation; activationBusy=true; render();
      try {
        const selectedAudio=ensureAudio(); await selectedAudio.resume();
        if (own!==generation) return;
        const device=selections.micDevice;
        const stream=await root.navigator.mediaDevices.getUserMedia({audio:{channelCount:1,echoCancellation:true,noiseSuppression:true,autoGainControl:true,...(device?{deviceId:{exact:device}}:{})},video:false});
        if (own!==generation) { stream.getTracks().forEach(track=>track.stop()); return; }
        mic=stream; stream.getTracks().forEach(track=>track.addEventListener('ended',()=>{ if (mic===stream) fail(new Error('Mikrofon se odpojil')); }));
        if (!workletReady) {
          const installed=document.querySelector?.('meta[name="kufr-capture-worklet"]')?.content;
          if (installed) {
            await selectedAudio.audioWorklet.addModule(captureWorkletUrl(installed,document.baseURI));
          } else {
            const url=root.URL.createObjectURL(new Blob([CAPTURE_WORKLET_SOURCE],{type:'text/javascript'}));
            try { await selectedAudio.audioWorklet.addModule(url); } finally { root.URL.revokeObjectURL(url); }
          }
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
    actions['start']=async()=>{
      if (!eligible() || actor || pendingStart || !mic || !speakerReady || !configuration || audio?.state!=='running') return;
      if (unsavedSettings() || !providerReady(configuration)) { failure='Nejdříve uložte podporované nastavení; vybraný engine se automaticky nemění.'; render(); return; }
      const own=generation; admission={operationId:root.crypto.randomUUID(),expectedBootId:context.bootId}; pendingStart=true; render();
      try {
        const cameraCapable=configuration.photoConsent && configuration.availability.camera && cameraSupported() && !!root.navigator?.mediaDevices?.getUserMedia &&
          typeof document.createElement('canvas').getContext==='function';
        const value=await request(context.instanceId,'/media/start',json({...admission,audioConsent:true,photoConsent:configuration.photoConsent,
          micAvailable:true,speakerAvailable:true,cameraAvailable:cameraCapable}));
        if (own!==generation) return;
        actor=session(value,context.bootId); inputGate(actor); admission=null; failure=''; notice='Nativní workflow přijat; phase určuje skutečný start. Kamera je jen dostupná API schopnost; oprávnění a fotku potvrzujete samostatně.';
        pendingStart=false; render(); void poll(own);
      } catch(error) { if (own===generation) { failure=error.message; void cancel('Přijetí media start není potvrzené; ruším jen tuto admission.'); } }
    };
    actions['stop']=()=>cancel();
    actions['camera']=async()=>{
      if (!eligible() || !photoRequest || camera || activationBusy || configuration?.availability.camera!==true || !cameraSupported()) return;
      const own=generation, requested=photoRequest; activationBusy=true; render();
      try {
        const device=selections.cameraDevice;
        const stream=await root.navigator.mediaDevices.getUserMedia({audio:false,video:{width:{ideal:640},height:{ideal:640},...(device?{deviceId:{exact:device}}:{})}});
        if (own!==generation || photoRequest!==requested) { stream.getTracks().forEach(track=>track.stop()); return; }
        camera=stream; stream.getTracks().forEach(track=>track.addEventListener('ended',()=>{ if (camera===stream) void declinePhoto('Kamera se odpojila.'); }));
        if (!video()) throw new Error("Camera preview is unavailable"); video().srcObject=stream; await video().play();
        if (own!==generation || photoRequest!==requested) return;
        notice='Kamera je pouze lokální náhled; nic se neodeslalo.'; render();
      } catch(error) { if (own===generation && photoRequest===requested) { failure=error.message; await declinePhoto('Kamera není dostupná; předávám unavailable nativnímu core.'); } }
      finally { if (own===generation) { activationBusy=false; render(); } }
    };
    actions['capture']=async()=>{
      if (!photoRequest || !camera) return;
      const own=generation, requested=photoRequest, preview=video(), canvas=document.createElement('canvas');
      try {
        if (!preview.videoWidth || !preview.videoHeight) throw new Error('Kamera zatím nedodala obraz');
        const ratio=Math.min(1,640/Math.max(preview.videoWidth,preview.videoHeight));
        canvas.width=Math.max(1,Math.floor(preview.videoWidth*ratio)); canvas.height=Math.max(1,Math.floor(preview.videoHeight*ratio));
        canvas.getContext('2d').drawImage(preview,0,0,canvas.width,canvas.height);
        let blob=null;
        for (const quality of [.85,.65,.45,.3]) {
          blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',quality));
          if (blob && blob.size<=262144) break;
        }
        if (!blob || blob.size>262144 || blob.type!=='image/jpeg') throw new Error('JPEG náhled překročil povolenou velikost');
        const bytes=stripJpegMetadata(new Uint8Array(await blob.arrayBuffer()));
        if (own!==generation || photoRequest!==requested) return;
        blob=new Blob([bytes],{type:'image/jpeg'});
        clearPhoto(); photoBlob=blob; photoUrl=root.URL.createObjectURL(blob);
        notice=configuration?.engine==='fake'
          ? 'Prohlédněte lokální fotku a potvrďte předání syntetické fixture. Fotografie se do OpenAI neodesílá.'
          : 'Prohlédněte lokální fotku. Přijetím potvrdíte odeslání společnosti OpenAI pro popis kostýmu v rozhovoru.'; render();
      } catch(error) { if (own===generation) { failure=error.message; render(); } }
    };
    actions['retake']=()=>{ clearPhoto(); render(); };
    async function declinePhoto(message='Fotka odmítnuta; nativní core dostane unavailable.') {
      if (!eligible() || !actor || !photoRequest) return;
      const selected=actor, requested=photoRequest, own=generation; photoRequest=null; clearPhoto();
      stopCamera();
      notice=message; render();
      try { await request(context.instanceId,'/media/photo',{method:'POST',headers:{...headers(selected,requested.inputEpoch,'0'),'Content-Type':'image/jpeg'},body:new Uint8Array(0)}); }
      catch(error) { if (own===generation && actor===selected && selected.inputEpoch===requested.inputEpoch) fail(error); }
    }
    actions['skip-photo']=()=>declinePhoto();
    actions['accept']=async()=>{
      if (!eligible() || !actor || !photoRequest || !photoBlob) return;
      const selected=actor, own=generation, bytes=photoBlob, requested=photoRequest; photoRequest=null; clearPhoto(); stopCamera(); render();
      try {
        await request(context.instanceId,'/media/photo',{method:'POST',headers:{...headers(selected,requested.inputEpoch,'0'),'Content-Type':'image/jpeg'},body:bytes});
        if (own===generation && actor===selected && selected.inputEpoch===requested.inputEpoch) {
          notice=configuration?.engine==='fake'
            ? 'Jedna schválená fotka předána syntetické fixture bez poskytovatele OpenAI.'
            : 'Jedna schválená fotka předána ke zpracování OpenAI pro popis kostýmu v rozhovoru.'; render();
        }
      } catch(error) { if (own===generation && actor===selected && selected.inputEpoch===requested.inputEpoch) fail(error); }
    };
    const pagehide=()=>{ void cancel('Stránka zavřena; média zastavena.',true); };
    root.addEventListener('pagehide',pagehide);
    const devicechange=async()=>{
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
    };
    root.navigator?.mediaDevices?.addEventListener?.('devicechange',devicechange);
    render();
    return {
      action(name,event) { if (disposed) return; return actions[name]?.(event); },
      selection(key,value) { if (disposed || actor || pendingStart || activationBusy || !Object.hasOwn(selections,key)) return; selections[key]=value; actions[({'micDevice':'mic-device','speakerDevice':'speaker-device'}[key] || key)+'-change']?.(); render(); },
      dispose() { if (disposed) return; dialogue=[]; configuration=null; void cancel('Workshop opuštěn; lokální média zastavena.',true); disposed=true; root.removeEventListener('pagehide',pagehide); root.navigator?.mediaDevices?.removeEventListener?.('devicechange',devicechange); },
      sync(next) {
        if (disposed) return;
        const key=JSON.stringify([next.instanceId,next.bootId,next.role,next.replay,next.mode,next.profile]);
        if (key!==sourceKey) {
          void cancel('Zdroj/session změněna; média čekají na výslovnou aktivaci.'); configuration=null; dialogue=[]; sourceKey=key;
          context=next; if (control()) void loadSettings();
        } else {
          context=next; if ((actor || mic || camera || audio || pendingStart) && !eligible()) void cancel('Zdroj není online; média zastavena.');
        }
        render();
      },
      invalidate(reason) { sourceKey=''; dialogue=[]; configuration=null; return cancel(reason); },
      state:()=>({actor:actor?{...actor}:null,mic:!!mic,speaker:speakerReady,camera:!!camera,captureBytes:queue.bytes+inFlightBytes,queuedOutputBytes,pendingStart,generation}),
      capture, handle
    };
  }
  const api={createMediaController,CAPTURE_WORKLET_SOURCE,captureWorkletUrl};
  if (typeof module!=='undefined' && module.exports) module.exports=api;
  else root.TwinMediaController=api;
})(typeof window==='undefined'?globalThis:window);
