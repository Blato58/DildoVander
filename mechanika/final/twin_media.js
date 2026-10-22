/* Legacy distribution adapter. Canonical logic lives in twin_media_core.js. */
(function(root) {
  "use strict";
  const {RATE,CHUNK_BYTES,CAPTURE_BYTES,CaptureQueue,decimal,pcm16,floatPcm,stripJpegMetadata,settings,session,frame} = typeof module !== "undefined" && module.exports ? require("./twin_media_core.js") : root.TwinMediaCore;
  const OUTPUT_BYTES = RATE * 2 * 4;
  function mount(document, container, transport) {
    const { button } = root.VapourUI;
    const section = document.createElement('section'); section.className='twin-media'; section.id='twin-media';
    section.setAttribute('aria-labelledby','twin-media-heading');
    section.innerHTML=`<h3 id="twin-media-heading">Normální AI kufr · browser HAL</h3>
      <p data-media-scope></p><p data-media-status role="status"></p><p data-media-error role="alert"></p>
      <details><summary>Nastavení nativního rozhovoru</summary><form data-media-settings>
        <label>Engine<select data-media-engine><option value="fake">Fake služby</option><option value="realtime">Realtime</option><option value="gpt-live">GPT-Live</option></select></label>
        <label>Hlas<select data-media-voice><option>cedar</option><option>marin</option><option>coral</option></select></label>
        <label>Rychlost<select data-media-speed><option>0.8</option><option selected>1</option><option>1.2</option></select></label>
        <label>Prezentace<select data-media-variant><option>main</option><option>short</option></select></label>
        <label class="twin-media-checkbox"><input data-media-consent type="checkbox"> Souhlasím s odesláním jedné fotografie společnosti OpenAI pro popis kostýmu v rozhovoru. Syntetická fixture fotografii do OpenAI neodesílá.</label>
        ${button({"label": "Uložit nastavení", "attributes": {"data-media-save": ""}, "type": "submit"})}${button({"label": "Načíst nastavení", "attributes": {"data-media-reload": ""}})}
        <p data-media-revision></p>
      </form></details>
      <div class="twin-media-devices"><label>Mikrofon<select data-media-mic-device><option value="">Systémový mikrofon</option></select></label>
        <label>Reproduktor<select data-media-speaker-device><option value="">Systémový výstup</option></select></label>
        <label>Kamera<select data-media-camera-device><option value="">Systémová kamera</option></select></label>
        ${button({"label": "Načíst lokální zařízení", "attributes": {"data-media-devices": ""}})}${button({"label": "Aktivovat mikrofon", "attributes": {"data-media-mic": ""}})}${button({"label": "Aktivovat reproduktor", "attributes": {"data-media-speaker": ""}})}
      </div>
      <div class="twin-media-actions">${button({"label": "Spustit rozhovor", "attributes": {"data-media-start": ""}, "variant": "primary"})}${button({"label": "Stop médií", "attributes": {"data-media-stop": ""}, "variant": "danger"})}</div>
      <p>Zvuk ukazuje software enqueue/drain evidence, nikoli potvrzení slyšitelnosti. Rozhoduje nativní core.</p>
      <div data-media-photo hidden><p>Nativní core žádá jednu fotku. Pořízení lokálního náhledu nic neodesílá. Přijetím potvrdíte předání fotografie společnosti OpenAI pro popis kostýmu v rozhovoru. Syntetická fixture pracuje bez předání OpenAI.</p>
        ${button({"label": "Aktivovat kameru", "attributes": {"data-media-camera": ""}})}<video data-media-video autoplay playsinline muted></video>
        ${button({"label": "Pořídit náhled", "attributes": {"data-media-capture": ""}})}<img data-media-preview alt="Lokální náhled fotky před předáním" hidden>
        ${button({"label": "Přijmout a předat jednu fotku", "attributes": {"data-media-accept": ""}, "variant": "primary"})}${button({"label": "Vymazat náhled", "attributes": {"data-media-retake": ""}})}${button({"label": "Pokračovat bez fotky", "attributes": {"data-media-skip-photo": ""}})}</div>
      <ol data-media-dialogue aria-label="Transientní nativní dialog" tabindex="0"></ol>`;
    container.append(section);
    const get=key=>section.querySelector(`[data-media-${key}]`);
    const {createMediaController}=typeof module!=='undefined' && module.exports ? require('./twin_media_controller.js') : root.TwinMediaController;
    const controller=createMediaController({document,transport,video:()=>get('video'),onChange:state=>{
      section.hidden=!state.visible;
      get('scope').textContent='Soukromá integrační simulace; fyzické ověření není dostupné.';
      get('status').textContent=(state.phase ? 'Nativní phase: '+state.phase+'. ' : '')+state.notice;
      get('error').textContent=state.failure;
      for (const [key,value] of Object.entries(state.disabled)) {
        const dom={micDevice:'mic-device',speakerDevice:'speaker-device',cameraDevice:'camera-device',skipPhoto:'skip-photo'}[key] || key;
        if(get(dom)) get(dom).disabled=value;
      }
      for (const key of ['engine','voice','speed','variant']) get(key).value=state.settings[key];
      get('consent').checked=state.settings.consent;
      for (const key of ['mic','speaker','camera']) {
        const select=get(key+'-device'); select.replaceChildren(new root.Option('Systémové zařízení',''));
        for(const option of state.devices[key]) select.add(new root.Option(option.label,option.id));
        select.value=state.settings[key+'Device'];
      }
      get('photo').hidden=!state.photoRequested;
      get('preview').hidden=!state.previewUrl;
      if(state.previewUrl) get('preview').src=state.previewUrl; else get('preview').removeAttribute('src');
      get('dialogue').replaceChildren();
      for(const text of state.dialogue) { const item=document.createElement('li'); item.textContent=text; get('dialogue').append(item); }
      get('revision').textContent=state.configuration ? 'Revision '+state.configuration.revision : 'Nastavení není načtené.';
    }});
    for(const name of ['reload','devices','speaker','mic','start','stop','camera','capture','retake','skip-photo','accept']) get(name).onclick=event=>controller.action(name,event);
    get('settings').onsubmit=event=>controller.action('save',event);
    for(const [dom,key] of [['engine','engine'],['voice','voice'],['speed','speed'],['variant','variant'],['consent','consent'],['mic-device','micDevice'],['speaker-device','speakerDevice'],['camera-device','cameraDevice']]) get(dom).onchange=()=>controller.selection(key,key==='consent' ? get(dom).checked : get(dom).value);
    return controller;
  }
  const api={RATE,CHUNK_BYTES,CAPTURE_BYTES,CaptureQueue,decimal,pcm16,floatPcm,stripJpegMetadata,settings,session,frame,mount};
  if (typeof module!=='undefined' && module.exports) module.exports=api;
  else root.TwinMedia=api;
})(typeof window==='undefined'?globalThis:window);
