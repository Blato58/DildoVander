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
    trim(limit) {
      const discarded=Math.max(0,this.bytes-limit);
      let remaining=discarded;
      while (remaining>0) {
        const part=this.parts[0], count=Math.min(part.length,remaining);
        remaining-=count; this.bytes-=count;
        if (count===part.length) this.parts.shift(); else this.parts[0]=part.subarray(count);
      }
      return discarded;
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

  const api = {RATE,CHUNK_BYTES,CAPTURE_BYTES,CaptureQueue,decimal,pcm16,floatPcm,stripJpegMetadata,settings,session,frame};
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.TwinMediaCore = api;
})(typeof window === "undefined" ? globalThis : window);
