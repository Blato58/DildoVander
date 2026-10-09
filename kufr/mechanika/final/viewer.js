/* Embedded by preview.py: the exported viewer needs no server or network. */
const el = id => document.getElementById(id);
const canvas = el('view');
const gl = canvas.getContext('webgl', {antialias: true});
if (!gl) throw new Error('WebGL není dostupné');

function shader(type, source) {
  const result = gl.createShader(type);
  gl.shaderSource(result, source);
  gl.compileShader(result);
  if (!gl.getShaderParameter(result, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(result));
  return result;
}
const program = gl.createProgram();
gl.attachShader(program, shader(gl.VERTEX_SHADER, `
  attribute vec3 p,n; uniform mat4 projection,view,move; varying vec3 normal;
  void main(){gl_Position=projection*view*move*vec4(p,1.);normal=mat3(move)*n;}`));
gl.attachShader(program, shader(gl.FRAGMENT_SHADER, `
  precision mediump float; uniform vec3 color; uniform bool picking; uniform float highlight; varying vec3 normal;
  void main(){if(picking){gl_FragColor=vec4(color,1.);return;}
    float l=.48+.42*max(0.,dot(normalize(normal),normalize(vec3(.4,-.7,1.))))+.1*abs(normalize(normal).z);
    gl_FragColor=vec4(mix(color*l,vec3(1.,.69,.12),highlight),1.);}`));
gl.linkProgram(program);
if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
gl.useProgram(program);
const loc = {};
for (const name of ['projection', 'view', 'move', 'color', 'picking', 'highlight']) loc[name] = gl.getUniformLocation(program, name);
const pa = gl.getAttribLocation(program, 'p'), na = gl.getAttribLocation(program, 'n');
gl.enableVertexAttribArray(pa);
gl.enableVertexAttribArray(na);

const colorKey = 'dildovander.f1.object-colors.v1';
let savedColors = {}, storageMessage = 'Barvy se ukládají v tomto prohlížeči i po jeho zavření.';
function validColor(value) { return typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value); }
function colorVector(value) { return [1, 3, 5].map(i => parseInt(value.slice(i, i + 2), 16) / 255); }
function colorHex(value) { return '#' + value.map(v => Math.round(v * 255).toString(16).padStart(2, '0')).join(''); }
try {
  const parsed = JSON.parse(localStorage.getItem(colorKey) || '{}');
  if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
    for (const mesh of model.meshes) if (validColor(parsed[mesh.name])) savedColors[mesh.name] = parsed[mesh.name];
  }
} catch {
  storageMessage = 'Uložené barvy nelze načíst. Prohlížeč může mít místní úložiště zakázané.';
}
function saveColors() {
  try {
    localStorage.setItem(colorKey, JSON.stringify(savedColors));
    storageMessage = 'Barva uložena v tomto prohlížeči; zůstane i po jeho zavření.';
  } catch {
    storageMessage = 'Barva platí jen do zavření: prohlížeč nepovolil uložení.';
  }
  el('storage-status').textContent = storageMessage;
}

const meshesByName = new Map();
for (const [index, mesh] of model.meshes.entries()) {
  mesh.pickId = index + 1;
  mesh.originalColor = [...mesh.color];
  if (savedColors[mesh.name]) mesh.color = colorVector(savedColors[mesh.name]);
  mesh.center = [0, 1, 2].map(i => (mesh.bounds[i] + mesh.bounds[i + 3]) / 2);
  mesh.buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, mesh.buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(mesh.data), gl.STATIC_DRAW);
  mesh.count = mesh.data.length / 6;
  delete mesh.data;
  meshesByName.set(mesh.name, mesh);
}

const identity = () => [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
function translate(z) { const m = identity(); m[14] = z; return m; }
function rotX(a, c) {
  const s = Math.sin(a), d = Math.cos(a);
  return [1,0,0,0, 0,d,s,0, 0,-s,d,0, 0,c[1]*(1-d)+c[2]*s,c[2]*(1-d)-c[1]*s,1];
}
function transform(m, v) { return [0,1,2,3].map(i => m[i]*v[0]+m[i+4]*v[1]+m[i+8]*v[2]+m[i+12]*v[3]); }
function subtract(a,b) { return a.map((x,i) => x-b[i]); }
function dot(a,b) { return a.reduce((v,x,i) => v+x*b[i],0); }
function cross(a,b) { return [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]]; }
function norm(v) { const length = Math.hypot(...v); return v.map(x => x/length); }
function look(eye,target) {
  const z = norm(subtract(eye,target)), x = norm(cross([0,0,1],z)), y = cross(z,x);
  return [x[0],y[0],z[0],0,x[1],y[1],z[1],0,x[2],y[2],z[2],0,-dot(x,eye),-dot(y,eye),-dot(z,eye),1];
}
function perspective(aspect) {
  const f = 1/Math.tan(.6/2), near = 1, far = 4000;
  return [f/aspect,0,0,0,0,f,0,0,0,0,(far+near)/(near-far),-1,0,0,2*far*near/(near-far),0];
}
function lift(a) {
  const d = model.mechanism.dwell_degrees, t = model.mechanism.travel_degrees;
  if (a <= d) return 0;
  if (a >= t-d) return model.mechanism.lift;
  const x = (a-d)/(t-2*d);
  return model.mechanism.lift*(10*x**3-15*x**4+6*x**5);
}
let yaw = -1.05, pitch = .66, distance = 1300, drag = null, showWands = true;
let dragDistance = 0;
let shiftDown = false, inspectEnabled = false, hovered = null, pinned = null, pointer = null;
let projection, view, frameRequested = false;
function inspecting() { return shiftDown || inspectEnabled; }
function visible(mesh) {
  if (['cover', 'fabric'].includes(mesh.group) && !el('cover').checked) return false;
  if (mesh.group === 'case' && !el('case').checked) return false;
  if ((mesh.name.startsWith('tray_') || mesh.group === 'wand') && !el('beds').checked) return false;
  if (['hardware', 'moving_hw'].includes(mesh.group) && !el('hardware').checked) return false;
  return !((mesh.group === 'wand' || mesh.name === 'powerbank') && !showWands);
}
function movement(mesh) {
  if (mesh.group.startsWith('lid')) return rotX(-el('lid').value*Math.PI/180, model.hinge);
  if (['tray_3','u059_3','led_3','wand_3','roller_3'].includes(mesh.name) ||
      (mesh.name.startsWith('follower_') && mesh.name.endsWith('_3'))) return translate(lift(+el('lift').value));
  if (['cam_3','stock_horn_3'].includes(mesh.name) ||
      (mesh.name.startsWith('coupler_') && mesh.name.endsWith('_3'))) {
    return rotX(el('lift').value*Math.PI/180, [0, model.mechanism.origin_y+model.mechanism.cam_y, model.mechanism.origin_z+model.mechanism.cam_z]);
  }
  return identity();
}
function project(mesh) {
  const point = transform(projection, transform(view, transform(movement(mesh), [...mesh.center, 1])));
  if (point[3] <= 0 || Math.abs(point[2]/point[3]) > 1) return null;
  return [(point[0]/point[3]+1)*canvas.clientWidth/2, (1-point[1]/point[3])*canvas.clientHeight/2];
}
function renderMeshes(picking, selected) {
  gl.uniform1i(loc.picking, picking ? 1 : 0);
  for (const mesh of model.meshes) {
    if (!visible(mesh)) continue;
    gl.uniformMatrix4fv(loc.move, false, movement(mesh));
    const id = mesh.pickId;
    gl.uniform3fv(loc.color, picking ? [(id & 255)/255, ((id >> 8) & 255)/255, ((id >> 16) & 255)/255] : mesh.color);
    gl.uniform1f(loc.highlight, !picking && selected === mesh ? .65 : 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, mesh.buffer);
    gl.vertexAttribPointer(pa, 3, gl.FLOAT, false, 24, 0);
    gl.vertexAttribPointer(na, 3, gl.FLOAT, false, 24, 12);
    gl.drawArrays(gl.TRIANGLES, 0, mesh.count);
  }
}

// A depth-tested ID pass hits the actual visible triangles, including animated parts.
const pickFramebuffer = gl.createFramebuffer(), pickTexture = gl.createTexture(), pickDepth = gl.createRenderbuffer();
let pickWidth = 0, pickHeight = 0;
function pick() {
  if (!pointer) return null;
  const rect = canvas.getBoundingClientRect();
  const x = Math.floor((pointer[0]-rect.left)*canvas.width/rect.width);
  const y = canvas.height-1-Math.floor((pointer[1]-rect.top)*canvas.height/rect.height);
  if (x < 0 || y < 0 || x >= canvas.width || y >= canvas.height) return null;
  gl.bindFramebuffer(gl.FRAMEBUFFER, pickFramebuffer);
  if (pickWidth !== canvas.width || pickHeight !== canvas.height) {
    pickWidth = canvas.width; pickHeight = canvas.height;
    gl.bindTexture(gl.TEXTURE_2D, pickTexture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, pickWidth, pickHeight, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, pickTexture, 0);
    gl.bindRenderbuffer(gl.RENDERBUFFER, pickDepth);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, pickWidth, pickHeight);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, pickDepth);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('Inspect framebuffer není dostupný');
  }
  gl.disable(gl.DITHER);
  gl.clearColor(0,0,0,1);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  renderMeshes(true, null);
  const pixel = new Uint8Array(4);
  gl.readPixels(x,y,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  gl.enable(gl.DITHER);
  return model.meshes[(pixel[0] | pixel[1] << 8 | pixel[2] << 16)-1] || null;
}

const svgNS = 'http://www.w3.org/2000/svg';
function svgElement(tag, attributes) {
  const node = document.createElementNS(svgNS, tag);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
  el('overlay').append(node);
  return node;
}
function label(name, x, y) {
  const node = document.createElement('span');
  node.className = 'object-name'; node.textContent = name;
  node.style.left = x+'px'; node.style.top = y+'px';
  el('names').append(node);
  return node;
}
function allNames() {
  const width = canvas.clientWidth, height = canvas.clientHeight;
  const panel = el('inspector');
  const exclusion = panel.hidden ? null : [width-panel.offsetWidth-12,12,panel.offsetWidth,panel.offsetHeight];
  const labels = [];
  // Append first, then measure together to avoid forcing layout once per object.
  for (const mesh of model.meshes.filter(visible)) {
    const point = project(mesh);
    if (!point || point[0] < 0 || point[0] > width || point[1] < 0 || point[1] > height) continue;
    labels.push({node:label(mesh.name,0,0),point});
  }
  for (const item of labels) { item.w = item.node.offsetWidth; item.h = item.node.offsetHeight; }
  const cellWidth = 16, rowHeight = Math.max(0,...labels.map(item=>item.h))+3;
  const columns = Math.max(0,Math.floor((width-8)/cellWidth));
  const rows = Math.max(0,Math.floor((height-45)/rowHeight));
  const occupied = Array.from({length:rows},()=>new Uint8Array(columns));
  if (exclusion) {
    for (let row = 0; row < rows; row++) {
      for (let column = 0; column < columns; column++) {
        const x = 8+column*cellWidth, y = 45+row*rowHeight;
        if (x < exclusion[0]+exclusion[2]+3 && x+cellWidth+3 > exclusion[0] &&
            y < exclusion[1]+exclusion[3]+3 && y+rowHeight+3 > exclusion[1]) occupied[row][column] = 1;
      }
    }
  }
  // Search free row segments, rather than every slot against every previous label.
  const placements = [];
  for (const {node,point,w,h} of labels) {
    const required = Math.ceil((w+3)/cellWidth);
    const desired = Math.round((point[0]-w/2-8)/cellWidth);
    let best = null, score = Infinity;
    for (let row = 0; row < rows; row++) {
      let start = 0;
      for (let column = 0; column <= columns; column++) {
        if (column < columns && !occupied[row][column]) continue;
        if (column-start >= required) {
          const candidate = Math.max(start,Math.min(column-required,desired));
          const x = 8+candidate*cellWidth, y = 45+row*rowHeight;
          const d = (x+w/2-point[0])**2+(y+h/2-point[1])**2;
          if (d < score) { best = {x,y,row,column:candidate}; score = d; }
        }
        start = column+1;
      }
    }
    // Very small viewports can be dense; every in-frame name is still shown.
    const {x,y} = best || {x:Math.max(0,Math.min(width-w,point[0])),y:Math.max(45,Math.min(height-h,point[1]))};
    if (best) occupied[best.row].fill(1,best.column,best.column+required);
    placements.push({node,point,w,h,x,y});
  }
  for (const {node,point,w,h,x,y} of placements) {
    node.style.left = x+'px'; node.style.top = y+'px';
    svgElement('line', {x1:point[0],y1:point[1],x2:x+w/2,y2:y+h/2,stroke:'#776c5b','stroke-opacity':'.35'});
  }
}
function connectionsFor(mesh) { return model.connections.filter(edge => edge.from === mesh.name || edge.to === mesh.name); }
function drawConnections(mesh) {
  const start = project(mesh);
  if (!start) return;
  const bundles = new Map();
  for (const edge of connectionsFor(mesh)) {
    const name = edge.from === mesh.name ? edge.to : edge.from;
    if (!bundles.has(name)) bundles.set(name, []);
    bundles.get(name).push(edge);
  }
  for (const [name, edges] of bundles) {
    const other = meshesByName.get(name);
    const end = project(other);
    if (!end) continue;
    const dx = end[0]-start[0], dy = end[1]-start[1], length = Math.hypot(dx,dy);
    const perpendicular = length > 1 ? [-dy/length,dx/length] : [1,0];
    // Fan parallel nets apart so GND cannot paint over power, I2C or OE.
    for (const [index, edge] of edges.entries()) {
      const offset = (index-(edges.length-1)/2)*24;
      const angle = index*2*Math.PI/edges.length;
      const displacement = length > 1 ? perpendicular.map(v => v*offset) : [32*Math.cos(angle),32*Math.sin(angle)];
      const control = [(start[0]+end[0])/2+displacement[0], (start[1]+end[1])/2+displacement[1]];
      const color = {power:'#c02b25',signal:'#256fb2',ground:'#655d55'}[edge.kind];
      svgElement('path', {
        d:`M ${start[0]} ${start[1]} Q ${control[0]} ${control[1]} ${end[0]} ${end[1]}`,
        fill:'none',stroke:color,'stroke-width':2,'stroke-dasharray':visible(other)?'':'6 4',
        'data-from':edge.from,'data-to':edge.to,'data-kind':edge.kind,
      });
    }
    svgElement('circle', {cx:end[0],cy:end[1],r:4,fill:'#655d55'});
    label(other.name+(visible(other)?'':' (skryto)'), Math.max(0,Math.min(canvas.clientWidth-170,end[0]+6)), Math.max(0,Math.min(canvas.clientHeight-24,end[1])));
  }
}
let displayedMesh = undefined;
function updatePanel(mesh) {
  el('inspector').hidden = !mesh && !inspectEnabled;
  el('object-details').hidden = !mesh;
  el('unpin').hidden = !pinned;
  if (displayedMesh === mesh) return;
  displayedMesh = mesh;
  el('object').value = mesh?.name || '';
  if (!mesh) return;
  el('object-title').textContent = mesh.info.title;
  el('mesh-id').textContent = mesh.name;
  el('description').textContent = mesh.info.description;
  el('details').replaceChildren();
  const size = [0,1,2].map(i => (mesh.bounds[i+3]-mesh.bounds[i]).toFixed(1)).join(' × ');
  for (const detail of [...mesh.info.details, `Obálka v CAD (osy X × Y × Z): ${size} mm.`, `Zdroj modelu: ${mesh.info.source}`]) {
    const li = document.createElement('li'); li.textContent = detail; el('details').append(li);
  }
  el('color').value = colorHex(mesh.color);
  el('storage-status').textContent = storageMessage;
  el('connections').replaceChildren();
  const edges = connectionsFor(mesh);
  el('empty-connections').hidden = edges.length > 0;
  el('empty-connections').textContent = mesh.name.startsWith('spare_') ? 'Rezervní díl není zapojený.' : 'Pro tento díl není v podkladech přiřazeno kabelové spojení.';
  for (const edge of edges) {
    const other = meshesByName.get(edge.from === mesh.name ? edge.to : edge.from);
    const li = document.createElement('li');
    li.textContent = `${meshesByName.get(edge.from).info.title} → ${meshesByName.get(edge.to).info.title}: ${edge.label}`;
    if (edge.note) { const note = document.createElement('small'); note.textContent = edge.note; li.append(note); }
    if (!visible(other)) { const note = document.createElement('small'); note.textContent = 'Druhý objekt je nyní skrytý.'; li.append(note); }
    el('connections').append(li);
  }
  el('evidence').textContent = model.sources.map(source => `${source.name} (SHA-256 ${source.sha256})`).join('\n');
}
function updateObjectOptions() {
  const select = el('object'), current = select.value;
  select.replaceChildren(new Option('Vyberte objekt…', ''));
  for (const mesh of model.meshes.filter(visible)) select.add(new Option(`${mesh.info.title} · ${mesh.name}`, mesh.name));
  select.value = current;
}
let overlayLayoutKey = '';
function updateOverlay(selected) {
  const names = inspecting() && !selected;
  const mode = names ? 'names' : selected ? 'connections' : 'none';
  const panel = el('inspector');
  // Picking remains live; stable empty-space movement reuses the existing DOM layout.
  const key = JSON.stringify([
    mode,mode === 'connections' ? selected.name : '',canvas.clientWidth,canvas.clientHeight,
    yaw,pitch,distance,el('lid').value,el('lift').value,
    ...['cover','case','beds','hardware'].map(id=>el(id).checked),showWands,
    panel.hidden ? 0 : panel.offsetWidth,panel.hidden ? 0 : panel.offsetHeight,
  ]);
  if (key === overlayLayoutKey) return;
  overlayLayoutKey = key;
  el('overlay').replaceChildren(); el('names').replaceChildren();
  if (names) allNames();
  else if (selected) drawConnections(selected);
}
function draw() {
  const ratio = devicePixelRatio || 1;
  const w = Math.max(1,Math.round(canvas.clientWidth*ratio)), h = Math.max(1,Math.round(canvas.clientHeight*ratio));
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
  gl.viewport(0,0,w,h); gl.enable(gl.DEPTH_TEST); gl.disable(gl.CULL_FACE); gl.disable(gl.BLEND);
  const target = [220,165,155], fitDistance = distance*Math.max(1,.9/(w/h));
  const eye = [target[0]+fitDistance*Math.cos(pitch)*Math.cos(yaw),target[1]+fitDistance*Math.cos(pitch)*Math.sin(yaw),target[2]+fitDistance*Math.sin(pitch)];
  projection = perspective(w/h); view = look(eye,target);
  gl.uniformMatrix4fv(loc.projection,false,projection); gl.uniformMatrix4fv(loc.view,false,view);
  if (pinned && !visible(pinned)) pinned = null;
  hovered = inspecting() && !drag ? pick() : null;
  const selected = pinned || hovered;
  gl.clearColor(.937,.922,.894,1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  renderMeshes(false, hovered || selected);
  el('liftvalue').textContent = lift(+el('lift').value).toFixed(1)+' mm';
  el('lidvalue').textContent = el('lid').value+'°';
  el('inspect').setAttribute('aria-pressed', String(inspectEnabled));
  el('inspect-hint').hidden = !inspecting();
  el('inspect-hint').textContent = pinned ? `Připnuto: ${pinned.name}. Klik na jiný objekt změní výběr; klik do prázdna nebo Zrušit výběr jej zruší.` : selected ? 'Klik na objekt připne jeho kartu a umožní změnit barvu.' : 'Inspect: názvy všech objektů zapnutých vrstev. Najeďte na díl; Servis odkryje elektroniku.';
  canvas.style.cursor = inspecting() ? 'crosshair' : drag ? 'grabbing' : 'grab';
  updatePanel(selected);
  updateOverlay(selected);
}
function requestDraw() {
  if (frameRequested) return;
  frameRequested = true;
  requestAnimationFrame(() => { frameRequested = false; draw(); });
}
function layersChanged() { displayedMesh = undefined; updateObjectOptions(); requestDraw(); }
canvas.addEventListener('pointerdown', event => {
  pointer = [event.clientX,event.clientY];
  if (inspecting()) {
    draw(); pinned = hovered; requestDraw();
    return;
  }
  drag = [event.clientX,event.clientY]; dragDistance = 0; canvas.setPointerCapture(event.pointerId);
});
function endDrag() { drag = null; requestDraw(); }
canvas.addEventListener('pointerup', event => {
  if (drag && dragDistance < 4 && pinned) {
    pointer = [event.clientX,event.clientY];
    draw();
    if (!pick()) pinned = null;
  }
  endDrag();
});
canvas.addEventListener('pointercancel', endDrag);
canvas.addEventListener('lostpointercapture', endDrag);
canvas.addEventListener('pointermove', event => {
  pointer = [event.clientX,event.clientY];
  // Recover a missed key-up after a native color dialog or switching windows.
  shiftDown = event.shiftKey;
  if (drag) {
    dragDistance += Math.hypot(event.clientX-drag[0],event.clientY-drag[1]);
    yaw -= (event.clientX-drag[0])*.006;
    pitch = Math.max(.07,Math.min(1.5,pitch+(event.clientY-drag[1])*.006));
    drag = [event.clientX,event.clientY];
  }
  requestDraw();
});
canvas.addEventListener('pointerleave', () => { pointer = null; requestDraw(); });
canvas.addEventListener('wheel', event => {
  event.preventDefault(); distance = Math.max(250,Math.min(2000,distance*Math.exp(event.deltaY*.001))); requestDraw();
}, {passive:false});
el('inspector').addEventListener('pointerenter', () => { if (!pinned && displayedMesh) pinned = displayedMesh; pointer = null; requestDraw(); });
function closeInspect() { shiftDown = false; inspectEnabled = false; pinned = null; hovered = null; requestDraw(); }
document.addEventListener('keydown', event => {
  if (event.key === 'Shift' && !shiftDown) { shiftDown = true; requestDraw(); }
  if (event.key === 'Escape') closeInspect();
});
document.addEventListener('keyup', event => {
  if (event.key === 'Shift') { shiftDown = false; inspectEnabled = false; requestDraw(); }
});
window.addEventListener('blur', () => { shiftDown = false; drag = null; pointer = null; requestDraw(); });
el('inspect').onclick = () => { inspectEnabled = !inspectEnabled; if (!inspectEnabled) pinned = null; requestDraw(); };
el('close-inspect').onclick = closeInspect;
el('unpin').onclick = () => { pinned = null; pointer = null; requestDraw(); };
el('object').onchange = () => { pinned = meshesByName.get(el('object').value) || null; pointer = null; requestDraw(); };
el('color').oninput = () => {
  if (!displayedMesh || !validColor(el('color').value)) return;
  pinned = displayedMesh;
  savedColors[pinned.name] = el('color').value; pinned.color = colorVector(el('color').value);
  saveColors(); requestDraw();
};
el('reset-color').onclick = () => {
  if (!displayedMesh) return;
  pinned = displayedMesh; delete savedColors[pinned.name]; pinned.color = [...pinned.originalColor];
  el('color').value = colorHex(pinned.color); saveColors(); requestDraw();
};
for (const id of ['lid','lift']) el(id).oninput = requestDraw;
for (const id of ['cover','case','beds','hardware']) el(id).oninput = layersChanged;
el('open').onclick = () => {
  el('lid').value = 105; el('lift').value = 160; el('cover').checked = true; el('case').checked = true; el('beds').checked = true;
  showWands = true; layersChanged();
};
el('service').onclick = () => {
  el('cover').checked = false; el('beds').checked = false; el('case').checked = false; el('lid').value = 105; el('lift').value = 0;
  showWands = false; layersChanged();
};
el('closed').onclick = () => {
  el('lid').value = 0; el('lift').value = 0; el('cover').checked = true; el('case').checked = true; el('beds').checked = true;
  showWands = false; layersChanged();
};
window.addEventListener('resize', requestDraw);
updateObjectOptions(); draw();
window.F1 = {draw, meshes:model.meshes.length};
