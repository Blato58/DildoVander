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
const trayAngles = Array(model.mechanism.count || 6).fill(0);
const routes = model.routes || [];
const routesById = new Map(routes.map(route => [route.id,route]));
const hiddenMeshes = new Set(), hiddenGroups = new Set();
const assemblyGuide = model.assemblyGuide || {steps:[]};
let mode = 'operation', assemblyIndex = -1, isolateName = null;
let yaw = -1.05, pitch = .66, distance = 1050, drag = null;
let automaticFit = true;
let cameraTarget = [(model.case?.inside_x || 431.8)/2,(model.case?.inside_y || 266.7)/2,65];
let dragDistance = 0;
let shiftDown = false, inspectEnabled = false, hovered = null, pinned = null, pointer = null;
let projection, view, frameRequested = false;
let selectedWire = '', selectedEndpoint = '';
let hoveredConnection = null;
let effect = null, effectFault = false;
const lightStates = Array(trayAngles.length).fill(null);
function inspecting() { return shiftDown || inspectEnabled; }
function matchesPart(name, part) {
  return name === part || name.startsWith(part.endsWith('*') ? part.slice(0,-1) : part.endsWith('_') ? part : part + '_');
}
function installedParts() { return assemblyGuide.steps.slice(0,assemblyIndex+1).flatMap(step => step.parts || []); }
function newlyInstalled(mesh) {
  return mode === 'assembly' && assemblyGuide.steps[assemblyIndex]?.parts.some(part => matchesPart(mesh.name,part));
}
function visible(mesh) {
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
  if (/^(tray_|u059_|led_|wand_|roller_|follower_|soft_liner_|tray_foam_|tray_fabric_)/.test(mesh.name)) return {type:'lift',index};
  return null;
}
function movement(mesh) {
  if (mesh.group.startsWith('lid')) return rotX(-el('lid').value*Math.PI/180, model.hinge);
  const motion = motionFor(mesh), angle = motion ? trayAngles[motion.index-1] : 0;
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
    const translucent = !picking && ['case','lid'].includes(mesh.group) && +el('case-opacity').value < 1;
    gl.uniform1f(loc.opacity,translucent ? +el('case-opacity').value : 1);
    if (translucent) { gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA); gl.depthMask(false); }
    else { gl.disable(gl.BLEND); gl.depthMask(true); }
    gl.uniform3fv(loc.color, picking ? [(id & 255)/255, ((id >> 8) & 255)/255, ((id >> 16) & 255)/255] : effectColor || mesh.color);
    gl.uniform1f(loc.highlight, !picking && selected === mesh ? .65 : !picking && newlyInstalled(mesh) ? .45 : 0);
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
        label(route.id+' · pohyblivý konec, aproximace smyčky',points[neighbour][0]+5,points[neighbour][1]-16);
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
  // A hover card can appear under the pointer. Until pinned, canvas picking
  // must still receive the next click; the empty Inspect selector stays usable.
  el('inspector').style.pointerEvents = pinned || !mesh ? 'auto' : 'none';
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
  const edges = scopedRoutes(mesh);
  el('empty-connections').hidden = edges.length > 0;
  el('empty-connections').textContent = mesh.name.startsWith('spare_') ? 'Rezervní díl není zapojený.' : 'Pro tento díl není v podkladech přiřazeno kabelové spojení.';
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
  const ratio = devicePixelRatio || 1;
  const w = Math.max(1,Math.round(canvas.clientWidth*ratio)), h = Math.max(1,Math.round(canvas.clientHeight*ratio));
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
  gl.viewport(0,0,w,h); gl.enable(gl.DEPTH_TEST); gl.disable(gl.CULL_FACE); gl.disable(gl.BLEND);
  if (automaticFit) fitScene();
  const target = cameraTarget, fitDistance = distance;
  const eye = [target[0]+fitDistance*Math.cos(pitch)*Math.cos(yaw),target[1]+fitDistance*Math.cos(pitch)*Math.sin(yaw),target[2]+fitDistance*Math.sin(pitch)];
  projection = perspective(w/h); view = look(eye,target);
  gl.uniformMatrix4fv(loc.projection,false,projection); gl.uniformMatrix4fv(loc.view,false,view);
  hovered = inspecting() && !drag ? pick() : null;
  hoveredConnection = inspecting() && !drag && !selectedWire && !selectedEndpoint ? wireHit() : null;
  const selected = pinned || hovered;
  gl.clearColor(.937,.922,.894,1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  renderMeshes(false, hovered || selected);
  el('liftvalue').textContent = lift(trayAngles[+el('tray').value-1] || 0).toFixed(1)+' mm';
  el('lidvalue').textContent = el('lid').value+'°';
  el('inspect').setAttribute('aria-pressed', String(inspectEnabled));
  el('inspect-hint').hidden = !inspecting();
  el('inspect-hint').textContent = pinned ? `Připnuto: ${pinned.name}. Klik na jiný objekt změní výběr; klik do prázdna nebo Zrušit výběr jej zruší.` : selected ? 'Klik na objekt připne jeho kartu a umožní změnit barvu.' : 'Inspect: názvy všech objektů zapnutých vrstev. Najeďte na díl; Servis odkryje elektroniku.';
  canvas.style.cursor = inspecting() ? 'crosshair' : drag ? 'grabbing' : 'grab';
  updatePanel(selected);
  updateWireDetails();
  updateOverlay(selected);
}
function requestDraw() {
  if (frameRequested) return;
  frameRequested = true;
  requestAnimationFrame(() => { frameRequested = false; draw(); });
}
function layersChanged() { displayedMesh = undefined; updateObjectOptions(); requestDraw(); }
canvas.addEventListener('pointerdown', event => {
  event.preventDefault(); // Shift inspection must not extend browser text selection.
  pointer = [event.clientX,event.clientY];
  if (inspecting()) {
    draw();
    const hit = wireHit();
    selectedWire = hit?.route || ''; selectedEndpoint = hit?.endpoint || '';
    pinned = hit ? null : hovered;
    el('wire').value = selectedWire; el('endpoint').value = selectedEndpoint;
    displayedMesh = undefined; updateWireDetails(); requestDraw();
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
  event.preventDefault(); automaticFit = false;
  distance = Math.max(75,Math.min(8000,distance*Math.exp(event.deltaY*.001))); requestDraw();
}, {passive:false});
el('inspector').addEventListener('pointerenter', () => {
  if (!pinned) return;
  pointer = null; requestDraw();
});
function clearWireSelection() {
  selectedWire = ''; selectedEndpoint = ''; hoveredConnection = null;
  el('wire').value = ''; el('endpoint').value = '';
}
function closeInspect() { shiftDown = false; inspectEnabled = false; pinned = null; hovered = null; clearWireSelection(); requestDraw(); }
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
  if (effect || effectFault || mode === 'transport' || mode === 'assembly') return;
  trayAngles[+el('tray').value-1] = +el('lift').value; requestDraw();
};
el('tray').onchange = () => { el('lift').value = trayAngles[+el('tray').value-1]; requestDraw(); };
for (const id of ['cover','case','beds','hardware']) el(id).oninput = layersChanged;
el('open').onclick = () => setMode('operation');
el('service').onclick = () => setMode('service');
el('closed').onclick = () => setMode('transport');
el('mode').onchange = () => setMode(el('mode').value);
for (const id of ['wands','bank','textile','wires','dimensions','case-opacity']) el(id).oninput = layersChanged;
for (const name of ['top','front','side','overview']) el('view-'+name).onclick = () => setView(name);
el('view-reset').onclick = resetView;
el('hide-object').onclick = () => { if (pinned) hiddenMeshes.add(pinned.name); layersChanged(); };
el('isolate-object').onclick = () => { isolateName = pinned?.name || null; layersChanged(); };
el('show-all').onclick = () => { hiddenMeshes.clear(); hiddenGroups.clear(); isolateName = null; buildTree(); layersChanged(); };
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
el('service-reset').onclick = () => {
  if (mode !== 'service') return;
  effect = null; effectFault = false; trayAngles.fill(0); lightStates.fill(null);
  el('effect-phase').textContent = '';
  el('lift').disabled = false; el('lift').value = 0;
  updateEffectControls();
  updateEffectStatus('Servisní reset modelu. Fyzická poloha musí být ručně zkontrolována.'); requestDraw();
};
function resetView() {
  automaticFit = true; setView('overview');
}
function setView(name) {
  const views = {top:[-Math.PI/2,Math.PI/2-.001],front:[-Math.PI/2,.01],side:[0,.01],overview:[-1.05,.66]};
  [yaw,pitch] = views[name] || views.overview; requestDraw();
}
function zoomObject(mesh) {
  automaticFit = false;
  cameraTarget = transform(movement(mesh),[...mesh.center,1]).slice(0,3);
  distance = Math.max(75,Math.max(...[0,1,2].map(i=>mesh.bounds[i+3]-mesh.bounds[i]))*4); requestDraw();
}
function buildTree() {
  el('tree').replaceChildren();
  const titles = {case:'Dřevěný kufr',lid:'Víko',fixed:'Rám a nosiče',moving:'Mechanismy',moving_hw:'Pohyblivé díly',hardware:'Elektronika a spoje',cover:'Kryty',fabric:'Textil',lid_fabric:'Textil víka',bond:'Lepené spoje',fastener:'Spojovací materiál',wand:'Vyjímatelné hůlky',restraint:'Přepravní zajištění'};
  for (const group of [...new Set(model.meshes.map(mesh=>mesh.group))]) {
    const section = document.createElement('details'), summary = document.createElement('summary');
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = !hiddenGroups.has(group);
    checkbox.setAttribute('aria-label','Zobrazit vrstvu '+(titles[group] || group));
    checkbox.onchange = () => { checkbox.checked ? hiddenGroups.delete(group) : hiddenGroups.add(group); layersChanged(); };
    summary.append(checkbox);
    const text = document.createElement('span'); text.textContent = ' '+(titles[group] || group); summary.append(text); section.append(summary);
    for (const mesh of model.meshes.filter(item=>item.group === group)) {
      const button = document.createElement('button'); button.textContent = mesh.name;
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
  mode = value; el('mode').value = value;
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
  if (transport || mode === 'assembly') trayAngles.fill(0);
  el('lift').value = trayAngles[+el('tray').value-1] || 0;
  el('lift').disabled = transport || mode === 'assembly' || effectFault;
  el('service-reset').disabled = !service;
  el('mode-note').textContent = transport ? 'Ruční přepravní sestava: všech šest pásků, prázdná lůžka, odpojená banka, parkovaný USB kabel. Stav pásků nelze elektronicky potvrdit.' : 'Jde o náhled sestavy. Přepnutí režimu nepohybuje fyzickým mechanismem.';
  if (mode === 'assembly') assemblyIndex = -1;
  if (!effectFault) {
    el('effect-phase').textContent = '';
    updateEffectStatus('Časování zdvihu je ilustrační do kalibrace. Lokální poslech tónů není fyzická přejímka.');
  }
  updateEffectControls();
  buildTree(); updateAssembly(); updateTransportInstructions(); layersChanged(); updateWireDetails();
}
function materialLabel(id) {
  const material = assemblyGuide.materialCatalog?.[id];
  return typeof material === 'string' ? material : material?.label || id;
}
function updateAssembly() {
  el('assembly-panel').hidden = mode !== 'assembly';
  const step = assemblyGuide.steps[assemblyIndex];
  el('assembly-title').textContent = step ? (assemblyIndex+1)+'/'+assemblyGuide.steps.length+' · '+step.title : 'Montáž od začátku';
  el('assembly-text').textContent = step?.instruction || (assemblyGuide.steps.length ? 'Pokračujte prvním krokem. Viditelné díly se odvozují z dokončených kroků.' : 'Montážní data nejsou dostupná.');
  el('assembly-check').textContent = step?.check || '';
  el('assembly-parts').textContent = step?.partSummary ? 'Díly v tomto kroku:\n'+(Array.isArray(step.partSummary) ? step.partSummary.join('\n') : step.partSummary) : '';
  el('assembly-materials').textContent = step ? 'Materiál:\n'+(step.materials || []).map(item=>'• '+item.quantity+' × '+materialLabel(item.id)).join('\n')+'\n\nNástroje:\n'+(step.tools || []).map(tool=>'• '+tool).join('\n') : '';
  el('assembly-depends').textContent = step?.depends?.length ? 'Předchůdci: '+step.depends.join(', ') : '';
  el('assembly-prev').disabled = assemblyIndex < 0; el('assembly-next').disabled = assemblyIndex >= assemblyGuide.steps.length-1;
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
function moveAssembly(delta) {
  if (mode !== 'assembly' || effectFault) return;
  assemblyIndex = Math.max(-1,Math.min(assemblyGuide.steps.length-1,assemblyIndex+delta));
  const step = assemblyGuide.steps[assemblyIndex]; if (step) setView(step.camera);
  updateAssembly(); layersChanged();
}
function updateWireDetails() {
  const entries = scopedRoutes(pinned || hovered);
  const endpoint = selectedEndpoint || hoveredConnection?.endpoint;
  const pin = model.endpoints?.[endpoint];
  const pinNote = pin ? '\nPin '+endpoint+': '+pin.position_status+'; fyzický terminál '+pin.physical_terminal : '';
  el('wire-details').textContent = entries.length ? entries.map(route=>route.id+': '+route.from+' → '+route.to+'; síť '+route.net+'; svazek '+route.bundle+'. '+(route.note || '')+((mode === 'transport' || !el('bank').checked) && route.transport_from ? ' Fyzický konec: '+route.transport_from+' — '+route.transport_connection : '')).join('\n')+pinNote+'\nPiny jsou návrhové obálky, smyčky zobrazují pouze aproximaci flexibility; nejde o změřené souřadnice.' : 'Vyberte vodič, pin nebo díl. Větev je konkrétní vodič; svazek a síť rozšíří výběr podle společných ID.'+pinNote;
}
function drawDimensions() {
  const c = model.case || {inside_x:431.8,inside_y:266.7,base_height:114.3};
  for (const [start,end,text] of [
    [[0,0,0],[c.inside_x,0,0],'Vnitřní X '+c.inside_x+' mm'],
    [[0,0,0],[0,c.inside_y,0],'Vnitřní Y '+c.inside_y+' mm'],
    [[0,0,0],[0,0,c.base_height],'Spodní Z '+c.base_height+' mm'],
  ]) {
    const a = projectPoint(start), b = projectPoint(end); if (!a || !b) continue;
    svgElement('line',{x1:a[0],y1:a[1],x2:b[0],y2:b[1],stroke:'#b79852','stroke-width':2});
    label(text,(a[0]+b[0])/2,(a[1]+b[1])/2);
  }
}
function drawOrientation() {
  const c = model.case || {inside_x:431.8,inside_y:266.7};
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
function updateEffectControls() {
  el('play-main').disabled = mode !== 'operation' || effectFault || !!effect;
  el('play-short').disabled = mode !== 'operation' || effectFault || !!effect;
  el('effect-stop').disabled = !effect;
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
  if (mode !== 'operation' || effectFault || effect) return;
  const phases = model.effects?.[kind];
  if (!Array.isArray(phases) || !phases.length) { updateEffectStatus('Sdílená data efektů nejsou dostupná.'); return; }
  if (el('audio-preview').checked && !audioContext && window.AudioContext) audioContext = new window.AudioContext();
  audioContext?.resume()?.catch(() => updateEffectStatus('Lokální zvuk není dostupný; vizuální sekvence pokračuje bez zvuku.'));
  effect = {phases,index:0,started:null,presentationStarted:null,selected:+el('tray').value-1,initial:[...trayAngles]};
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
  const now = Number.isFinite(timestamp) ? timestamp : Date.now();
  if (effect.presentationStarted === null) effect.presentationStarted = now;
  // Match the firmware's whole-cycle limit even after a suspended browser frame.
  if (now < effect.presentationStarted || now-effect.presentationStarted >= 15000) {
    stopEffect();
    updateEffectStatus('Limit 15 s: výstupy vypnuty, dosažená poloha zachována. Zkontrolujte mechanismus a potvrďte servisní reset.');
    return;
  }
  const phase = effect.phases[effect.index];
  if (effect.started === null) {
    effect.started = now; effect.initial = [...trayAngles];
    playTone(phase.audio ? phase.tone_hz : 0);
  }
  const fraction = Math.min(1,(now-effect.started)/Math.max(1,phase.duration_ms));
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
el('envelope-note').textContent = 'Bezpečný vložitelný kvádr: '+(model.case?.safe_zone || [400,240,170]).join(' × ')+' mm. Zdvih '+model.mechanism.lift+' mm, '+trayAngles.length+' modulů. Korpus není určen k tisku.';
updateObjectOptions(); buildTree(); updateAssembly(); updateTransportInstructions(); updateEffectControls(); draw();
window.addEventListener('resize',requestDraw);
window.F2 = {draw,meshes:model.meshes.length,lift,movement,visible,routePoints,setMode,setView,moveAssembly,startEffect,stopEffect,
  projectPoint,visibleCorners,
  state:()=>({mode,trayAngles:[...trayAngles],assemblyIndex,effectFault,selectedWire,selectedEndpoint,pinned:pinned?.name || '',automaticFit,distance,cameraTarget:[...cameraTarget]}),routes:routes.map(route=>route.id)};
window.F1 = window.F2;
