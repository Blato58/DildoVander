/* Embedded by preview.py: the exported viewer needs no server or network. */
const el = id => document.getElementById(id);
let twinViewer = null;
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
  precision mediump float; uniform vec3 color; uniform bool picking; uniform float highlight,opacity; varying vec3 normal;
  void main(){if(picking){gl_FragColor=vec4(color,1.);return;}
    float l=.48+.42*max(0.,dot(normalize(normal),normalize(vec3(.4,-.7,1.))))+.1*abs(normalize(normal).z);
    gl_FragColor=vec4(mix(color*l,vec3(1.,.69,.12),highlight),opacity);}`));
gl.linkProgram(program);
if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
gl.useProgram(program);
const loc = {};
for (const name of ['projection', 'view', 'move', 'color', 'picking', 'highlight', 'opacity']) loc[name] = gl.getUniformLocation(program, name);
const pa = gl.getAttribLocation(program, 'p'), na = gl.getAttribLocation(program, 'n');
gl.enableVertexAttribArray(pa);
gl.enableVertexAttribArray(na);

const colorKey = 'dildovander.f2.object-colors.v1';
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

// The exported payload shares positions; the GPU still gets one flat normal per facet.
function uploadGeometry(mesh) {
  if (mesh.vertices === undefined && mesh.triangles === undefined) return new Float32Array(mesh.data);
  const vertices = mesh.vertices, triangles = mesh.triangles;
  if (!Array.isArray(vertices) || !Array.isArray(triangles) || vertices.length % 3 || triangles.length % 3 || !vertices.every(Number.isFinite)) {
    throw new Error('Neplatná indexovaná geometrie: '+mesh.name);
  }
  const vertexCount = vertices.length/3;
  if (!triangles.every(index=>Number.isInteger(index) && index >= 0 && index < vertexCount)) {
    throw new Error('Neplatný index trojúhelníku: '+mesh.name);
  }
  const packed = new Float32Array(triangles.length*6);
  let written = 0;
  for (let face=0; face<triangles.length; face+=3) {
    const a = triangles[face]*3, b = triangles[face+1]*3, c = triangles[face+2]*3;
    const ux = vertices[b]-vertices[a], uy = vertices[b+1]-vertices[a+1], uz = vertices[b+2]-vertices[a+2];
    const vx = vertices[c]-vertices[a], vy = vertices[c+1]-vertices[a+1], vz = vertices[c+2]-vertices[a+2];
    const nx = uy*vz-uz*vy, ny = uz*vx-ux*vz, nz = ux*vy-uy*vx;
    const length = Math.hypot(nx,ny,nz);
    // Match the generator's facet rejection; repeated/collinear points never make NaN normals.
    if (!Number.isFinite(length) || length < 1e-12) continue;
    for (const position of [a,b,c]) {
      packed[written++] = vertices[position]; packed[written++] = vertices[position+1]; packed[written++] = vertices[position+2];
      packed[written++] = nx/length; packed[written++] = ny/length; packed[written++] = nz/length;
    }
  }
  return packed.subarray(0,written);
}

const meshesByName = new Map();
const treeButtons = new Map();
for (const [index, mesh] of model.meshes.entries()) {
  mesh.pickId = index + 1;
  mesh.originalColor = [...mesh.color];
  if (savedColors[mesh.name]) mesh.color = colorVector(savedColors[mesh.name]);
  mesh.center = [0, 1, 2].map(i => (mesh.bounds[i] + mesh.bounds[i + 3]) / 2);
  mesh.buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, mesh.buffer);
  const geometry = uploadGeometry(mesh);
  gl.bufferData(gl.ARRAY_BUFFER, geometry, gl.STATIC_DRAW);
  mesh.count = geometry.length / 6;
  delete mesh.data; delete mesh.vertices; delete mesh.triangles;
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
  const f = 1/Math.tan(.6/2), near = Math.max(.5,distance*.001), far = Math.max(4000,distance+modelScale*4);
  return [f/aspect,0,0,0,0,f,0,0,0,0,(far+near)/(near-far),-1,0,0,2*far*near/(near-far),0];
}
function lift(a) {
  const d = model.mechanism.dwell_degrees, t = model.mechanism.travel_degrees;
  if (a <= d) return 0;
  if (a >= t-d) return model.mechanism.lift;
  const x = (a-d)/(t-2*d);
  return model.mechanism.lift*(10*x**3-15*x**4+6*x**5);
}
const trayAngles = Array(model.mechanism.count || 6).fill(0);
const routes = model.routes || [];
const routesById = new Map(routes.map(route => [route.id,route]));
const hiddenMeshes = new Set(), hiddenGroups = new Set();
const assemblyGuide = model.assemblyGuide || {steps:[]};
let mode = 'operation', assemblyIndex = -1, isolateName = null;
let yaw = -1.05, pitch = .66, distance = 1050, drag = null;
let automaticFit = true, visualMode = 'complete';
let cameraTransition = null;
const activePointers = new Map();
let gesture = null, gestureWasMulti = false;
const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)');
const modelBounds = [0,1,2].map(axis => [Math.min(...model.meshes.map(mesh=>mesh.bounds[axis])),Math.max(...model.meshes.map(mesh=>mesh.bounds[axis+3]))]);
const modelScale = Math.max(100,...modelBounds.map(([low,high])=>high-low));
const modelPivot = modelBounds.map(([low,high])=>(low+high)/2);
function boundDistance(value) { return Math.max(25,Math.min(modelScale*16,value)); }
function boundTarget(target) { return target.map((value,axis)=>Math.max(modelBounds[axis][0]-modelScale*.6,Math.min(modelBounds[axis][1]+modelScale*.6,value))); }
function currentStep() { return assemblyGuide.steps[assemblyIndex]; }
function assemblyPartState(mesh) {
  if ((currentStep()?.preparedParts || []).some(part=>matchesPart(mesh.name,part))) return 'prepared';
  if (newlyInstalled(mesh)) return 'current';
  if (isBenchStep() && benchPreviewPatterns().some(part=>matchesPart(mesh.name,part))) return 'prepared';
  if (installedParts().some(part=>matchesPart(mesh.name,part))) return 'installed';
  const next = assemblyGuide.steps[assemblyIndex+1];
  if ([...(next?.parts || []),...(next?.preparedParts || [])].some(part=>matchesPart(mesh.name,part))) return 'next';
  return 'future';
}
function isBenchStep() { return mode === 'assembly' && currentStep()?.installation?.kind === 'bench'; }
function currentPartPatterns(step = currentStep()) { return [...(step?.parts || []),...(step?.preparedParts || [])]; }
function casePartPatterns(step) { return step?.caseParts ?? step?.parts ?? []; }
function benchPreviewPatterns() {
  const step = currentStep(), targets = step?.installation?.targets || [];
  if (Array.isArray(step?.benchParts)) return step.benchParts;
  const active = [...currentPartPatterns(step),...targets];
  // Wiring-only bench work adds no geometry; retain the preassembled deck for context.
  return active.length ? active : installedParts();
}

let cameraTarget = [(model.case?.inside_x || 430)/2,(model.case?.inside_y || 269)/2,65];
let dragDistance = 0;
let shiftDown = false, inspectEnabled = false, hovered = null, pinned = null, pointer = null;
let projection, view, frameRequested = false;
let selectedWire = '', selectedEndpoint = '';
let hoveredConnection = null;
let effect = null, effectFault = false;
const lightStates = Array(trayAngles.length).fill(null);
const twinOutputStates = new Map();
function inspecting() { return shiftDown || inspectEnabled; }
function matchesPart(name, part) {
  return name === part || name.startsWith(part.endsWith('*') ? part.slice(0,-1) : part.endsWith('_') ? part : part + '_');
}
function installedParts() { return assemblyGuide.steps.slice(0,assemblyIndex+1).flatMap(casePartPatterns); }
function newlyInstalled(mesh) {
  const patterns = isBenchStep() ? currentPartPatterns() : casePartPatterns(currentStep());
  return mode === 'assembly' && patterns.some(part => matchesPart(mesh.name,part));
}
function visible(mesh) {
  if (isBenchStep() && !benchPreviewPatterns().some(part=>matchesPart(mesh.name,part))) return false;
  if (visualMode === 'structure' && ['hardware','moving_hw','wand','textile','fabric','lid_fabric','cover'].includes(mesh.group)) return false;
  if (visualMode === 'electronics' && !['hardware','moving_hw','case','lid','fixed'].includes(mesh.group)) return false;
  if (visualMode === 'cutaway' && ['case','cover'].includes(mesh.group)) return false;
  if (hiddenMeshes.has(mesh.name) || hiddenGroups.has(mesh.group)) return false;
  if (isolateName && mesh.name !== isolateName) return false;
  if (mesh.transportOnly && mode !== 'transport') return false;
  if (mesh.operationOnly && mode === 'transport') return false;
  if ((mesh.group === 'wand' || mesh.name.startsWith('wand_')) && (mode === 'transport' || !el('wands').checked)) return false;
  if (mesh.name === 'powerbank' && (mode === 'transport' || !el('bank').checked)) return false;
  if ((mesh.group.includes('fabric') || mesh.group === 'textile' || mesh.name.startsWith('soft_liner_')) && (mode === 'transport' || !el('textile').checked)) return false;
  if (mesh.group === 'cover' && !el('cover').checked) return false;
  if (mesh.group === 'case' && !el('case').checked) return false;
  if (mesh.name.startsWith('tray_') && !el('beds').checked) return false;
  if (['hardware', 'moving_hw'].includes(mesh.group) && !el('hardware').checked) return false;
  if (isBenchStep()) return true;
  if (mode === 'assembly' && !['case','lid','lid_hardware'].includes(mesh.group)) {
    return installedParts().some(part => matchesPart(mesh.name,part));
  }
  return true;
}
function motionFor(mesh) {
  if (mesh.motion) return mesh.motion;
  const index = +(mesh.name.match(/_([1-6])$/)?.[1] || 0);
  if (!index) return null;
  if (/^(cam_|stock_horn_|coupler_)/.test(mesh.name)) return {type:'cam',index};
  if (/^(tray_|u059_|led_|wand_|soft_liner_|tray_foam_|tray_fabric_)/.test(mesh.name)) return {type:'lift',index};
  return null;
}
function movement(mesh) {
  if (mesh.group.startsWith('lid')) return rotX(-el('lid').value*Math.PI/180, model.hinge);
  const motion = motionFor(mesh);
  const angle = motion ? (twinViewer?.active() ? twinViewer.angle(motion.index) ?? 0 : trayAngles[motion.index-1]) : 0;
  if (motion?.type === 'lift') return translate(lift(angle));
  if (motion?.type === 'cam') {
    return rotX(angle*Math.PI/180, [0, model.mechanism.origin_y+model.mechanism.cam_y, model.mechanism.origin_z+model.mechanism.cam_z]);
  }
  return identity();
}
function project(mesh) {
  return projectPoint(transform(movement(mesh),[...mesh.center,1]).slice(0,3));
}
function projectPoint(position) {
  const point = transform(projection, transform(view, [...position, 1]));
  if (point[3] <= 0 || Math.abs(point[2]/point[3]) > 1) return null;
  return [(point[0]/point[3]+1)*canvas.clientWidth/2, (1-point[1]/point[3])*canvas.clientHeight/2];
}
function visibleCorners() {
  const points = [];
  for (const mesh of model.meshes.filter(visible)) {
    const matrix = movement(mesh);
    for (let corner=0; corner<8; corner++) {
      const point = [0,1,2].map(axis => mesh.bounds[axis+((corner >> axis)&1)*3]);
      points.push(transform(matrix,[...point,1]).slice(0,3));
    }
  }
  return points;
}
function fitScene() {
  const points = visibleCorners();
  if (!points.length) return;
  cameraTarget = [0,1,2].map(axis => (Math.min(...points.map(point=>point[axis]))+Math.max(...points.map(point=>point[axis])))/2);
  const forward = [Math.cos(pitch)*Math.cos(yaw),Math.cos(pitch)*Math.sin(yaw),Math.sin(pitch)];
  const right = norm(cross([0,0,1],forward)), up = cross(forward,right);
  const tangent = Math.tan(.6/2), aspect = Math.max(.05,canvas.clientWidth/Math.max(1,canvas.clientHeight));
  let requiredDistance = 75;
  // Fit every transformed corner, including an open lid, with screen-space margin.
  for (const point of points) {
    const relative = subtract(point,cameraTarget);
    const extent = Math.max(Math.abs(dot(relative,right))/(tangent*aspect),Math.abs(dot(relative,up))/tangent);
    requiredDistance = Math.max(requiredDistance,dot(relative,forward)+extent*1.16);
  }
  distance = requiredDistance;
}
function renderMeshes(picking, selected) {
  gl.uniform1i(loc.picking, picking ? 1 : 0);
  const ordered = [...model.meshes].sort((a,b) => Number(['case','lid'].includes(a.group))-Number(['case','lid'].includes(b.group)));
  for (const mesh of ordered) {
    if (!visible(mesh)) continue;
    gl.uniformMatrix4fv(loc.move, false, movement(mesh));
    const id = mesh.pickId;
    const ledIndex = +(mesh.name.match(/^led_([1-6])$/)?.[1] || 0);
    const effectColor = ledIndex && lightStates[ledIndex-1];
    const reportedOutput = twinViewer?.active() ? twinOutputStates.get(mesh.name) : null;
    const outputColor = reportedOutput === 'cancel' ? [.906,.502,.486] :
      reportedOutput === 'voice-busy' ? [.549,.733,.867] : reportedOutput ? [.851,.718,.467] : null;
    const motion = motionFor(mesh), uncertain = twinViewer?.active() && motion && twinViewer.uncertainty(motion.index);
    const contextOpacity = uncertain ? .28 : visualMode === 'electronics' && !['hardware','moving_hw'].includes(mesh.group) ? .12 : ['case','lid'].includes(mesh.group) ? +el('case-opacity').value : 1;
    const translucent = !picking && contextOpacity < 1;
    gl.uniform1f(loc.opacity,translucent ? contextOpacity : 1);
    if (translucent) { gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA); gl.depthMask(false); }
    else { gl.disable(gl.BLEND); gl.depthMask(true); }
    gl.uniform3fv(loc.color, picking ? [(id & 255)/255, ((id >> 8) & 255)/255, ((id >> 16) & 255)/255] : effectColor || outputColor || mesh.color);
    gl.uniform1f(loc.highlight, !picking && selected === mesh ? .65 : !picking && newlyInstalled(mesh) ? .45 : !picking && reportedOutput ? .35 : 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, mesh.buffer);
    gl.vertexAttribPointer(pa, 3, gl.FLOAT, false, 24, 0);
    gl.vertexAttribPointer(na, 3, gl.FLOAT, false, 24, 12);
    gl.drawArrays(gl.TRIANGLES, 0, mesh.count);
  }
  gl.depthMask(true); gl.disable(gl.BLEND);
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
function label(name, x, y,container='names') {
  const node = document.createElement('span');
  node.className = 'object-name'; node.textContent = name;
  node.style.left = x+'px'; node.style.top = y+'px';
  el(container).append(node);
  return node;
}
function allNames() {
  const width = canvas.clientWidth, height = canvas.clientHeight;
  // The inspector is docked outside the canvas, so it no longer consumes label space.
  const exclusion = null;
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
function componentOf(endpoint) { return model.endpoints?.[endpoint]?.component || (endpoint || '').split(':')[0]; }
function physicalEndpoint(route,side) {
  const bankAbsent = mode === 'transport' || !el('bank').checked;
  return bankAbsent && route['transport_'+side] || route[side];
}
function routePoints(route) {
  const bankAbsent = mode === 'transport' || !el('bank').checked;
  const parked = bankAbsent && (route.transport_points || route.disconnected_points);
  const points = (parked || route.points).map(point => [...point]);
  if (parked) return points;
  if (route.point_lift_weights) {
    for (const [module,weights] of Object.entries(route.point_lift_weights)) {
      const mesh = meshesByName.get('tray_left_'+module);
      const lift = mesh ? movement(mesh)[14] : 0;
      if (weights.length !== points.length) throw new Error('Neplatný model kabelové smyčky');
      for (let i=0;i<points.length;i++) points[i][2] += weights[i]*lift;
    }
    return points;
  }
  // Fixed trunks stay in CAD coordinates. Only declared moving points deform.
  const transforms = route.moving_points || {};
  for (let i = 0; i < points.length; i++) {
    const endpoint = transforms[i] || (i === 0 ? route.from : i === points.length-1 ? route.to : '');
    const mesh = meshesByName.get(componentOf(endpoint));
    if (mesh && motionFor(mesh)) points[i] = transform(movement(mesh),[...points[i],1]).slice(0,3);
  }
  return points;
}
function connectionsFor(mesh) {
  return routes.filter(route => componentOf(route.from) === mesh.name || componentOf(route.to) === mesh.name);
}
function scopedRoutes(mesh) {
  const wire = selectedWire || hoveredConnection?.route, endpoint = selectedEndpoint || (!selectedWire && hoveredConnection?.endpoint);
  let selected = wire ? [routesById.get(wire)].filter(Boolean) : mesh ? connectionsFor(mesh) : [];
  if (endpoint) selected = routes.filter(route => route.from === endpoint || route.to === endpoint || physicalEndpoint(route,'from') === endpoint || physicalEndpoint(route,'to') === endpoint);
  const scope = el('wire-scope').value;
  if (scope === 'net' || scope === 'bundle') {
    const keys = new Set(selected.map(route => route[scope]));
    selected = routes.filter(route => route[scope] && keys.has(route[scope]));
  }
  return selected;
}
function wireColor(route) { return {power:'#b6322b',signal:'#287ab0',ground:'#73685d'}[route.kind] || '#a36e27'; }
function routeIsInstalled(route) {
  return mode !== 'assembly' || assemblyGuide.steps.slice(0,assemblyIndex+1).some(step => (step.wires || []).includes(route.id));
}
function drawRoutes(selected) {
  const selectedIds = new Set(scopedRoutes(selected).map(route => route.id));
  for (const route of routes.filter(route => routeIsInstalled(route) && (el('wires').checked || selectedIds.has(route.id)))) {
    const points = routePoints(route).map(projectPoint);
    if (points.some(point => !point)) continue;
    const active = selectedIds.has(route.id);
    svgElement('polyline', {
      points:points.map(point => point.join(',')).join(' '),fill:'none',
      stroke:wireColor(route),'stroke-width':active ? 3.5 : 1.2,
      'stroke-opacity':active ? 1 : .5,'data-wire-id':route.id,
      'data-from':route.from,'data-to':route.to,'data-kind':route.kind,
      'data-net':route.net || '', 'data-bundle':route.bundle || '',
    });
    if (active) {
      for (const moving of route.moving_endpoints || []) {
        const index = moving.endpoint === route.from ? 0 : points.length-1;
        const neighbour = index === 0 ? 1 : index-1;
        svgElement('line',{x1:points[index][0],y1:points[index][1],x2:points[neighbour][0],y2:points[neighbour][1],
          stroke:'#b79852','stroke-width':4,'stroke-dasharray':'4 3','data-flexible':route.id});
        label(route.id+' · pohyblivý konec, '+(route.point_lift_weights ? 'vratný oblouk R8' : 'aproximace smyčky'),points[neighbour][0]+5,points[neighbour][1]-16);
      }
    }
    if (!active && !inspecting()) continue;
    for (const [index,endpoint] of [[0,physicalEndpoint(route,'from')],[points.length-1,physicalEndpoint(route,'to')]]) {
      const point = points[index];
      svgElement('circle',{cx:point[0],cy:point[1],r:active ? 4 : 2.5,
        fill:wireColor(route),'data-endpoint':endpoint,'data-wire-id':route.id});
      if (active) {
        const disconnected = endpoint !== route.from && index === 0;
        label(endpoint+(disconnected ? ' · odpojeno / parkováno' : ''),point[0]+5,point[1]+5);
      }
    }
  }
}
function wireHit() {
  if (!pointer || !inspecting()) return null;
  const rect = canvas.getBoundingClientRect(), point = [pointer[0]-rect.left,pointer[1]-rect.top];
  let best = null, bestDistance = 7;
  function distanceToSegment(start,end) {
    const dx = end[0]-start[0], dy = end[1]-start[1], length = dx*dx+dy*dy;
    const t = length ? Math.max(0,Math.min(1,((point[0]-start[0])*dx+(point[1]-start[1])*dy)/length)) : 0;
    return Math.hypot(point[0]-start[0]-t*dx,point[1]-start[1]-t*dy);
  }
  const selectedIds = new Set(scopedRoutes(pinned || hovered).map(route=>route.id));
  const projected = routes.filter(route => routeIsInstalled(route) && (el('wires').checked || selectedIds.has(route.id)))
    .map(route=>({route,points:routePoints(route).map(projectPoint)})).filter(item=>item.points.every(Boolean));
  // Endpoint markers take priority over line segments, including crossings.
  for (const {route,points} of projected) {
    for (const [index,endpoint] of [[0,physicalEndpoint(route,'from')],[points.length-1,physicalEndpoint(route,'to')]]) {
      const d = Math.hypot(point[0]-points[index][0],point[1]-points[index][1]);
      if (d < bestDistance) { best = {route:route.id,endpoint}; bestDistance = d; }
    }
  }
  if (best) return best;
  for (const {route,points} of projected) {
    for (let i=1; i<points.length; i++) {
      const d = distanceToSegment(points[i-1],points[i]);
      if (d < bestDistance) { best = {route:route.id,endpoint:''}; bestDistance = d; }
    }
  }
  return best;
}
let displayedMesh = undefined;
function updatePanel(mesh) {
  const wireInspection = !!(selectedWire || selectedEndpoint || hoveredConnection);
  el('inspector').hidden = (!mesh && !inspectEnabled) || (wireInspection && !pinned);
  el('inspector-empty').hidden = !el('inspector').hidden;
  // A hover card can appear under the pointer. Until pinned, canvas picking
  // must still receive the next click; the empty Inspect selector stays usable.
  el('inspector').style.pointerEvents = pinned || !mesh ? 'auto' : 'none';
  el('object-details').hidden = !mesh;
  el('unpin').hidden = !pinned;
  el('hide-object').textContent = mesh && hiddenMeshes.has(mesh.name) ? 'Zobrazit' : 'Skrýt';
  if (displayedMesh === mesh) return;
  if (pinned && mesh) setWorkshopPanel('inspector', true);
  displayedMesh = mesh;
  for (const [name, button] of treeButtons) button.setAttribute('aria-pressed', String(name === mesh?.name));
  el('object').value = mesh?.name || '';
  if (!mesh) return;
  el('object-title').textContent = mesh.info.title;
  el('mesh-id').textContent = mesh.name;
  el('description').textContent = mesh.info.description;
  el('details').replaceChildren();
  const size = [0,1,2].map(i => (mesh.bounds[i+3]-mesh.bounds[i]).toFixed(1)).join(' × ');
  const subassembly = (assemblyGuide.subassemblies || []).find(group=>(group.parts || []).some(part=>matchesPart(mesh.name,part)));
  const steps = assemblyGuide.steps.filter(step=>currentPartPatterns(step).some(part=>matchesPart(mesh.name,part)));
  const installationDetails = [
    `Poloha v CAD X / Y / Z: ${mesh.center.map(value=>value.toFixed(1)).join(' / ')} mm.`,
    ...(subassembly ? [`Podsestava: ${subassembly.title}. ${subassembly.function || ''}`,`Montáž: ${subassembly.installation || 'Viz montážní průvodce.'}`,`Servis: ${subassembly.removal || 'Viz montážní průvodce.'}`] : []),
    ...(steps.length ? ['Montážní kroky: '+steps.map(step=>step.title).join('; ')] : []),
  ];
  for (const detail of [...mesh.info.details,...installationDetails, `Obálka v CAD (osy X × Y × Z): ${size} mm.`, `Zdroj modelu: ${mesh.info.source}`]) {
    const li = document.createElement('li'); li.textContent = detail; el('details').append(li);
  }
  el('color').value = colorHex(mesh.color);
  el('storage-status').textContent = storageMessage;
  el('connections').replaceChildren();
  const edges = scopedRoutes(mesh);
  el('empty-connections').hidden = edges.length > 0;
  el('empty-connections').textContent = mesh.name.startsWith('spare_') ? 'Rezervní díl není zapojený.' : 'Pro tento díl nejsou v podkladech přiřazená spojení.';
  for (const edge of edges) {
    const li = document.createElement('li');
    li.textContent = edge.id+': '+edge.from+' → '+edge.to+' · '+edge.net+' · svazek '+(edge.bundle || 'neuveden');
    if (edge.note) { const note = document.createElement('small'); note.textContent = edge.note; li.append(note); }
    el('connections').append(li);
  }
  el('evidence').textContent = model.sources.map(source => `${source.name} (SHA-256 ${source.sha256})`).join('\n');
}
function updateObjectOptions() {
  const select = el('object'), current = select.value;
  select.replaceChildren(new Option('Vyberte objekt…', ''));
  for (const mesh of model.meshes) select.add(new Option(`${mesh.info.title} · ${mesh.name}`, mesh.name));
  select.value = current;
}
let overlayLayoutKey = '';
function updateOverlay(selected) {
  const names = inspecting() && !selected && !selectedWire && !selectedEndpoint && !hoveredConnection;
  const mode = names ? 'names' : selected ? 'connections' : 'none';
  const panel = el('inspector');
  // Picking remains live; stable empty-space movement reuses the existing DOM layout.
  const key = JSON.stringify([
    mode,mode === 'connections' ? selected.name : '',canvas.clientWidth,canvas.clientHeight,
    yaw,pitch,distance,cameraTarget,el('lid').value,trayAngles,mode,assemblyIndex,selectedWire,selectedEndpoint,hoveredConnection,
    el('wire-scope').value,el('wires').checked,el('dimensions').checked,
    ...['cover','case','beds','hardware','wands','bank','textile'].map(id=>el(id).checked),
    [...hiddenMeshes],[...hiddenGroups],isolateName,
    panel.hidden ? 0 : panel.offsetWidth,panel.hidden ? 0 : panel.offsetHeight,
  ]);
  if (key === overlayLayoutKey) return;
  overlayLayoutKey = key;
  el('overlay').replaceChildren(); el('names').replaceChildren(); el('orientation').replaceChildren();
  if (names) allNames();
  drawRoutes(selected);
  if (el('dimensions').checked) drawDimensions();
  drawOrientation();
}
function draw() {
  const ratio = Math.min(2,devicePixelRatio || 1);
  const w = Math.max(1,Math.round(canvas.clientWidth*ratio)), h = Math.max(1,Math.round(canvas.clientHeight*ratio));
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
  gl.viewport(0,0,w,h); gl.enable(gl.DEPTH_TEST); gl.disable(gl.CULL_FACE); gl.disable(gl.BLEND);
  if (automaticFit) fitScene();
  advanceCamera();
  const target = cameraTarget, fitDistance = distance;
  const eye = [target[0]+fitDistance*Math.cos(pitch)*Math.cos(yaw),target[1]+fitDistance*Math.cos(pitch)*Math.sin(yaw),target[2]+fitDistance*Math.sin(pitch)];
  projection = perspective(w/h); view = look(eye,target);
  gl.uniformMatrix4fv(loc.projection,false,projection); gl.uniformMatrix4fv(loc.view,false,view);
  hovered = inspecting() && !drag ? pick() : null;
  hoveredConnection = inspecting() && !drag && !selectedWire && !selectedEndpoint ? wireHit() : null;
  const selected = pinned || hovered;
  gl.clearColor(13/255,18/255,24/255,1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  renderMeshes(false, hovered || selected);
  el('liftvalue').textContent = lift(trayAngles[+el('tray').value-1] || 0).toFixed(1)+' mm';
  el('lidvalue').textContent = el('lid').value+'°';
  el('inspect').setAttribute('aria-pressed', String(inspectEnabled));
  el('inspect-hint').hidden = !inspecting();
  el('inspect-hint').textContent = pinned ? `Připnuto: ${pinned.name}. Klik na jiný objekt změní výběr; klik do prázdna nebo Zrušit výběr jej zruší.` : selected ? 'Klik na objekt připne jeho kartu a umožní změnit barvu.' : 'Inspect: názvy všech objektů zapnutých vrstev. Najeďte na díl; Servis odkryje elektroniku.';
  canvas.style.cursor = inspecting() ? 'crosshair' : drag ? 'grabbing' : 'grab';
  updatePanel(selected);
  twinViewer?.selection(pinned?.name || null);
  updateWireDetails();
  updateOverlay(selected);
  el('selection-bar').hidden = !pinned;
  el('selection-title').textContent = pinned?.info?.title || pinned?.name || '';
  if (cameraTransition) requestDraw();
}
function requestDraw() {
  if (frameRequested) return;
  frameRequested = true;
  requestAnimationFrame(() => { frameRequested = false; draw(); });
}
function layersChanged() { displayedMesh = undefined; updateObjectOptions(); requestDraw(); }
function selectAtPointer(openDetails = false) {
  draw();
  const hit = inspecting() ? wireHit() : null;
  selectedWire = hit?.route || ''; selectedEndpoint = hit?.endpoint || '';
  pinned = hit ? null : pick();
  el('wire').value = selectedWire; el('endpoint').value = selectedEndpoint;
  displayedMesh = undefined; updateWireDetails();
  if (openDetails && pinned) setWorkshopPanel('inspector',true);
  requestDraw();
}
function gestureSnapshot() {
  const points = [...activePointers.values()];
  if (!points.length) return null;
  if (points.length === 1) return {x:points[0].x,y:points[0].y,span:0,pan:points[0].button === 2};
  const [a,b] = points;
  return {x:(a.x+b.x)/2,y:(a.y+b.y)/2,span:Math.hypot(a.x-b.x,a.y-b.y),pan:true};
}
function panCamera(dx,dy) {
  const forward = [Math.cos(pitch)*Math.cos(yaw),Math.cos(pitch)*Math.sin(yaw),Math.sin(pitch)];
  const right = norm(cross([0,0,1],forward)), up = cross(forward,right);
  const units = 2*distance*Math.tan(.3)/Math.max(1,canvas.clientHeight);
  cameraTarget = boundTarget(cameraTarget.map((value,axis)=>value+(-dx*right[axis]+dy*up[axis])*units));
}
canvas.addEventListener('pointerdown', event => {
  event.preventDefault(); pointer = [event.clientX,event.clientY];
  // Desktop Shift picking stays immediate; touch selection waits until a tap is known.
  if (inspecting() && event.pointerType !== 'touch' && event.button !== 2) { selectAtPointer(); return; }
  cameraTransition = null;
  activePointers.set(event.pointerId,{x:event.clientX,y:event.clientY,button:event.button || 0});
  gestureWasMulti = gestureWasMulti || activePointers.size > 1;
  gesture = gestureSnapshot(); drag = [gesture.x,gesture.y]; dragDistance = 0;
  canvas.setPointerCapture(event.pointerId);
});
function endDrag(event) {
  if (event?.pointerId !== undefined) activePointers.delete(event.pointerId); else activePointers.clear();
  gesture = gestureSnapshot(); drag = gesture ? [gesture.x,gesture.y] : null;
  if (!drag) { gestureWasMulti = false; pointer = null; }
  requestDraw();
}
canvas.addEventListener('pointerup', event => {
  if (drag && dragDistance < 4 && !gestureWasMulti && event.button !== 2) {
    pointer = [event.clientX,event.clientY]; selectAtPointer(event.pointerType === 'touch');
  }
  endDrag(event);
});
canvas.addEventListener('pointercancel', endDrag);
canvas.addEventListener('lostpointercapture', endDrag);
canvas.addEventListener('pointermove', event => {
  pointer = [event.clientX,event.clientY]; shiftDown = !!event.shiftKey;
  const pointerId = event.pointerId ?? (activePointers.size === 1 ? [...activePointers.keys()][0] : undefined);
  if (drag && activePointers.has(pointerId)) {
    event.preventDefault(); cameraTransition = null; automaticFit = false;
    const previous = activePointers.get(pointerId);
    activePointers.set(pointerId,{...previous,x:event.clientX,y:event.clientY});
    const next = gestureSnapshot(), dx = next.x-gesture.x, dy = next.y-gesture.y;
    dragDistance += Math.hypot(dx,dy);
    if (next.pan) {
      panCamera(dx,dy);
      if (next.span > 2 && gesture.span > 2) distance = boundDistance(distance*gesture.span/next.span);
    } else {
      yaw -= dx*.006; pitch = Math.max(.01,Math.min(Math.PI/2-.001,pitch+dy*.006));
    }
    gesture = next; drag = [next.x,next.y];
  }
  requestDraw();
});
canvas.addEventListener('pointerleave', () => { if (!drag) pointer = null; requestDraw(); });
canvas.addEventListener('contextmenu', event=>event.preventDefault());
canvas.addEventListener('wheel', event => {
  event.preventDefault(); automaticFit = false; cameraTransition = null;
  distance = boundDistance(distance*Math.exp(Math.max(-200,Math.min(200,event.deltaY))*.001)); requestDraw();
}, {passive:false});
canvas.addEventListener('keydown',event=>{
  if (!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','=','-','Home'].includes(event.key)) return;
  event.preventDefault(); cameraTransition = null;
  if (event.key === 'Home') { resetView(); return; }
  automaticFit = false;
  if (event.key === 'ArrowLeft') yaw += .1;
  if (event.key === 'ArrowRight') yaw -= .1;
  if (event.key === 'ArrowUp') pitch = Math.min(Math.PI/2-.001,pitch+.1);
  if (event.key === 'ArrowDown') pitch = Math.max(.01,pitch-.1);
  if (['+','='].includes(event.key)) distance = boundDistance(distance*.85);
  if (event.key === '-') distance = boundDistance(distance/ .85);
  requestDraw();
});
el('inspector').addEventListener('pointerenter', () => {
  if (!pinned) return;
  pointer = null; requestDraw();
});
function clearWireSelection() {
  selectedWire = ''; selectedEndpoint = ''; hoveredConnection = null;
  el('wire').value = ''; el('endpoint').value = '';
}
function closeInspect() { shiftDown = false; inspectEnabled = false; pinned = null; hovered = null; clearWireSelection(); requestDraw(); }
function setWorkshopPanel(name, open) {
  const panel = el(name === 'explorer' ? 'sidebar' : 'inspector-rail');
  const activeElement = document.activeElement;
  const focusWasInside = panel.contains(activeElement);
  panel.setAttribute('data-open', String(open));
  const toggle = el(name+'-toggle');
  toggle.setAttribute('aria-expanded', String(open));
  if (open) {
    if (mode === 'assembly') setGuideOpen(false);
    setMotionOpen(false);
    const other = name === 'explorer' ? 'inspector' : 'explorer';
    const otherPanel = el(other === 'explorer' ? 'sidebar' : 'inspector-rail');
    const focusWasInOther = otherPanel.contains(activeElement);
    otherPanel.setAttribute('data-open', 'false');
    el(other+'-toggle').setAttribute('aria-expanded', 'false');
    const closeButton = el(name+'-close');
    if (focusWasInOther && !otherPanel.getClientRects().length && closeButton.getClientRects().length) {
      closeButton.focus({preventScroll:true});
    }
  } else if (focusWasInside && !panel.getClientRects().length && toggle.getClientRects().length) {
    toggle.focus({preventScroll:true});
  }
  requestDraw();
}
for (const name of ['explorer','inspector']) {
  el(name+'-toggle').onclick = () => setWorkshopPanel(name, el(name+'-toggle').getAttribute('aria-expanded') !== 'true');
  el(name+'-close').onclick = () => setWorkshopPanel(name, false);
}
el('begin-inspect').onclick = () => { inspectEnabled = true; requestDraw(); };
const localExport = ['localhost','127.0.0.1'].includes(window.location?.hostname) && window.location?.pathname === '/viewer.html';
const localViewer = window.location?.protocol === 'file:' || localExport;
el('workshop-home').setAttribute('href', localViewer ? './reference-sources.html' : '/');
if (localViewer) el('workshop-home').setAttribute('aria-label', 'Zdrojové podklady offline balíčku F2');
document.addEventListener('keydown', event => {
  if (event.key === 'Shift' && !shiftDown) { shiftDown = true; requestDraw(); }
  if (event.key === 'Escape') { closeInspect(); setWorkshopPanel('explorer', false); setWorkshopPanel('inspector', false); setGuideOpen(false); setMotionOpen(false); el('camera-menu').open = false; }
});
document.addEventListener('keyup', event => {
  if (event.key === 'Shift') { shiftDown = false; inspectEnabled = false; requestDraw(); }
});
window.addEventListener('blur', () => { shiftDown = false; endDrag(); });
el('inspect').onclick = () => { inspectEnabled = !inspectEnabled; if (!inspectEnabled) pinned = null; requestDraw(); };
el('close-inspect').onclick = closeInspect;
el('unpin').onclick = () => { pinned = null; pointer = null; clearWireSelection(); requestDraw(); };
el('object').onchange = () => {
  clearWireSelection(); displayedMesh = undefined;
  pinned = meshesByName.get(el('object').value) || null; pointer = null; updateWireDetails(); requestDraw();
};
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
el('lid').oninput = requestDraw;
el('lift').oninput = () => {
  if (twinViewer?.active() || effect || effectFault || mode === 'transport' || mode === 'assembly') return;
  trayAngles[+el('tray').value-1] = +el('lift').value; requestDraw();
};
el('tray').onchange = () => { el('lift').value = trayAngles[+el('tray').value-1]; requestDraw(); };
for (const id of ['cover','case','beds','hardware']) el(id).oninput = layersChanged;
el('open').onclick = () => setMode('operation');
el('service').onclick = () => setMode('service');
el('closed').onclick = () => setMode('transport');
el('assembly-mode').onclick = () => setMode('assembly');
el('mode').onchange = () => setMode(el('mode').value);
for (const id of ['wands','bank','textile','wires','dimensions','case-opacity']) el(id).oninput = layersChanged;
for (const name of ['top','front','back','left','side','overview']) el('view-'+name).onclick = () => setView(name);
el('view-reset').onclick = resetView;
el('view-fit').onclick = () => { isolateName = null; resetView(); };
el('visual-mode').onchange = () => { visualMode = el('visual-mode').value; resetView(); layersChanged(); };
el('selection-focus').onclick = () => { if (pinned) zoomObject(pinned); };
el('selection-details').onclick = () => setWorkshopPanel('inspector',true);
el('selection-clear').onclick = () => { pinned = null; clearWireSelection(); requestDraw(); };
el('hide-object').onclick = () => { if (pinned) hiddenMeshes.has(pinned.name) ? hiddenMeshes.delete(pinned.name) : hiddenMeshes.add(pinned.name); layersChanged(); };
el('isolate-object').onclick = () => { isolateName = pinned?.name || null; layersChanged(); };
el('show-all').onclick = () => { hiddenMeshes.clear(); hiddenGroups.clear(); isolateName = null; visualMode = 'complete'; el('visual-mode').value = 'complete'; resetView(); buildTree(); layersChanged(); };
el('zoom-object').onclick = () => { if (pinned) zoomObject(pinned); };
el('wire').onchange = () => {
  pinned = null; hovered = null; hoveredConnection = null; pointer = null;
  selectedWire = el('wire').value; selectedEndpoint = ''; el('endpoint').value = '';
  displayedMesh = undefined; updateWireDetails(); requestDraw();
};
el('endpoint').onchange = () => {
  pinned = null; hovered = null; hoveredConnection = null; pointer = null;
  selectedEndpoint = el('endpoint').value; selectedWire = ''; el('wire').value = '';
  displayedMesh = undefined; updateWireDetails(); requestDraw();
};
el('wire-scope').onchange = () => { displayedMesh = undefined; updateWireDetails(); requestDraw(); };
el('assembly-next').onclick = () => moveAssembly(1);
el('assembly-prev').onclick = () => moveAssembly(-1);
el('assembly-restart').onclick = () => { setMode('assembly'); };
el('play-main').onclick = () => startEffect('main');
el('play-short').onclick = () => startEffect('short');
el('effect-stop').onclick = stopEffect;
el('dock-effect-stop').onclick = stopEffect;
el('service-reset').onclick = () => {
  if (twinViewer?.active() || mode !== 'service') return;
  effect = null; effectFault = false; trayAngles.fill(0); lightStates.fill(null);
  el('effect-phase').textContent = '';
  el('lift').disabled = false; el('lift').value = 0;
  updateEffectControls();
  showEffectTimeline(model.effects?.main || []);
  updateEffectStatus('Servisní reset modelu. Fyzická poloha musí být ručně zkontrolována.'); requestDraw();
};
function resetView() {
  cameraTransition = null; automaticFit = true; setView('overview');
}
function setView(name) {
  cameraTransition = null;
  const views = {top:[-Math.PI/2,Math.PI/2-.001],front:[-Math.PI/2,.01],back:[Math.PI/2,.01],left:[Math.PI,.01],side:[0,.01],overview:[-1.05,.66],iso:[-1.05,.66]};
  [yaw,pitch] = views[name] || views.overview;
  automaticFit = true; el('camera-menu').open = false; requestDraw();
  for (const viewName of ['top','front','back','left','side','overview']) el('view-'+viewName).setAttribute('aria-pressed',String(viewName === name));
}
function transitionCamera(target,newDistance) {
  automaticFit = false;
  const destination = {target:[...target],distance:boundDistance(newDistance)};
  if (reducedMotion?.matches) { cameraTarget = destination.target; distance = destination.distance; cameraTransition = null; }
  else cameraTransition = {...destination,from:[...cameraTarget],fromDistance:distance,start:performance.now()};
  requestDraw();
}
function advanceCamera() {
  if (!cameraTransition) return;
  const transition = cameraTransition, progress = Math.min(1,(performance.now()-transition.start)/240), eased = 1-(1-progress)**3;
  cameraTarget = transition.from.map((value,axis)=>value+(transition.target[axis]-value)*eased);
  distance = transition.fromDistance+(transition.distance-transition.fromDistance)*eased;
  if (progress === 1) cameraTransition = null;
}
function focusMeshes(meshes) {
  if (!meshes.length) return;
  const points = meshes.flatMap(mesh=>Array.from({length:8},(_,corner)=>transform(movement(mesh),[...[0,1,2].map(axis=>mesh.bounds[axis+((corner>>axis)&1)*3]),1]).slice(0,3)));
  const low = [0,1,2].map(axis=>Math.min(...points.map(point=>point[axis]))), high = [0,1,2].map(axis=>Math.max(...points.map(point=>point[axis])));
  const radius = Math.hypot(...high.map((value,axis)=>(value-low[axis])/2));
  const angle = Math.atan(Math.tan(.3)*Math.min(1,canvas.clientWidth/Math.max(1,canvas.clientHeight)));
  transitionCamera(low.map((value,axis)=>(value+high[axis])/2),Math.max(75,radius/Math.sin(angle)*1.2));
}
function zoomObject(mesh) { focusMeshes([mesh]); }
function buildTree() {
  el('tree').replaceChildren();
  treeButtons.clear();
  el('part-count').textContent = model.meshes.length+' dílů';
  const titles = {case:'Dřevěný kufr',lid:'Víko',fixed:'Rám a nosiče',moving:'Mechanismy',moving_hw:'Pohyblivé díly',hardware:'Elektronika a spoje',cover:'Kryty',fabric:'Textil',lid_fabric:'Textil víka',bond:'Lepené spoje',fastener:'Spojovací materiál',wand:'Vyjímatelné hůlky',restraint:'Přepravní zajištění'};
  for (const group of [...new Set(model.meshes.map(mesh=>mesh.group))]) {
    const section = document.createElement('details'), summary = document.createElement('summary');
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = !hiddenGroups.has(group);
    checkbox.setAttribute('aria-label','Zobrazit vrstvu '+(titles[group] || group));
    checkbox.onchange = () => { checkbox.checked ? hiddenGroups.delete(group) : hiddenGroups.add(group); layersChanged(); };
    const visibilityLabel = document.createElement('label'); visibilityLabel.className = 'tree-visibility';
    visibilityLabel.append(checkbox); summary.append(visibilityLabel);
    const text = document.createElement('span'); text.textContent = ' '+(titles[group] || group); summary.append(text); section.append(summary);
    for (const mesh of model.meshes.filter(item=>item.group === group)) {
      const button = document.createElement('button'); button.textContent = mesh.name;
      button.setAttribute('aria-pressed', String(mesh === pinned)); treeButtons.set(mesh.name, button);
      button.onclick = () => { pinned = mesh; inspectEnabled = true; pointer = null; clearWireSelection(); requestDraw(); };
      section.append(button);
    }
    el('tree').append(section);
  }
}
function setMode(value) {
  if (effect) stopEffect();
  if (effectFault) {
    value = 'service';
    updateEffectStatus('Poloha po přerušení je neznámá. Před změnou režimu ručně zkontrolujte mechanismus a potvrďte servisní reset; dosažené úhly zůstávají zachované.');
  }
  mode = value; el('mode').value = value; cameraTransition = null; automaticFit = true;
  visualMode = 'complete'; el('visual-mode').value = 'complete';
  setMotionOpen(false);
  if (mode !== 'assembly') setGuideOpen(false);
  for (const [id, target] of [['open','operation'],['service','service'],['closed','transport'],['assembly-mode','assembly']]) {
    el(id).setAttribute('aria-pressed', String(mode === target));
  }
  pinned = null; hovered = null; hoveredConnection = null; pointer = null;
  clearWireSelection(); displayedMesh = undefined;
  el('object').value = ''; el('wire').value = ''; el('endpoint').value = ''; el('wire-scope').value = 'branch';
  hiddenMeshes.clear(); hiddenGroups.clear(); isolateName = null;
  const transport = mode === 'transport', service = mode === 'service';
  el('lid').value = transport ? 0 : 105;
  el('cover').checked = !service; el('case').checked = true; el('beds').checked = true; el('hardware').checked = true;
  el('wands').checked = !transport && !service; el('bank').checked = !transport; el('textile').checked = !transport && !service;
  el('wires').checked = service; el('case-opacity').value = service ? .2 : 1;
  el('dimensions').checked = false;
  if (!twinViewer?.active() && (transport || mode === 'assembly')) trayAngles.fill(0);
  el('lift').value = trayAngles[+el('tray').value-1] || 0;
  el('lift').disabled = !!twinViewer?.active() || transport || mode === 'assembly' || effectFault;
  el('service-reset').disabled = !!twinViewer?.active() || !service;
  el('mode-note').textContent = transport ? 'Ruční přepravní sestava: všech šest pásků, prázdná lůžka, odpojená banka, parkovaný USB kabel. Stav pásků nelze elektronicky potvrdit.' : 'Jde o náhled sestavy. Přepnutí režimu nepohybuje fyzickým mechanismem.';
  if (mode === 'assembly') { assemblyIndex = -1; setGuideOpen(true); }
  if (!effectFault) {
    el('effect-phase').textContent = '';
    updateEffectStatus('Časování zdvihu je ilustrační do kalibrace. Lokální poslech tónů není fyzická přejímka.');
    showEffectTimeline(model.effects?.main || []);
  }
  updateEffectControls();
  buildTree(); updateAssembly(); updateTransportInstructions(); layersChanged(); updateWireDetails();
}
function materialLabel(id) {
  const material = assemblyGuide.materialCatalog?.[id];
  return typeof material === 'string' ? material : material?.label || material?.name || id;
}
function setGuideOpen(open) {
  el('assembly-panel').setAttribute('data-open',String(open));
  if (open) { setWorkshopPanel('explorer',false); setWorkshopPanel('inspector',false); setMotionOpen(false); }
  el('guide-toggle').setAttribute('aria-expanded',String(open));
  if (open && el('guide-close').getClientRects().length) el('guide-close').focus({preventScroll:true});
  if (!open && el('assembly-panel').contains(document.activeElement)) el('guide-toggle').focus({preventScroll:true});
  requestDraw();
}
function setMotionOpen(open) {
  el('motion-panel').setAttribute('data-open',String(open));
  if (open) { setWorkshopPanel('explorer',false); setWorkshopPanel('inspector',false); setGuideOpen(false); }
  el('motion-toggle').setAttribute('aria-expanded',String(open));
  if (open && el('motion-close').getClientRects().length) el('motion-close').focus({preventScroll:true});
  if (!open && el('motion-panel').contains(document.activeElement)) el('motion-toggle').focus({preventScroll:true});
  requestDraw();
}
el('guide-toggle').onclick = () => setGuideOpen(el('guide-toggle').getAttribute('aria-expanded') !== 'true');
el('guide-close').onclick = () => setGuideOpen(false);
el('motion-toggle').onclick = () => setMotionOpen(el('motion-toggle').getAttribute('aria-expanded') !== 'true');
el('motion-close').onclick = () => setMotionOpen(false);
el('assembly-step').onchange = () => jumpAssembly(+el('assembly-step').value);
el('assembly-focus').onclick = focusAssembly;
el('assembly-exit').onclick = () => { setMode('operation'); el('assembly-mode').focus({preventScroll:true}); };
function readableItems(value) {
  const label = item => typeof item === 'string' ? item : item?.text || item?.pattern || '';
  return Array.isArray(value) ? value.map(label).join('\n') : label(value);
}
function updateAssembly() {
  const active = mode === 'assembly', step = currentStep(), count = assemblyGuide.steps.length;
  el('assembly-panel').hidden = !active; el('assembly-controls').hidden = !active;
  el('motion-toggle').hidden = active;
  el('assembly-counter').textContent = (assemblyIndex+1)+' / '+count;
  el('assembly-title').textContent = step ? (assemblyIndex+1)+'/'+count+' · '+step.title : 'Začátek montáže';
  el('assembly-progress').max = Math.max(1,count); el('assembly-progress').value = assemblyIndex+1;
  el('assembly-step').replaceChildren(new Option('Začátek / příprava','-1'));
  for (const [index,item] of assemblyGuide.steps.entries()) el('assembly-step').add(new Option((index+1)+'. '+item.title,String(index)));
  el('assembly-step').value = String(assemblyIndex);
  el('assembly-text').textContent = step?.instruction || (count ? 'Vyberte první krok nebo přejděte tlačítkem Další. Kroky neoznačují fyzické dokončení.' : 'Montážní data nejsou dostupná.');
  el('assembly-context').textContent = isBenchStep() ? 'PRÁCE NA STOLE MIMO KUFR · Zobrazené díly v návrhovém vzájemném uspořádání; korpus je skrytý. Nejde o dráhu zasouvání.' : step ? 'STAV V KUFŘÍKU · Fyzicky vložené díly + mosazně zvýrazněné díly umístěné tímto krokem. Podsestavy připravené na stole se objeví až v kroku vložení. Vložení není animované.' : 'NÁVRHOVÝ PRŮVODCE · Postup ověřte na skutečném prototypu.';
  el('assembly-location').textContent = step ? ['Podsestava: '+readableItems((Array.isArray(step.subassembly) ? step.subassembly : [step.subassembly]).filter(Boolean).map(id=>(assemblyGuide.subassemblies || []).find(item=>item.id===id)?.title || id)),step.location && 'Pracoviště / poloha: '+step.location,step.direction && 'Směr vložení: '+step.direction,step.installation?.clearance_mm && 'Požadovaný přístup: '+step.installation.clearance_mm+' mm'].filter(Boolean).join('\n') : '';
  el('assembly-access').textContent = step?.access?.length ? 'Přístup rukou a nástroje:\n'+readableItems(step.access) : '';
  el('assembly-connectors').textContent = step?.connectors?.length ? 'Konektory / kabely:\n'+readableItems(step.connectors) : '';
  el('assembly-removal').textContent = step?.removal ? 'Servis a demontáž: '+readableItems(step.removal) : '';
  el('assembly-check').textContent = step?.check ? 'Kontrola před pokračováním: '+step.check : '';
  el('assembly-parts').textContent = step ? (step.preparedParts?.length ? 'Díly připravované na stole (ještě neosazené):\n' : 'Aktuální díly (zvýrazněné):\n')+readableItems(step.partSummary || currentPartPatterns(step)) : '';
  const next = assemblyGuide.steps[assemblyIndex+1];
  el('assembly-next-parts').textContent = next ? 'Následuje: '+next.title+'\n'+readableItems(next.partSummary || currentPartPatterns(next)) : count ? 'Poslední krok průvodce. Fyzická přejímka zůstává samostatná.' : '';
  el('assembly-materials').textContent = step ? 'Materiál:\n'+(step.materials || []).map(item=>'• '+item.quantity+' × '+materialLabel(item.id)).join('\n')+'\n\nNástroje:\n'+(step.tools || []).map(tool=>'• '+tool).join('\n') : '';
  const predecessors = (step?.depends || []).map(id=>assemblyGuide.steps.find(item=>item.id===id)?.title || id);
  el('assembly-depends').textContent = [predecessors.length && 'Předchůdci: '+predecessors.join(', '),step?.installation?.absent?.length && 'Před vložením ještě neosazovat: '+step.installation.absent.join(', ')].filter(Boolean).join('\n');
  el('assembly-prev').disabled = assemblyIndex < 0; el('assembly-next').disabled = assemblyIndex >= count-1;
  el('assembly-focus').disabled = !step;
}
function focusAssembly() {
  const step = currentStep(); if (!step) return;
  const patterns = isBenchStep() ? benchPreviewPatterns() : [...casePartPatterns(step),...(step.installation?.targets || [])];
  focusMeshes(model.meshes.filter(mesh=>visible(mesh) && patterns.some(part=>matchesPart(mesh.name,part))));
}
function jumpAssembly(index) {
  if (mode !== 'assembly' || effectFault) return;
  if (!Number.isInteger(index)) return;
  assemblyIndex = Math.max(-1,Math.min(assemblyGuide.steps.length-1,index));
  pinned = null; clearWireSelection(); displayedMesh = undefined;
  const step = currentStep(); if (step) setView(step.camera);
  updateAssembly(); layersChanged(); focusAssembly();
}
function updateTransportInstructions() {
  el('transport-panel').hidden = mode !== 'transport';
  for (const [target,instructions] of [
    ['transport-instructions',assemblyGuide.transport],
    ['recommission-instructions',assemblyGuide.recommission || assemblyGuide.startup],
  ]) {
    el(target).replaceChildren();
    for (const instruction of instructions || []) {
      const item = document.createElement('li');
      item.textContent = typeof instruction === 'string' ? instruction : [instruction.title,instruction.instruction,instruction.check].filter(Boolean).join(' — ');
      el(target).append(item);
    }
    if (!instructions?.length) {
      const item = document.createElement('li'); item.textContent = 'Postup není v montážních datech dostupný.'; el(target).append(item);
    }
  }
}
function moveAssembly(delta) { jumpAssembly(assemblyIndex+delta); }
function updateWireDetails() {
  const entries = scopedRoutes(pinned || hovered);
  const endpoint = selectedEndpoint || hoveredConnection?.endpoint;
  const pin = model.endpoints?.[endpoint];
  const pinNote = pin ? '\nPin '+endpoint+': '+pin.position_status+'; fyzický terminál '+pin.physical_terminal : '';
  el('wire-details').textContent = entries.length ? entries.map(route=>route.id+': '+route.from+' → '+route.to+'; síť '+route.net+'; svazek '+route.bundle+'. '+(route.note || '')+((mode === 'transport' || !el('bank').checked) && route.transport_from ? ' Fyzický konec: '+route.transport_from+' — '+route.transport_connection : '')).join('\n')+pinNote+'\nPiny jsou návrhové obálky, smyčky zobrazují pouze aproximaci flexibility; nejde o změřené souřadnice.' : 'Vyberte vodič, pin nebo díl. Větev je konkrétní vodič; svazek a síť rozšíří výběr podle společných ID.'+pinNote;
}
function drawDimensions() {
  const c = model.case || {inside_x:430,inside_y:269,base_height:114.3};
  for (const [start,end,text] of [
    [[0,0,c.base_height],[c.inside_x,0,c.base_height],'Průchozí horní X '+c.inside_x+' mm'],
    [[0,0,c.base_height],[0,c.inside_y,c.base_height],'Průchozí horní Y '+c.inside_y+' mm'],
    [[0,0,0],[0,0,c.base_height],'Spodní Z '+c.base_height+' mm'],
  ]) {
    const a = projectPoint(start), b = projectPoint(end); if (!a || !b) continue;
    svgElement('line',{x1:a[0],y1:a[1],x2:b[0],y2:b[1],stroke:'#b79852','stroke-width':2});
    label(text,(a[0]+b[0])/2,(a[1]+b[1])/2);
  }
}
function drawOrientation() {
  const c = model.case || {inside_x:430,inside_y:269};
  for (const [position,text] of [
    [[c.inside_x/2,0,0],'PŘEDEK · ovládání'],
    [[c.inside_x/2,c.inside_y,0],'ZÁDA · panty'],
    [[0,c.inside_y/2,0],'LEVÁ'],
    [[c.inside_x,c.inside_y/2,0],'PRAVÁ'],
  ]) {
    const point = projectPoint(position); if (!point) continue;
    label(text,Math.max(4,Math.min(canvas.clientWidth-125,point[0])),Math.max(4,Math.min(canvas.clientHeight-22,point[1])),'orientation');
  }
}
function updateEffectStatus(text) { el('effect-status').textContent = text; }
let timelinePhases = [];
function showEffectTimeline(phases) {
  timelinePhases = phases;
  el('effect-timeline').replaceChildren();
  for (const phase of phases) {
    const segment = document.createElement('span');
    segment.className = 'timeline-phase'; segment.textContent = phase.phase;
    segment.style.flexGrow = Math.max(1,phase.duration_ms);
    segment.setAttribute('title', phase.phase+' · '+(phase.duration_ms/1000).toFixed(2)+' s');
    segment.setAttribute('aria-label', phase.phase+'; ilustrační čas '+(phase.duration_ms/1000).toFixed(2)+' sekund');
    el('effect-timeline').append(segment);
  }
  updateEffectTimeline(-1,0);
}
function updateEffectTimeline(index, fraction) {
  const total = timelinePhases.reduce((sum,phase)=>sum+phase.duration_ms,0);
  const elapsed = index < 0 ? 0 : timelinePhases.slice(0,index).reduce((sum,phase)=>sum+phase.duration_ms,0)+(timelinePhases[index]?.duration_ms || 0)*fraction;
  el('timeline-time').textContent = (elapsed/1000).toFixed(1)+' / '+(total/1000).toFixed(1)+' s';
  for (const [position,segment] of Array.from(el('effect-timeline').children).entries()) {
    segment.setAttribute('aria-current', position === index ? 'step' : 'false');
    segment.setAttribute('data-complete', String(position < index));
  }
}
function updateEffectControls() {
  el('play-main').disabled = !!twinViewer?.active() || mode !== 'operation' || effectFault || !!effect;
  el('play-short').disabled = !!twinViewer?.active() || mode !== 'operation' || effectFault || !!effect;
  el('effect-stop').disabled = !effect;
  el('dock-effect-stop').hidden = !effect;
  el('dock-effect-stop').disabled = !effect;
}
let audioContext = null, toneOscillator = null;
function stopTone() { if (toneOscillator) { toneOscillator.stop(); toneOscillator = null; } }
function playTone(frequency) {
  stopTone();
  if (!audioContext || !el('audio-preview').checked || !frequency) return;
  const gain = audioContext.createGain(); gain.gain.value = .035;
  toneOscillator = audioContext.createOscillator(); toneOscillator.type = 'sine';
  toneOscillator.frequency.value = frequency; toneOscillator.connect(gain); gain.connect(audioContext.destination); toneOscillator.start();
}
function startEffect(kind) {
  if (twinViewer?.active() || mode !== 'operation' || effectFault || effect) return;
  const phases = model.effects?.[kind];
  if (!Array.isArray(phases) || !phases.length) { updateEffectStatus('Sdílená data efektů nejsou dostupná.'); return; }
  // Include audio setup in the budget, using the live monotonic clock.
  const presentationStarted = performance.now();
  if (el('audio-preview').checked && !audioContext && window.AudioContext) audioContext = new window.AudioContext();
  audioContext?.resume()?.catch(() => updateEffectStatus('Lokální zvuk není dostupný; vizuální sekvence pokračuje bez zvuku.'));
  effect = {phases,index:0,started:null,presentationStarted,selected:+el('tray').value-1,initial:[...trayAngles]};
  showEffectTimeline(phases);
  updateEffectControls();
  el('lift').disabled = true;
  updateEffectStatus('Ilustrační časování; zdvih vyžaduje kalibraci serva. Zvuk v prohlížeči je pouze lokální poslech tónů.');
  requestAnimationFrame(tickEffect);
}
function stopEffect() {
  if (!effect) return;
  effect = null; effectFault = true; lightStates.fill(null); stopTone(); el('lift').disabled = true;
  updateEffectControls();
  updateEffectStatus('Stop: výstupy vypnuty, poloha modelu zůstává. Skutečná poloha je neznámá; ruční kontrola a explicitní servisní reset.');
  requestDraw();
}
function targetIndexes(target,selected,fraction) {
  if (typeof target === 'number') return [target-1];
  if (target === 'all') return trayAngles.map((_,index)=>index);
  if (target === 'next') return [(selected+1)%trayAngles.length];
  if (target === 'previous') return [(selected+trayAngles.length-1)%trayAngles.length];
  if (target === 'scan') return [Math.min(trayAngles.length-1,Math.floor(fraction*trayAngles.length))];
  return [selected];
}
function tickEffect(timestamp) {
  if (!effect) return;
  // A shared rAF timestamp can predate admission or earlier slow callbacks.
  // Check the live monotonic clock before any animation, tone or light output.
  const deadlineNow = performance.now();
  if (deadlineNow < effect.presentationStarted || deadlineNow-effect.presentationStarted >= 15000) {
    stopEffect();
    updateEffectStatus('Limit 15 s: výstupy vypnuty, dosažená poloha zachována. Zkontrolujte mechanismus a potvrďte servisní reset.');
    return;
  }
  const now = Number.isFinite(timestamp) ? timestamp : deadlineNow;
  const phase = effect.phases[effect.index];
  if (effect.started === null) {
    effect.started = now; effect.initial = [...trayAngles];
    playTone(phase.audio ? phase.tone_hz : 0);
  }
  const fraction = Math.min(1,(now-effect.started)/Math.max(1,phase.duration_ms));
  updateEffectTimeline(effect.index,fraction);
  const indexes = targetIndexes(phase.target,effect.selected,fraction);
  lightStates.fill(null);
  for (const index of indexes) {
    if (index < 0 || index >= trayAngles.length) continue;
    lightStates[index] = (phase.light || [0,0,0]).map(value=>Math.min(255,value)*(model.effects.max_brightness || 64)/(255*255));
    if (phase.motion) {
      const destination = phase.motion === 'raise' ? model.mechanism.travel_degrees : 0;
      trayAngles[index] = effect.initial[index]+(destination-effect.initial[index])*fraction;
    }
  }
  el('lift').value = trayAngles[+el('tray').value-1];
  el('effect-phase').textContent = phase.phase+(phase.vibration ? ' · vibrace vybrané přihrádky (náhled)' : '');
  if (fraction >= 1) {
    effect.index++; effect.started = null; stopTone();
    if (effect.index >= effect.phases.length) {
      updateEffectTimeline(effect.phases.length,0);
      effect = null; lightStates.fill(null); el('lift').disabled = false;
      updateEffectControls();
      updateEffectStatus('Ilustrační sekvence dokončena. Náhled není zkouška fyzických výstupů.');
    }
  }
  requestDraw(); if (effect) requestAnimationFrame(tickEffect);
}
for (let index=1; index<=trayAngles.length; index++) el('tray').add(new Option('Přihrádka '+index,String(index)));
el('tray').value = '3';
for (const route of routes) el('wire').add(new Option(route.id+' · '+route.from+' → '+route.to,route.id));
for (const endpoint of [...new Set([...Object.keys(model.endpoints || {}),...routes.flatMap(route=>[route.from,route.to,route.transport_from])])].filter(Boolean).sort()) el('endpoint').add(new Option(endpoint,endpoint));
el('mode').value = mode; el('wire-scope').value = 'branch'; el('case-opacity').value = 1;
el('envelope-note').textContent = 'Meze návrhu (nejsou zaručený volný kvádr): '+(model.case?.safe_zone || [400,240,170]).join(' × ')+' mm. Zdvih '+model.mechanism.lift+' mm, '+trayAngles.length+' modulů. Korpus není určen k tisku.';
updateObjectOptions(); buildTree(); updateAssembly(); updateTransportInstructions(); updateEffectControls(); showEffectTimeline(model.effects?.main || []); draw();
if (typeof DigitalTwin !== 'undefined') twinViewer = DigitalTwin.mount(document, model, {
  clear:() => { trayAngles.fill(0); lightStates.fill(null); twinOutputStates.clear(); pinned = null; requestDraw(); },
  activate:() => {
    effect = null; stopTone(); lightStates.fill(null); twinOutputStates.clear();
    el('lift').disabled = true; el('service-reset').disabled = true;
    updateEffectControls(); updateEffectStatus('Digital Twin: polohy a LED pouze ze snapshotu. Návrhový přehrávač je pozastaven.');
    requestDraw();
  },
  deactivate:() => {
    twinOutputStates.clear();
    el('lift').disabled = mode === 'transport' || mode === 'assembly' || effectFault;
    el('service-reset').disabled = mode !== 'service'; updateEffectControls();
    updateEffectStatus('Návrhový náhled bez runtime snapshotu.'); requestDraw();
  },
  apply:(angles,grb,reported = {}) => {
    angles.forEach((angle,index) => { trayAngles[index] = angle ?? 0; });
    const outputs = DigitalTwin.reportedOutputs(model.twinManifest, reported);
    twinOutputStates.clear();
    for (const name of outputs.vibrationMeshes) twinOutputStates.set(name, 'vibration-commanded');
    for (const name of outputs.audioMeshes) twinOutputStates.set(name, outputs.audioKind);
    for (let slot=0; slot<6; slot++) {
      const pixels = model.twinManifest?.slots[slot]?.channels;
      if (!pixels || grb.length !== 108) { lightStates[slot] = null; continue; }
      const sum = [0,0,0];
      for (let pixel=0; pixel<pixels.ledCount; pixel++) {
        const offset = (pixels.ledStart+pixel)*3;
        sum[0] += grb[offset+1]; sum[1] += grb[offset]; sum[2] += grb[offset+2];
      }
      lightStates[slot] = sum.map(value => value/(255*pixels.ledCount));
    }
    requestDraw();
  },
  select:name => { pinned = meshesByName.get(name) || null; inspectEnabled = true; clearWireSelection(); setWorkshopPanel('inspector',true); requestDraw(); },
});
window.addEventListener('resize',requestDraw);
window.visualViewport?.addEventListener('resize',requestDraw);
if (typeof ResizeObserver !== 'undefined') new ResizeObserver(requestDraw).observe(canvas);
reducedMotion?.addEventListener('change',()=>{ if (cameraTransition) { cameraTarget = cameraTransition.target; distance = cameraTransition.distance; cameraTransition = null; requestDraw(); } });
window.F2 = {draw,focusMeshes,assemblyPartState,meshes:model.meshes.length,lift,movement,visible,routePoints,setMode,setView,moveAssembly,startEffect,stopEffect,
  projectPoint,visibleCorners,
  state:()=>({mode,visualMode,yaw,pitch,activePointers:activePointers.size,cameraTransition:!!cameraTransition,trayAngles:[...trayAngles],assemblyIndex,effectFault,selectedWire,selectedEndpoint,pinned:pinned?.name || '',automaticFit,distance,cameraTarget:[...cameraTarget]}),routes:routes.map(route=>route.id)};
window.F1 = window.F2;
