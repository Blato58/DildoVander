/** Lifecycle around the original extracted F2 WebGL engine. Vue owns controls/overlays. */
import { graphics } from './graphics.mjs';
import { overlaySnapshot } from './overlays.mjs';
export const uploadGeometry=graphics.uploadGeometry;
const {look}=graphics;
export const matchesPart = (name, pattern) => name === pattern || name.startsWith(pattern.endsWith('*') ? pattern.slice(0, -1) : pattern.endsWith('_') ? pattern : pattern + '_');
export function partVisible(mesh, state, guide) {
  if (state.hidden.includes(mesh.id) || state.hiddenGroups.includes(mesh.group) || state.isolate && state.isolate !== mesh.id) return false;
  if (mesh.transportOnly && state.mode !== 'transport' || mesh.operationOnly && state.mode === 'transport') return false;
  if (!state.wands && (mesh.group === 'wand' || mesh.name.startsWith('wand_'))) return false;
  if (state.mode === 'transport' && mesh.group === 'wand') return false;
  if (mesh.name === 'powerbank' && (state.mode === 'transport' || !state.bank)) return false;
  if ((mesh.group.includes('fabric') || mesh.group === 'textile' || mesh.name.startsWith('soft_liner_')) && (state.mode === 'transport' || !state.textile)) return false;
  if (mesh.group === 'cover' && !state.cover || mesh.group === 'case' && !state.case || mesh.name.startsWith('tray_') && !state.beds || ['hardware','moving_hw'].includes(mesh.group) && !state.hardware) return false;
  if (state.visualMode === 'cutaway' && ['case', 'cover'].includes(mesh.group)) return false;
  if (state.visualMode === 'structure' && ['hardware', 'moving_hw', 'wand', 'textile', 'fabric', 'lid_fabric', 'cover'].includes(mesh.group)) return false;
  if (state.visualMode === 'electronics' && !['hardware', 'moving_hw', 'case', 'lid', 'fixed'].includes(mesh.group)) return false;
  if (state.mode === 'assembly' && state.assemblyIndex >= 0) {
    const step = guide.steps[state.assemblyIndex];
    if (step?.installation?.kind === 'bench') {
      const active = [...(step.parts || []), ...(step.preparedParts || []), ...(step.installation.targets || [])];
      const installed = guide.steps.slice(0, state.assemblyIndex + 1).flatMap(s => s.caseParts ?? s.parts ?? []);
      const patterns = step.benchParts ?? (active.length ? active : installed);
      return patterns.some(p => matchesPart(mesh.name, p));
    }
    const patterns = guide.steps.slice(0, state.assemblyIndex + 1).flatMap(s => s.caseParts ?? s.parts ?? []);
    return patterns.some(p => matchesPart(mesh.name, p)) || ['case', 'lid', 'lid_hardware'].includes(mesh.group);
  }
  return true;
}
function newlyInstalled(mesh,state,guide) {
  if(state.mode!=='assembly')return false;
  const step=guide.steps[state.assemblyIndex];
  const patterns=step?.installation?.kind==='bench' ? [...(step.parts || []),...(step.preparedParts || [])] : step?.caseParts ?? step?.parts ?? [];
  return patterns.some(pattern=>matchesPart(mesh.name,pattern));
}
export function movement(mesh,model,state) {return graphics.movement(mesh,model,state);}

export const defaultSceneState = () => ({ mode: 'operation', visualMode: 'complete', assemblyIndex: -1, hidden: [], hiddenGroups: [], isolate: null, selected: null, colors: {}, opacity: 1, lid: 105, wands: true, bank: true, textile: true, cover: true, case: true, beds: true, hardware: true, dimensions: false, inspect: false, wires: false, angles: [0,0,0,0,0,0], ledColors: {}, outputs: {}, uncertain: [], selectedWire: '', selectedEndpoint: '', wireScope: 'branch' });

/** @param {HTMLCanvasElement} canvas @param {any} model @param {{onSelect?:(id:string|null)=>void,onCamera?:(state:any)=>void,onError?:(message:string)=>void,onOverlay?:(snapshot:any)=>void,onHover?:(id:string|null)=>void}} options */
export function createViewerEngine(canvas, model, { onSelect = () => {}, onCamera = () => {}, onError = () => {}, onOverlay = () => {}, onHover = () => {} } = {}) {
  const gl = canvas.getContext('webgl', { antialias: true });
  if (!gl) throw new Error('WebGL není dostupné. Model vyžaduje podporovaný grafický prohlížeč.');
  let state = defaultSceneState(), disposed = false, frame = null, automaticFit=true, cameraTransition=null, hovered=null, shiftDown=false;
  let yaw = -1.05, pitch = .66, distance = 1050, target = [...model.presentation.target];
  const listeners = [], shaders = [], buffers = [];
  const modelBounds = [0, 1, 2].map(i => [Math.min(...model.meshes.map(m => m.bounds[i])), Math.max(...model.meshes.map(m => m.bounds[i + 3]))]);
  const scale = Math.max(100, ...modelBounds.map(([low, high]) => high - low));
  const shader = (type, source) => {
    const value = gl.createShader(type); shaders.push(value); gl.shaderSource(value, source); gl.compileShader(value);
    if (!gl.getShaderParameter(value, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(value) || 'WebGL shader failed');
    return value;
  };
  const program = gl.createProgram();
  const framebuffer = gl.createFramebuffer(), texture = gl.createTexture(), depth = gl.createRenderbuffer();
  let observer, themeObserver;
  function dispose() {
    if (disposed) return; disposed = true;
    if (frame !== null) cancelAnimationFrame(frame);
    observer?.disconnect(); themeObserver?.disconnect(); listeners.forEach(remove => remove());
    buffers.forEach(value => gl.deleteBuffer(value)); shaders.forEach(value => gl.deleteShader(value));
    gl.deleteProgram(program); gl.deleteFramebuffer(framebuffer); gl.deleteTexture(texture); gl.deleteRenderbuffer(depth);
    for (const pointer of pointers.keys()) if (canvas.hasPointerCapture?.(pointer)) canvas.releasePointerCapture(pointer);
    pointers.clear();
  }
  const pointers = new Map();
  try {
    gl.attachShader(program, shader(gl.VERTEX_SHADER, graphics.shaders.vertex));
    gl.attachShader(program, shader(gl.FRAGMENT_SHADER, graphics.shaders.fragment));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) || 'WebGL linking failed');
    gl.useProgram(program);
    const uniforms = Object.fromEntries(['projection', 'view', 'move', 'color', 'picking', 'highlight', 'opacity'].map(key => [key, gl.getUniformLocation(program, key)]));
    const position = gl.getAttribLocation(program, 'p'), normal = gl.getAttribLocation(program, 'n');
    gl.enableVertexAttribArray(position); gl.enableVertexAttribArray(normal); gl.enable(gl.DEPTH_TEST);
    const geometry = model.meshes.map((mesh, index) => {
      const buffer = gl.createBuffer(); buffers.push(buffer); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      const data = uploadGeometry(mesh); gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
      return { mesh, buffer, count: data.length / 6, pickId: index + 1 };
    });
    function resize() {
      const ratio = Math.min(2, globalThis.devicePixelRatio || 1);
      const width = Math.max(1, Math.round(canvas.clientWidth * ratio)); const height = Math.max(1, Math.round(canvas.clientHeight * ratio));
      if (canvas.width === width && canvas.height === height) return;
      canvas.width = width; canvas.height = height;
      gl.bindTexture(gl.TEXTURE_2D, texture); gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.bindRenderbuffer(gl.RENDERBUFFER, depth); gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, width, height);
      gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer); gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth); gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }
    function draw(picking = false) {
      if (disposed) return;
      resize(); gl.useProgram(program); gl.viewport(0, 0, canvas.width, canvas.height);
      const theme=globalThis.document?.documentElement?.getAttribute('data-theme');
      const light=theme==='light' || theme==='system' && globalThis.matchMedia?.('(prefers-color-scheme: light)').matches;
      const background=light?[238,241,245]:[27,36,48]; gl.clearColor(...(picking?[0,0,0]:background.map(channel=>channel/255)),picking?0:1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
      if(automaticFit) {
        const fitted=graphics.fitScene(model.meshes.filter(mesh=>partVisible(mesh,state,model.assemblyGuide)),model,state,yaw,pitch,canvas.clientWidth,canvas.clientHeight);
        if(fitted) {target=fitted.target;distance=fitted.distance;}
      }
      if(cameraTransition) {
        const progress=Math.min(1,(performance.now()-cameraTransition.start)/240),eased=1-(1-progress)**3;
        target=cameraTransition.from.map((value,axis)=>value+(cameraTransition.target[axis]-value)*eased);
        distance=cameraTransition.fromDistance+(cameraTransition.distance-cameraTransition.fromDistance)*eased;
        if(progress===1)cameraTransition=null;else invalidate();
      }
      const projection=graphics.perspective(canvas.width/canvas.height,distance,scale);
      const eye=[target[0]+distance*Math.cos(pitch)*Math.cos(yaw),target[1]+distance*Math.cos(pitch)*Math.sin(yaw),target[2]+distance*Math.sin(pitch)];
      const view=look(eye,target);
      gl.uniformMatrix4fv(uniforms.projection,false,projection);gl.uniformMatrix4fv(uniforms.view,false,view);gl.uniform1i(uniforms.picking,picking?1:0);
      for (const item of [...geometry].sort((a, b) => Number(['case', 'lid'].includes(a.mesh.group)) - Number(['case', 'lid'].includes(b.mesh.group)))) {
        const { mesh, buffer, count, pickId } = item;
        if (!partVisible(mesh, state, model.assemblyGuide)) continue;
        const uncertain = state.uncertain.includes(mesh.id);
        const opacity = picking ? 1 : uncertain ? .28 : state.visualMode==='electronics' && !['hardware','moving_hw'].includes(mesh.group) ? .12 : ['case', 'lid'].includes(mesh.group) ? state.opacity : 1;
        if (opacity < 1) { gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA); gl.depthMask(false); }
        else { gl.disable(gl.BLEND); gl.depthMask(true); }
        const stored = state.colors[mesh.id];
        const color = typeof stored === 'string' && /^#[a-f0-9]{6}$/i.test(stored) ? [1, 3, 5].map(i => parseInt(stored.slice(i, i + 2), 16) / 255) : mesh.color;
        const output = state.outputs[mesh.name];
        const outputColor = output === 'cancel' ? [.906,.502,.486] : output === 'voice-busy' ? [.549,.733,.867] : output ? [.851,.718,.467] : null;
        gl.uniform3fv(uniforms.color, picking ? [(pickId&255)/255,((pickId>>8)&255)/255,((pickId>>16)&255)/255] : state.ledColors[mesh.name] || outputColor || color);
        gl.uniform1f(uniforms.opacity, opacity); gl.uniform1f(uniforms.highlight, !picking && mesh.id === (state.selected || hovered) ? .65 : newlyInstalled(mesh,state,model.assemblyGuide) ? .45 : output ? .35 : 0);
        gl.uniformMatrix4fv(uniforms.move, false, movement(mesh, model, state));
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer); gl.vertexAttribPointer(position, 3, gl.FLOAT, false, 24, 0); gl.vertexAttribPointer(normal, 3, gl.FLOAT, false, 24, 12);
        gl.drawArrays(gl.TRIANGLES, 0, count);
      }
      gl.depthMask(true); gl.disable(gl.BLEND);
      if(!picking) onOverlay(overlaySnapshot(model,{...state,inspect:state.inspect || shiftDown,selected:state.selected || hovered},model.meshes.filter(mesh=>partVisible(mesh,state,model.assemblyGuide)),projection,view,canvas.clientWidth,canvas.clientHeight));
    }

    function invalidate() { if (!disposed && frame === null) frame = requestAnimationFrame(() => { frame = null; draw(); }); }
    function pick(x, y, select=true) {
      resize(); gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer); if(gl.checkFramebufferStatus(gl.FRAMEBUFFER)!==gl.FRAMEBUFFER_COMPLETE){gl.bindFramebuffer(gl.FRAMEBUFFER,null);return;} draw(true);
      const rect = canvas.getBoundingClientRect(), pixel = new Uint8Array(4);
      const px = Math.max(0, Math.min(canvas.width - 1, Math.floor((x - rect.left) / rect.width * canvas.width)));
      const py = Math.max(0, Math.min(canvas.height - 1, canvas.height - 1 - Math.floor((y - rect.top) / rect.height * canvas.height)));
      gl.readPixels(px, py, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel); gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      const id = pixel[0] + (pixel[1] << 8) + (pixel[2] << 16);
      const hit=geometry.find(item => item.pickId === id)?.mesh.id ?? null; if(select)onSelect(hit);else {hovered=hit;onHover(hit);} invalidate();
    }
    const listen = (targetNode, name, handler, options) => { targetNode.addEventListener(name, handler, options); listeners.push(() => targetNode.removeEventListener(name, handler, options)); };
    let gesture=null,dragDistance=0,multi=false;
    const cameraChanged=()=>{
      const preset=Object.values(graphics.cameraViews).find(item=>Math.abs(Math.atan2(Math.sin(yaw-item.angles[0]),Math.cos(yaw-item.angles[0])))<.002 && Math.abs(pitch-item.angles[1])<.002);
      onCamera({yaw,pitch,distance,target:[...target],label:preset?.label || 'Volný pohled'});invalidate();
    };
    const zoom=factor=>{automaticFit=false;cameraTransition=null;distance=graphics.boundDistance(distance*factor,scale);cameraChanged();};
    function pan(dx,dy) {target=graphics.panCamera(target,distance,yaw,pitch,canvas.clientHeight,dx,dy,modelBounds,scale);}
    function gestureSnapshot() {
      const points=[...pointers.values()];if(!points.length)return null;
      if(points.length===1)return {x:points[0].x,y:points[0].y,span:0,pan:points[0].button===2};
      const [a,b]=points;return {x:(a.x+b.x)/2,y:(a.y+b.y)/2,span:Math.hypot(a.x-b.x,a.y-b.y),pan:true};
    }
    listen(canvas,'pointerdown',event=>{
      event.preventDefault();shiftDown=!!event.shiftKey;
      if((state.inspect || shiftDown) && event.pointerType!=='touch' && event.button!==2){pick(event.clientX,event.clientY);return;}
      cameraTransition=null;pointers.set(event.pointerId,{x:event.clientX,y:event.clientY,button:event.button || 0});
      multi=multi || pointers.size>1;gesture=gestureSnapshot();dragDistance=0;canvas.setPointerCapture(event.pointerId);
    });
    listen(canvas,'pointermove',event=>{
      shiftDown=!!event.shiftKey;
      const old=pointers.get(event.pointerId);
      if(!old) {if(state.inspect || shiftDown)pick(event.clientX,event.clientY,false);else if(hovered){hovered=null;onHover(null);invalidate();}return;}
      event.preventDefault();cameraTransition=null;automaticFit=false;
      pointers.set(event.pointerId,{...old,x:event.clientX,y:event.clientY});const next=gestureSnapshot(),dx=next.x-gesture.x,dy=next.y-gesture.y;
      dragDistance+=Math.hypot(dx,dy);
      if(next.pan){pan(dx,dy);if(next.span>2 && gesture.span>2)distance=graphics.boundDistance(distance*gesture.span/next.span,scale);}
      else {yaw-=dx*.006;pitch=Math.max(.01,Math.min(Math.PI/2-.001,pitch+dy*.006));}
      gesture=next;cameraChanged();
    });
    function endPointer(event,allowPick=false) {
      if(!pointers.has(event.pointerId))return;
      if(allowPick && dragDistance<4 && !multi && event.button!==2)pick(event.clientX,event.clientY);
      pointers.delete(event.pointerId);gesture=gestureSnapshot();if(!gesture)multi=false;
      if(canvas.hasPointerCapture(event.pointerId))canvas.releasePointerCapture(event.pointerId);invalidate();
    }
    listen(canvas,'pointerup',event=>endPointer(event,true));listen(canvas,'pointercancel',endPointer);listen(canvas,'lostpointercapture',endPointer);
    listen(canvas,'pointerleave',()=>{if(!pointers.size){hovered=null;onHover(null);shiftDown=false;invalidate();}});
    listen(canvas,'wheel',event=>{event.preventDefault();zoom(Math.exp(Math.max(-200,Math.min(200,event.deltaY))*.001));},{passive:false});
    listen(canvas, 'contextmenu', event => event.preventDefault());
    listen(canvas, 'webglcontextlost', event => { event.preventDefault(); onError('Grafický kontext byl přerušen. Načtěte model znovu.'); dispose(); });
    observer = new ResizeObserver(invalidate); observer.observe(canvas);
    const ownerDocument=canvas.ownerDocument || globalThis.document;
    if(ownerDocument?.documentElement && typeof MutationObserver!=='undefined') {
      themeObserver=new MutationObserver(invalidate);
      themeObserver.observe(ownerDocument.documentElement,{attributes:true,attributeFilter:['data-theme']});
    }
    const colorPreference=globalThis.matchMedia?.('(prefers-color-scheme: light)');
    if(colorPreference?.addEventListener)listen(colorPreference,'change',invalidate);
    const motionPreference=globalThis.matchMedia?.('(prefers-reduced-motion: reduce)');
    if(motionPreference?.addEventListener)listen(motionPreference,'change',()=>{
      if(motionPreference.matches && cameraTransition){target=cameraTransition.target;distance=cameraTransition.distance;cameraTransition=null;}
      invalidate();
    });
    function fit() {automaticFit=true;cameraTransition=null;cameraChanged();}
    function focus(ids) {
      const visible=model.meshes.filter(mesh=>ids.includes(mesh.id) && partVisible(mesh,state,model.assemblyGuide));
      const destination=graphics.focusMeshes(visible,model,state,canvas.clientWidth,canvas.clientHeight);if(!destination)return;
      automaticFit=false;destination.distance=graphics.boundDistance(destination.distance,scale);
      if(globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches){target=destination.target;distance=destination.distance;cameraTransition=null;}
      else cameraTransition={...destination,from:[...target],fromDistance:distance,start:performance.now()};cameraChanged();
    }
    fit();
    return {
      update(next){state=next;invalidate();},fit,focus,
      camera(preset){cameraTransition=null;[yaw,pitch]=(graphics.cameraViews[preset==='iso'?'overview':preset] || graphics.cameraViews.overview).angles;automaticFit=true;cameraChanged();},
      orbit(dx,dy){automaticFit=false;cameraTransition=null;yaw+=dx;pitch=Math.max(.01,Math.min(Math.PI/2-.001,pitch+dy));cameraChanged();},
      pan(dx,dy){automaticFit=false;cameraTransition=null;pan(dx*canvas.clientHeight,dy*canvas.clientHeight);cameraChanged();},zoom,dispose,
      state:()=>({yaw,pitch,distance,target:[...target],automaticFit,cameraTransition:!!cameraTransition}),
    };
  } catch (error) { dispose(); throw error; }
}
