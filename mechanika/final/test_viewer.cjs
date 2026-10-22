/* Exercise the real viewer event handlers; rendering hits are supplied by a fake GPU. */
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const designTokens = JSON.parse(fs.readFileSync(path.join(__dirname,'../../design-system/tokens.json'),'utf8'));

class Element {
  constructor(document = null) {
    this.document = document; this.parent = null; this.rendered = true;
    this.value = ''; this.checked = true; this.hidden = true;
    this.children = []; this.style = {}; this.dataset = {}; this.listeners = {}; this.attributes = {};
    this.clientWidth = 800; this.clientHeight = 600; this.width = 800; this.height = 600;
    this.offsetWidth = 100; this.offsetHeight = 22;
  }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  fire(name, event = {}) { this.listeners[name]?.({preventDefault() {}, ...event}); }
  setAttribute(name, value) { this.attributes[name] = value; }
  set innerHTML(value) { this.textContent = value.replace(/<[^>]*>/g, ''); }
  getAttribute(name) { return name === 'data-theme' ? this.dataset.theme : this.attributes[name]; }
  append(node) { node.parent = this; this.children.push(node); }
  add(node) { this.append(node); }
  replaceChildren(...nodes) { this.children = []; for (const node of nodes) this.append(node); }
  contains(node) { return this === node || this.children.some(child => child === node || child.contains?.(node)); }
  getClientRects() {
    if (!this.rendered || (this.panelDrawer && this.attributes['data-open'] !== 'true')) return [];
    if (this.parent && !this.parent.getClientRects().length) return [];
    return [this.getBoundingClientRect()];
  }
  focus(options) { this.document.activeElement = this; this.focusOptions = options; }
  getBoundingClientRect() { return {left:0,top:0,width:800,height:600}; }
  setPointerCapture() {}
}

function viewer(routes = [], extraMeshes = [], data = {}) {
  const elements = new Map(), frames = [], geometryUploads = [], backgroundColors = [], selectionColors = [], shaderSources = [];
  let document;
  let labelMeasurements = 0, clockNow = 0, audioStarts = 0;
  const get = id => { if (!elements.has(id)) elements.set(id, new Element(document)); return elements.get(id); };
  get('lid').value = '105'; get('lift').value = '0';
  if (data.viewport) [get('view').clientWidth,get('view').clientHeight]=data.viewport;
  if (data.canvasRect) get('view').getBoundingClientRect=()=>data.canvasRect;
  for (const name of ['wires','dimensions','audio-preview']) get(name).checked = false;
  let hit = 0;
  const constants = new Map();
  const gl = new Proxy({
    getShaderParameter: () => true, getProgramParameter: () => true,
    getAttribLocation: () => 0, getUniformLocation: (_program, name) => name,
    shaderSource: (_shader, source) => shaderSources.push(source),
    uniform3fv: (location, value) => { if (location === 'selectionColor') selectionColors.push([...value]); },
    checkFramebufferStatus: () => gl.FRAMEBUFFER_COMPLETE,
    clearColor: (...color) => backgroundColors.push(color),
    readPixels: (x,y,w,h,format,type,pixel) => { pixel[0] = hit; },
    bufferData: (target,data) => geometryUploads.push(Array.from(data)),
  }, {get(target,key) {
    if (key in target) return target[key];
    if (key === key.toUpperCase()) {
      if (!constants.has(key)) constants.set(key, constants.size+1);
      return constants.get(key);
    }
    return () => ({});
  }});
  get('view').getContext = () => gl;
  const meshes = [...['pca9685','esp32'].map(name => ({name,group:'hardware'})),...extraMeshes].map(item => ({
    ...item,color:[.1,.4,.2],bounds:item.bounds || [100,100,20,110,110,30],data:item.data ?? (item.vertices ? undefined : [0,0,0,0,0,1]),
    info:{title:item.name,description:item.name,details:[],source:'test'},
  }));
  function createElement() {
    const node = new Element(document);
    for (const [property,value] of [['offsetWidth',100],['offsetHeight',22]]) {
      Object.defineProperty(node,property,{get() {
        if (node.className === 'object-name') labelMeasurements++;
        return value;
      }});
    }
    return node;
  }
  document = {
    activeElement:null, documentElement:new Element(),
    querySelector:() => get('theme-meta'), getElementById:get, createElement, createElementNS:() => new Element(document),
    listeners:{}, addEventListener(name, callback) { this.listeners[name] = callback; },
  };
  // Model only the ancestry/visibility needed by keyboard focus; layout stays fake.
  for (const element of elements.values()) element.document = document;
  get('sidebar').append(get('tree')); get('sidebar').append(get('explorer-close'));
  get('assembly-panel').append(get('guide-close')); get('motion-panel').append(get('motion-close'));
  get('inspector-rail').append(get('object')); get('inspector-rail').append(get('inspector-close'));
  for (const id of ['sidebar','inspector-rail']) get(id).panelDrawer = !!data.mobilePanels;
  for (const id of ['explorer-toggle','inspector-toggle','explorer-close','inspector-close']) get(id).rendered = !!data.mobilePanels;
  const window = {matchMedia:()=>({matches:!!data.reducedMotion,addEventListener(){}}),location:data.location,listeners:{},addEventListener(name,callback) { this.listeners[name] = callback; }};
  window.AudioContext = class {
    constructor() { clockNow+=data.audioSetupDelayMs || 0; }
    resume() { return Promise.resolve(); }
    createGain() { return {gain:{value:0},connect() {}}; }
    createOscillator() {
      return {frequency:{value:0},connect() {},start() { audioStarts++; },stop() {}};
    }
  };
  const context = {
    document, window, devicePixelRatio:data.devicePixelRatio || 1,
    getComputedStyle: () => ({ getPropertyValue: name => {
      const theme = designTokens.themeRegistry[document.documentElement.dataset.theme];
      const palette = designTokens.themes[theme.palette];
      const resolve = value => value.replace(/var\(--aw-([a-z-]+)\)/g, (_match, key) => resolve(palette[key] || designTokens.primitives[key]));
      return resolve(palette[name.replace('--aw-', '')]);
    } }),
    performance:{now:()=>clockNow},
    model:{meshes,routes,sources:[],hinge:[0,0,0],mechanism:{count:6,origin_y:21,origin_z:12,cam_y:112,cam_z:36,dwell_degrees:20,travel_degrees:160,lift:16},...data},
    localStorage:{getItem:key => key === 'dildovander.theme' ? data.theme || null : null,setItem() {}},
    Option:class {constructor(text,value) {this.textContent = text; this.value = value;}},
    requestAnimationFrame:callback => frames.push(callback),
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../design-system/theme.js'),'utf8'), context);
  require('./ui_test_runtime.cjs')(context);
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'viewer_engine_core.js'),'utf8'), context);
  context.F2GraphicsCore=context.window.F2GraphicsCore;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'viewer.js'),'utf8'), context);
  const flush = () => { let count=0; while (frames.length) { if (++count>1000) throw new Error('Animation did not finish'); clockNow+=100; frames.shift()(clockNow); } };
  const frame = (timestamp,liveNow=timestamp) => { if (Number.isFinite(liveNow)) clockNow=liveNow; const pending = frames.splice(0); for (const callback of pending) callback(timestamp); };
  const hover = id => { hit = id; get('view').fire('pointermove',{clientX:400,clientY:300,shiftKey:true}); flush(); };
  const click = () => { get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1}); flush(); };
  const lights = () => vm.runInNewContext('lightStates.map(color=>color && [...color])', context);
  return {get,hover,click,flush,frame,document,window,meshes,geometryUploads,backgroundColors,selectionColors,shaderSources,api:window.F2,lights,
    now:()=>clockNow,advanceTo:timestamp=>{clockNow=timestamp;},audioStarts:()=>audioStarts,measurements:()=>labelMeasurements};
}

function route(id='wire-A', net='MAIN5', bundle='rear') {
  return {id,from:'esp32:3V3',to:'pca9685:VCC',kind:'power',net,bundle,
    points:[[100,100,25],[120,160,30],[160,160,30],[170,110,25]],note:'pin coordinates unmeasured'};
}

test('clicked card remains pinned crossing another part and entering the panel', () => {
  const app = viewer();
  app.hover(1); app.click();
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  app.hover(2);
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  app.get('inspector').fire('pointerenter'); app.flush();
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  app.get('color').value = '#112233'; app.get('color').oninput(); app.flush();
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  app.get('unpin').onclick(); app.flush(); app.hover(2);
  assert.equal(app.get('mesh-id').textContent,'esp32');
});

test('Shift over empty space keeps only the pinned object until an empty click unpins it', () => {
  const app = viewer();
  app.hover(1); app.click(); app.hover(0);
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  assert.equal(app.get('object-details').hidden,false);
  assert.equal(app.get('names').children.length,0,'Pinned inspection does not show all labels');
  app.click();
  assert.equal(app.get('object-details').hidden,true,'Empty click unpins the card');
  assert.deepEqual(app.get('names').children.map(node=>node.textContent).sort(),['esp32','pca9685']);
  app.hover(2); app.click();
  assert.equal(app.get('mesh-id').textContent,'esp32');
});

test('releasing Shift keeps pin during orbit; empty click clears it', () => {
  const app = viewer([route()]);
  app.hover(1); app.click();
  app.document.listeners.keyup({key:'Shift'}); app.flush();
  const line = () => app.get('overlay').children.find(node=>node.attributes['data-wire-id'] === 'wire-A' && node.attributes.points);
  const before = line().attributes.points;
  app.get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1});
  app.get('view').fire('pointermove',{clientX:450,clientY:320,shiftKey:false});
  app.get('view').fire('pointerup',{clientX:450,clientY:320}); app.flush();
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  assert.notEqual(line().attributes.points,before);
  app.hover(0); app.document.listeners.keyup({key:'Shift'}); app.flush();
  app.get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1});
  app.get('view').fire('pointerup',{clientX:400,clientY:300}); app.flush();
  assert.equal(app.get('inspector').hidden,true);
});

test('routes preserve each actual waypoint, IDs and pin endpoints without substitute curves', () => {
  const wires = [route('power'),{...route('signal','SDA'),kind:'signal',points:[[102,100,25],[122,160,32],[162,160,32],[172,110,25]]}];
  const app = viewer(wires); app.hover(1); app.click();
  const paths = app.get('overlay').children.filter(node=>node.attributes.points);
  assert.equal(paths.length,2);
  for (const node of paths) {
    assert.equal(node.attributes.points.split(' ').length,4);
    assert.equal(node.attributes['data-from'],'esp32:3V3');
    assert.equal(node.attributes['data-to'],'pca9685:VCC');
    assert.equal(node.attributes.d,undefined,'No quadratic centre curve');
  }
  assert.notEqual(paths[0].attributes.points,paths[1].attributes.points);
  assert.equal(app.get('connections').children.length,2);
});

test('six independently controlled trays share exact cam law and fixed neighbours stay fixed', () => {
  const extras = Array.from({length:6},(_,i)=>({name:'tray_'+(i+1),group:'moving'}));
  const app = viewer([],extras);
  for (let index=1; index<=6; index++) {
    app.get('tray').value = String(index); app.get('tray').onchange();
    app.get('lift').value = '80'; app.get('lift').oninput(); app.flush();
    assert.equal(app.api.movement(app.meshes.find(mesh=>mesh.name === 'tray_'+index))[14],8);
  }
  assert.deepEqual(Array.from(app.api.state().trayAngles),[80,80,80,80,80,80]);
  app.get('tray').value='4'; app.get('lift').value='160'; app.get('lift').oninput(); app.flush();
  assert.equal(app.api.movement(app.meshes.find(mesh=>mesh.name==='tray_4'))[14],16);
  assert.equal(app.api.movement(app.meshes.find(mesh=>mesh.name==='tray_3'))[14],8);
  assert.equal(app.api.lift(20),0); assert.equal(app.api.lift(140),16);
});

test('moving cable endpoint follows lift while fixed trunk stays fixed; absent bank uses parked route', () => {
  const wire = {...route('LED1','LED_DATA','motion-1'),to:'led_1:DIN',points:[[100,100,20],[120,100,20],[130,100,30]]};
  const bank = {...route('USB'),from:'powerbank:C1',transport_points:[[30,120,25],[30,105,25],[55,95,25]]};
  const app=viewer([wire,bank],[{name:'led_1',group:'moving_hw'},{name:'powerbank',group:'hardware'}]);
  app.get('tray').value='1'; app.get('lift').value='160'; app.get('lift').oninput(); app.flush();
  const points=app.api.routePoints(wire);
  assert.deepEqual(Array.from(points[0]),wire.points[0]); assert.deepEqual(Array.from(points[1]),wire.points[1]);
  assert.deepEqual(Array.from(points[2]),[130,100,46]);
  app.get('bank').checked=false; app.get('bank').oninput(); app.flush();
  assert.deepEqual(Array.from(app.api.routePoints(bank),point=>Array.from(point)),bank.transport_points);
  assert.ok(app.api.routes.includes('USB'),'Removing bank preserves topology');
});

test('rolling return deforms half a lift while its fixed trunk and other module stay fixed', () => {
  const wire={...route('rolling'),points:[[100,76,38],[100,68,20],[100,60,59],[117,27,71]],
    point_lift_weights:{'1':[0,.5,1,1],'2':[0,0,0,0]}};
  const app=viewer([wire],[{name:'tray_left_1',group:'moving'},{name:'tray_left_2',group:'moving'}]);
  app.get('tray').value='1'; app.get('lift').value='160'; app.get('lift').oninput(); app.flush();
  assert.deepEqual(Array.from(app.api.routePoints(wire),p=>Array.from(p)),
    [[100,76,38],[100,68,28],[100,60,75],[117,27,87]]);
  app.get('tray').value='2'; app.get('lift').value='160'; app.get('lift').oninput(); app.flush();
  assert.deepEqual(Array.from(app.api.routePoints(wire),p=>Array.from(p)),
    [[100,76,38],[100,68,28],[100,60,75],[117,27,87]]);
});

test('pin, branch, bundle and net scopes resolve stable IDs even after hiding the selected part', () => {
  const wires=[route('one','VCC','a'),{...route('two','SDA','a'),from:'esp32:SDA'},
    {...route('three','VCC','b'),from:'other:VCC',to:'third:VCC'}];
  const app=viewer(wires);
  const ids=()=>app.get('overlay').children.filter(node=>node.attributes.points).map(node=>node.attributes['data-wire-id']).sort();
  app.get('wire').value='one'; app.get('wire').onchange(); app.flush();
  assert.deepEqual(ids(),['one']);
  app.get('wire-scope').value='bundle'; app.get('wire-scope').onchange(); app.flush();
  assert.deepEqual(ids(),['one','two']);
  app.get('wire-scope').value='net'; app.get('wire-scope').onchange(); app.flush();
  assert.deepEqual(ids(),['one','three']);
  app.get('wire-scope').value='branch'; app.get('endpoint').value='esp32:SDA'; app.get('endpoint').onchange(); app.flush();
  assert.deepEqual(ids(),['two']);
  app.get('object').value='esp32'; app.get('object').onchange(); app.flush();
  app.get('hardware').checked=false; app.get('hardware').oninput(); app.flush();
  assert.equal(app.get('mesh-id').textContent,'esp32'); assert.deepEqual(ids(),['one','two']);
});

test('Shift hover and click on an actual terminal marker resolves the exact endpoint', () => {
  const app=viewer([route('terminal')]);
  app.get('wires').checked=true; app.get('wires').oninput(); app.flush();
  app.hover(0);
  const marker=app.get('overlay').children.find(node=>node.attributes['data-endpoint'] === 'esp32:3V3');
  assert.ok(marker);
  const x=+marker.attributes.cx,y=+marker.attributes.cy;
  app.get('view').fire('pointermove',{clientX:x,clientY:y,shiftKey:true}); app.flush();
  assert.match(app.get('wire-details').textContent,/terminal: esp32:3V3/);
  app.get('view').fire('pointerdown',{clientX:x,clientY:y,pointerId:1}); app.flush();
  assert.equal(app.api.state().selectedEndpoint,'esp32:3V3');
  assert.equal(app.api.state().selectedWire,'terminal');
});

test('floating hover card cannot intercept a pin click; explicit wire selection hides the mesh card', () => {
  const wire={...route('D2-2','DRIVE5','power'),from:'degson_2:2'};
  const app=viewer([wire],[],{
    viewport:[453,545],devicePixelRatio:1.25,
    canvasRect:{left:275,top:107.6,width:453,height:545.2},
  });
  app.hover(1);
  assert.equal(app.get('inspector').hidden,false);
  assert.equal(app.get('inspector').style.pointerEvents,'none','Unpinned hover card must pass clicks to canvas');
  app.get('inspector').fire('pointerenter'); app.flush();
  assert.equal(app.api.state().pinned,'','Entering hover card must not silently pin its mesh');
  app.get('wire').value='D2-2'; app.get('wire').onchange(); app.flush();
  assert.equal(app.get('inspector').hidden,true,'Selected wire details belong to the sidebar');
  app.get('inspect').onclick(); app.flush();
  const marker=app.get('overlay').children.find(node=>node.attributes['data-endpoint']==='degson_2:2');
  const clientX=275+(+marker.attributes.cx),clientY=107.6+(+marker.attributes.cy);
  app.get('view').fire('pointermove',{clientX,clientY,shiftKey:true}); app.flush();
  app.get('view').fire('pointerdown',{clientX,clientY,shiftKey:true,pointerId:1}); app.flush();
  assert.equal(app.api.state().selectedWire,'D2-2');
  assert.equal(app.api.state().selectedEndpoint,'degson_2:2');
  assert.equal(app.api.state().pinned,'','A wire click must not pin the underlying mesh');
  assert.equal(app.get('inspector').hidden,true);
  app.get('object').value='esp32'; app.get('object').onchange(); app.flush();
  assert.equal(app.get('inspector').hidden,false);
  assert.equal(app.get('inspector').style.pointerEvents,'auto','Explicit mesh selection exposes interactive controls');
});

test('assembly advances cumulatively, goes back and restarts without stale visibility', () => {
  const guide={steps:[
    {id:'frame',title:'Frame',instruction:'Install frame',parts:['frame_'],materials:[{id:'M4',quantity:4}],tools:['key'],check:'Check fit',camera:'top',wires:[],depends:[]},
    {id:'tray',title:'Tray',instruction:'Install tray',parts:['tray_1'],materials:[],tools:[],check:'Check motion',camera:'side',wires:['wire-A'],depends:['frame']},
  ]};
  const app=viewer([route()],[{name:'frame_front',group:'fixed'},{name:'tray_1',group:'moving'}],{assemblyGuide:guide});
  const part=name=>app.meshes.find(mesh=>mesh.name===name);
  app.api.setMode('assembly'); app.flush();
  assert.equal(app.api.visible(part('frame_front')),false);
  app.api.moveAssembly(1); app.flush();
  assert.equal(app.api.visible(part('frame_front')),true); assert.equal(app.api.visible(part('tray_1')),false);
  assert.match(app.get('assembly-materials').textContent,/4 × M4/);
  app.api.moveAssembly(1); app.flush(); assert.equal(app.api.visible(part('tray_1')),true);
  app.api.moveAssembly(-1); app.flush(); assert.equal(app.api.visible(part('tray_1')),false);
  app.get('assembly-restart').onclick(); app.flush(); assert.equal(app.api.visible(part('frame_front')),false);
  app.api.setMode('operation'); app.flush(); assert.equal(app.api.visible(part('tray_1')),true);
});

test('transport has six real ties, removes contents and resets all mechanism angles deterministically', () => {
  const extras=[{name:'powerbank',group:'hardware'},{name:'wand_1',group:'wand'},{name:'tray_fabric_1',group:'fabric'},
    ...Array.from({length:6},(_,i)=>({name:'bed_transport_tie_'+(i+1),group:'restraint',transportOnly:true}))];
  const app=viewer([],extras);
  app.get('lift').value='160'; app.get('lift').oninput(); app.flush(); app.api.setMode('transport'); app.flush();
  assert.deepEqual(Array.from(app.api.state().trayAngles),[0,0,0,0,0,0]);
  for (const name of ['powerbank','wand_1','tray_fabric_1']) assert.equal(app.api.visible(app.meshes.find(mesh=>mesh.name===name)),false);
  assert.equal(app.meshes.filter(mesh=>mesh.transportOnly && app.api.visible(mesh)).length,6);
  app.api.setMode('service'); app.flush(); app.api.setMode('operation'); app.flush();
  assert.equal(app.api.visible(app.meshes.find(mesh=>mesh.name==='wand_1')),true);
  assert.equal(app.meshes.filter(mesh=>mesh.transportOnly && app.api.visible(mesh)).length,0);
});

test('Stop freezes intermediate lift and disables further output until explicit service recovery', () => {
  const effects={max_brightness:64,main:[{phase:'Raise',duration_ms:1000,target:'selected',motion:'raise',light:[255,0,0]}]};
  const app=viewer([],[{name:'tray_3',group:'moving'}],{effects});
  app.api.startEffect('main'); app.frame(0); app.frame(500);
  assert.equal(app.api.state().trayAngles[2],80);
  app.api.stopEffect(); app.frame(1000); app.flush();
  assert.equal(app.api.state().trayAngles[2],80); assert.equal(app.api.state().effectFault,true);
  app.get('lift').value='0'; app.get('lift').oninput(); app.flush(); assert.equal(app.api.state().trayAngles[2],80);
  app.api.setMode('service'); app.flush(); assert.equal(app.api.state().trayAngles[2],80);
  app.get('service-reset').onclick(); app.flush(); assert.equal(app.api.state().effectFault,false); assert.equal(app.api.state().trayAngles[2],0);
});

test('authored main and short scores illuminate at most one segment for every selection', () => {
  const effects=JSON.parse(fs.readFileSync(path.join(__dirname,'effects.json'),'utf8'));
  for (const variant of ['main','short']) {
    for (let selected=1; selected<=6; selected++) {
      const app=viewer([],[],{effects});
      app.get('tray').value=String(selected); app.get('tray').onchange(); app.flush();
      const admitted=app.now();
      app.api.startEffect(variant); app.frame(admitted);
      const litIndexes=()=>Array.from(app.lights()).flatMap((color,index)=>color?.some(value=>value>0) ? [index] : []);
      assert.deepEqual(litIndexes(),[selected-1],'Wake lights only the selected compartment');
      for (let timestamp=100; timestamp<=20000; timestamp+=100) {
        app.frame(admitted+timestamp);
        assert.ok(litIndexes().length<=1,`${variant}, selection ${selected}, ${timestamp} ms`);
      }
      assert.equal(app.get('effect-stop').disabled,true,'The complete score finishes');
      assert.equal(app.api.state().effectFault,false,'The authored score completes before the hard deadline');
    }
  }
});

test('whole-cycle deadline stops before a late animation frame can move or complete a tray', () => {
  const effects={max_brightness:64,main:[
    {phase:'Raise',duration_ms:1000,target:'selected',motion:'raise',light:[255,0,0]},
    {phase:'Wait',duration_ms:14000,target:'selected',light:[255,0,0]},
    {phase:'Return',duration_ms:1000,target:'selected',motion:'lower',light:[255,0,0]},
  ]};
  const app=viewer([],[{name:'tray_3',group:'moving'}],{effects});
  app.api.startEffect('main'); app.frame(0); app.frame(1000); app.frame(1100); app.frame(14999);
  assert.equal(app.api.state().effectFault,false);
  assert.equal(app.api.state().trayAngles[2],160);
  app.frame(15000); app.frame(17000);
  assert.equal(app.api.state().effectFault,true);
  assert.equal(app.api.state().trayAngles[2],160,'Deadline must not initiate a return');
  assert.ok(Array.from(app.lights()).every(color=>color===null));
  assert.equal(app.get('play-main').disabled,true);
  assert.match(app.get('effect-status').textContent,/Limit 15 s/);
});

function admissionDeadlineViewer(audioSetupDelayMs=0) {
  const phase={phase:'Raise',duration_ms:1000,target:'selected',motion:'raise',light:[255,0,0],audio:'local_tone',tone_hz:880};
  const app=viewer([],[{name:'tray_3',group:'moving'}],{audioSetupDelayMs,effects:{max_brightness:64,main:[phase],short:[phase]}});
  app.get('audio-preview').checked=true;
  app.advanceTo(5000);
  return app;
}

test('admission deadline faults before outputs when the first frame arrives at or after 15 seconds', () => {
  for (const variant of ['main','short']) {
    for (const delay of [15000,16000]) {
      const app=admissionDeadlineViewer();
      app.get('play-'+variant).onclick();
      assert.equal(app.get('effect-stop').disabled,false,'The click admits the sequence before rAF');
      app.frame(5000+delay);
      assert.equal(app.api.state().effectFault,true,`${variant}, first frame delayed ${delay} ms`);
      assert.deepEqual(Array.from(app.api.state().trayAngles),[0,0,0,0,0,0]);
      assert.ok(Array.from(app.lights()).every(color=>color===null));
      assert.equal(app.audioStarts(),0,'Expired first frame cannot start the local tone');
      assert.equal(app.get('effect-phase').textContent,undefined,'No phase entered or completed');
      assert.match(app.get('effect-status').textContent,/Limit 15 s/);
    }
  }
});

test('a first frame just before the admission deadline has only the remaining budget', () => {
  const app=admissionDeadlineViewer();
  app.get('play-main').onclick();
  app.frame(19990);
  assert.equal(app.api.state().effectFault,false);
  assert.equal(app.audioStarts(),1);
  app.frame(19995);
  assert.ok(app.api.state().trayAngles[2]>0 && app.api.state().trayAngles[2]<160);
  const frozen=Array.from(app.api.state().trayAngles);
  app.frame(20000); app.frame(21000);
  assert.equal(app.api.state().effectFault,true);
  assert.deepEqual(Array.from(app.api.state().trayAngles),frozen,'The first frame cannot reset the deadline');
  assert.ok(Array.from(app.lights()).every(color=>color===null));
});

test('audio initialization delay counts from admission before the first frame', () => {
  const app=admissionDeadlineViewer(15000);
  app.get('play-main').onclick();
  assert.equal(app.now(),20000);
  app.frame(app.now());
  assert.equal(app.api.state().effectFault,true);
  assert.equal(app.audioStarts(),0);
  assert.equal(app.api.state().trayAngles[2],0);
  assert.ok(Array.from(app.lights()).every(color=>color===null));
});

test('missing rAF timestamp uses the admission performance clock and rollback still faults', () => {
  const app=admissionDeadlineViewer();
  app.get('play-main').onclick();
  app.advanceTo(5100); app.frame(NaN);
  assert.equal(app.api.state().effectFault,false);
  app.advanceTo(5600); app.frame(NaN);
  assert.equal(app.api.state().trayAngles[2],80,'Fallback uses the same monotonic domain');
  app.frame(4999);
  assert.equal(app.api.state().effectFault,true);
  assert.equal(app.api.state().trayAngles[2],80,'A clock before admission cannot move the tray');
});

test('live expiry stops a callback with a stale frame timestamp before motion or completion', () => {
  const app=admissionDeadlineViewer();
  app.get('play-main').onclick();
  app.frame(5000);
  assert.equal(app.audioStarts(),1);
  app.frame(19999,20000);
  assert.equal(app.api.state().effectFault,true,'Live elapsed time expires even if the rAF timestamp has not');
  assert.equal(app.api.state().trayAngles[2],0,'The expired callback cannot finish the raise');
  assert.ok(Array.from(app.lights()).every(color=>color===null));
  assert.equal(app.audioStarts(),1,'The callback starts no new tone');
  assert.match(app.get('effect-status').textContent,/Limit 15 s/);
});

test('a first shared frame timestamp before admission does not falsely fault a live callback', () => {
  const app=admissionDeadlineViewer();
  app.get('play-main').onclick();
  app.frame(4999,5001);
  assert.equal(app.api.state().effectFault,false);
  assert.equal(app.audioStarts(),1);
  assert.equal(app.api.state().trayAngles[2],0);
  app.frame(5499,5501);
  assert.equal(app.api.state().trayAngles[2],80);
  assert.equal(app.api.state().effectFault,false);
});

test('service recovery admits a fresh sequence on the continuing monotonic clock', () => {
  const app=admissionDeadlineViewer();
  app.get('play-main').onclick(); app.frame(20000);
  assert.equal(app.api.state().effectFault,true);
  app.api.setMode('service'); app.flush();
  app.get('service-reset').onclick(); app.flush();
  app.api.setMode('operation'); app.flush();
  const admitted=app.now();
  assert.ok(admitted>20000,'Draw flushes continue the same live clock');
  app.get('play-main').onclick();
  app.frame(admitted+10); app.frame(admitted+510);
  assert.equal(app.api.state().effectFault,false);
  assert.equal(app.api.state().trayAngles[2],80);
  app.frame(admitted+1010);
  assert.equal(app.get('effect-stop').disabled,true);
  assert.equal(app.api.state().effectFault,false,'Fresh admission does not retain the expired deadline');
});

function partiallyRaisedSequence() {
  const effects={max_brightness:64,main:[{phase:'Raise',duration_ms:1000,target:'selected',motion:'raise',light:[255,0,0]}]};
  const app=viewer([],[
    ...Array.from({length:6},(_,index)=>({name:'tray_'+(index+1),group:'moving'})),
    ...Array.from({length:6},(_,index)=>({name:'bed_transport_tie_'+(index+1),group:'restraint',transportOnly:true})),
  ],{effects,assemblyGuide:{steps:[{id:'tray',title:'Tray',parts:['tray_1'],materials:[],tools:[],camera:'top'}]}});
  for (let index=1; index<=6; index++) {
    app.get('tray').value=String(index); app.get('lift').value=String(index*10); app.get('lift').oninput();
  }
  app.get('tray').value='3'; app.get('tray').onchange(); app.flush();
  const admitted=app.now();
  app.api.startEffect('main'); app.frame(admitted); app.frame(admitted+500);
  assert.deepEqual(Array.from(app.api.state().trayAngles),[10,20,95,40,50,60]);
  return app;
}

test('switching mode during motion stops into service without changing any angles or showing transport ties', () => {
  for (const requestedMode of ['transport','assembly','operation','service']) {
    const app=partiallyRaisedSequence(), frozen=Array.from(app.api.state().trayAngles);
    app.api.setMode(requestedMode); app.flush();
    assert.equal(app.api.state().mode,'service',requestedMode);
    assert.equal(app.get('mode').value,'service');
    assert.equal(app.api.state().effectFault,true);
    assert.deepEqual(Array.from(app.api.state().trayAngles),frozen);
    assert.ok(app.meshes.filter(mesh=>mesh.transportOnly).every(mesh=>!app.api.visible(mesh)));
    assert.equal(app.get('transport-panel').hidden,true);
    assert.equal(app.get('assembly-panel').hidden,true);
    assert.equal(app.get('lift').disabled,true);
    assert.equal(app.get('play-main').disabled,true);
    assert.match(app.get('effect-status').textContent,/servisní reset/);
  }
});

test('already stopped motion cannot be reset by mode, slider or assembly controls; explicit service reset releases it', () => {
  const app=partiallyRaisedSequence(), frozen=Array.from(app.api.state().trayAngles);
  app.api.stopEffect(); app.flush();
  for (const requestedMode of ['transport','assembly','operation']) {
    app.get('mode').value=requestedMode; app.get('mode').onchange(); app.flush();
    assert.equal(app.api.state().mode,'service');
    assert.equal(app.get('mode').value,'service');
    assert.deepEqual(Array.from(app.api.state().trayAngles),frozen);
  }
  app.get('lift').value='0'; app.get('lift').oninput();
  app.api.moveAssembly(1); app.get('assembly-restart').onclick(); app.flush();
  assert.equal(app.api.state().assemblyIndex,-1);
  assert.deepEqual(Array.from(app.api.state().trayAngles),frozen);
  assert.ok(app.meshes.filter(mesh=>mesh.transportOnly).every(mesh=>!app.api.visible(mesh)));
  app.get('service-reset').onclick(); app.flush();
  assert.equal(app.api.state().effectFault,false);
  assert.deepEqual(Array.from(app.api.state().trayAngles),[0,0,0,0,0,0]);
  app.api.setMode('transport'); app.flush();
  assert.equal(app.api.state().mode,'transport');
  assert.equal(app.meshes.filter(mesh=>mesh.transportOnly && app.api.visible(mesh)).length,6);
  app.api.setMode('assembly'); app.api.moveAssembly(1); app.flush();
  assert.equal(app.api.state().assemblyIndex,0);
  app.api.setMode('operation'); app.flush();
  assert.equal(app.get('play-main').disabled,false);
});

test('empty-space labels reuse DOM between pointer moves and refresh after view changes', () => {
  const app=viewer([],Array.from({length:230},(_,i)=>({name:'part_'+i,group:'hardware'})));
  app.hover(0); const first=app.get('names').children[0], measured=app.measurements();
  assert.equal(app.get('names').children.length,232);
  for(let i=0;i<20;i++) app.hover(0);
  assert.equal(app.get('names').children[0],first); assert.equal(app.measurements(),measured);
  app.get('view').fire('wheel',{deltaY:20,preventDefault(){}}); app.flush();
  assert.notEqual(app.get('names').children[0],first);
});

test('initial, standard and reset views fit every open-case corner on narrow canvases', () => {
  const app=viewer([],[
    {name:'wood_case',group:'case',bounds:[-9.1,-9.1,-6,440.9,300.9,114.3]},
    {name:'wood_lid',group:'lid',bounds:[-9.1,-9.1,114.3,440.9,300.9,194]},
  ],{hinge:[0,266.7,114.3],viewport:[453,480]});
  function fullyInFrame() {
    for (const corner of app.api.visibleCorners()) {
      const point=app.api.projectPoint(corner);
      assert.ok(point,'Every corner must be in front of camera');
      assert.ok(point[0]>=0 && point[0]<=app.get('view').clientWidth,'Horizontal corner must fit');
      assert.ok(point[1]>=0 && point[1]<=app.get('view').clientHeight,'Vertical corner must fit');
    }
  }
  fullyInFrame();
  for (const view of ['top','front','side','overview']) { app.api.setView(view); app.flush(); fullyInFrame(); }
  app.get('view').clientWidth=280; app.get('view').clientHeight=600;
  app.window.listeners.resize(); app.flush(); fullyInFrame();
  app.get('view').fire('wheel',{deltaY:-100,preventDefault(){}}); app.flush();
  const zoom=app.api.state().distance;
  app.get('view').clientWidth=350; app.window.listeners.resize(); app.flush();
  assert.equal(app.api.state().distance,zoom,'Resize preserves deliberate user zoom');
  app.get('view-reset').onclick(); app.flush();
  assert.equal(app.api.state().automaticFit,true); fullyInFrame();
});

test('changing modes clears stale object/wire/endpoint selections and restores explicit controls', () => {
  const app=viewer([route()]);
  app.get('object').value='esp32'; app.get('object').onchange(); app.flush();
  app.get('wire').value='wire-A'; app.get('wire').onchange(); app.flush();
  app.get('wire-scope').value='net'; app.get('wire-scope').onchange(); app.flush();
  app.get('hide-object').onclick(); app.flush();
  app.api.setMode('service'); app.flush();
  assert.equal(app.api.state().pinned,''); assert.equal(app.api.state().selectedWire,'');
  assert.equal(app.get('wire').value,''); assert.equal(app.get('wire-scope').value,'branch');
  assert.equal(app.get('play-main').disabled,true); assert.equal(app.get('wires').checked,true);
  app.api.setMode('operation'); app.flush();
  assert.equal(app.get('play-main').disabled,false); assert.equal(app.get('wires').checked,false);
  assert.equal(app.get('wands').checked,true); assert.equal(app.get('bank').checked,true); assert.equal(app.get('textile').checked,true);
  assert.ok(app.meshes.every(app.api.visible),'Hidden parts must be restored by mode');
  assert.equal(app.get('effect-stop').disabled,true);
});

test('transport and recommission instructions and Czech assembly material labels come from shared data', () => {
  const guide={
    transport:['Vypnout výstupy a napájení.','Vyjmout banku, hůlky a textil.','Zajistit šest prázdných lůžek.','Zaparkovat USB a zavřít kufr.'],
    recommission:['Otevřít kufr a vyjmout šest pásků.','Zkontrolovat dráhy, připojit banku a provést omezený test.'],
    materialCatalog:{M4:'šroub M4 × 12'},
    steps:[{id:'frame',title:'Rám',instruction:'Přišroubovat',parts:[],partSummary:['4 × kotva','1 × rám'],materials:[{id:'M4',quantity:4}],tools:['klíč'],check:'Zkontrolovat',camera:'top'}],
  };
  const app=viewer([],[],{assemblyGuide:guide});
  app.api.setMode('transport'); app.flush();
  assert.equal(app.get('transport-panel').hidden,false);
  assert.deepEqual(app.get('transport-instructions').children.map(item=>item.textContent),guide.transport);
  assert.deepEqual(app.get('recommission-instructions').children.map(item=>item.textContent),guide.recommission);
  app.api.setMode('assembly'); app.api.moveAssembly(1); app.flush();
  assert.equal(app.get('transport-panel').hidden,true);
  assert.match(app.get('assembly-materials').textContent,/4 × šroub M4 × 12/);
  assert.match(app.get('assembly-parts').textContent,/4 × kotva/);
  assert.deepEqual(app.get('orientation').children.map(node=>node.textContent),['PŘEDEK · ovládání','ZÁDA · panty','LEVÁ','PRAVÁ']);
});

test('workshop drawers expose and clear state; object selection opens inspector without changing geometry', () => {
  const app=viewer();
  app.get('explorer-toggle').onclick(); app.flush();
  assert.equal(app.get('sidebar').attributes['data-open'],'true');
  assert.equal(app.get('explorer-toggle').attributes['aria-expanded'],'true');
  app.get('inspector-toggle').onclick(); app.flush();
  assert.equal(app.get('inspector-rail').attributes['data-open'],'true');
  assert.equal(app.get('sidebar').attributes['data-open'],'false');
  app.get('inspector-close').onclick(); app.flush();
  assert.equal(app.get('inspector-rail').attributes['data-open'],'false');
  app.get('object').value='esp32'; app.get('object').onchange(); app.flush();
  assert.equal(app.get('inspector-rail').attributes['data-open'],'true');
  app.document.listeners.keydown({key:'Escape'}); app.flush();
  assert.equal(app.get('inspector-rail').attributes['data-open'],'false');
  assert.equal(app.api.state().pinned,'');
  assert.equal(app.api.state().mode,'operation');
});

test('mobile panel close and Escape restore focus to the visible corresponding toggle', () => {
  const app=viewer([],[],{mobilePanels:true});
  app.get('explorer-toggle').onclick(); app.flush();
  app.get('explorer-close').focus(); app.get('explorer-close').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('explorer-toggle'));
  assert.equal(app.get('explorer-toggle').focusOptions.preventScroll,true);
  app.get('inspector-toggle').onclick(); app.flush();
  app.get('object').focus(); app.document.listeners.keydown({key:'Escape'}); app.flush();
  assert.equal(app.document.activeElement,app.get('inspector-toggle'));
  assert.equal(app.get('inspector-toggle').focusOptions.preventScroll,true);
  app.get('explorer-toggle').onclick(); app.flush();
  app.get('inspector-toggle').focus(); app.get('explorer-close').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('inspector-toggle'),'Closing a panel cannot steal outside focus');
  app.get('explorer-toggle').onclick(); app.flush();
  app.get('explorer-close').focus(); app.get('explorer-toggle').rendered=false;
  app.get('explorer-close').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('explorer-close'),'Never transfer focus to a hidden toggle');
});

test('mobile tree selection transfers focus to Inspector; opening Explorer transfers it back', () => {
  const app=viewer([],[],{mobilePanels:true});
  app.get('explorer-toggle').onclick(); app.flush();
  const treeButton=app.get('tree').children[0].children.find(node=>typeof node.onclick === 'function');
  treeButton.focus(); treeButton.onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('inspector-close'));
  assert.equal(app.get('inspector-close').focusOptions.preventScroll,true);
  assert.equal(app.get('sidebar').getClientRects().length,0);
  app.get('object').focus(); app.get('explorer-toggle').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('explorer-close'));
  assert.equal(app.get('explorer-close').focusOptions.preventScroll,true);
  assert.equal(app.get('inspector-rail').getClientRects().length,0);
});

test('desktop docked panels preserve keyboard focus on selection, switching and Escape', () => {
  const app=viewer();
  const treeButton=app.get('tree').children[0].children.find(node=>typeof node.onclick === 'function');
  treeButton.focus(); treeButton.onclick(); app.flush();
  assert.equal(app.document.activeElement,treeButton,'The still-visible Explorer retains focus');
  app.get('object').focus(); app.get('explorer-toggle').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('object'),'A still-visible Inspector retains focus');
  treeButton.focus();
  app.document.listeners.keydown({key:'Escape'}); app.flush();
  assert.equal(app.document.activeElement,treeButton);
});

test('timeline follows shared effect durations and retains elapsed progress when Stop freezes motion', () => {
  const phases=[{phase:'Raise',duration_ms:1000,target:'selected',motion:'raise',light:[255,0,0]},
    {phase:'Lower',duration_ms:1000,target:'selected',motion:'lower',light:[0,0,0]}];
  const app=viewer([],[{name:'tray_3',group:'moving'}],{effects:{main:phases,max_brightness:64}});
  assert.equal(app.get('effect-timeline').children.length,2);
  assert.equal(app.get('timeline-time').textContent,'0.0 / 2.0 s');
  app.api.startEffect('main'); app.frame(0); app.frame(500);
  assert.equal(app.get('timeline-time').textContent,'0.5 / 2.0 s');
  assert.equal(app.get('effect-timeline').children[0].attributes['aria-current'],'step');
  app.api.stopEffect(); app.flush();
  assert.equal(app.get('timeline-time').textContent,'0.5 / 2.0 s');
  assert.equal(app.api.state().trayAngles[2],80);
  app.api.setMode('service'); app.get('service-reset').onclick(); app.flush();
  assert.equal(app.get('timeline-time').textContent,'0.0 / 2.0 s');
});

test('offline home resolves inside complete package while hosted viewer links to project root', () => {
  for (const location of [{protocol:'file:',hostname:''},{protocol:'http:',hostname:'127.0.0.1',pathname:'/viewer.html'}]) {
    const app=viewer([],[],{location});
    assert.equal(app.get('workshop-home').attributes.href,'./reference-sources.html');
  }
  const app=viewer([],[],{location:{protocol:'https:',hostname:'example.test'}});
  assert.equal(app.get('workshop-home').attributes.href,'/');
  const localSite=viewer([],[],{location:{protocol:'http:',hostname:'127.0.0.1',pathname:'/mechanika/final/exports/viewer.html'}});
  assert.equal(localSite.get('workshop-home').attributes.href,'/');
});

test('workshop template retains all source viewer IDs once and exposes self-contained stylesheet placeholders', () => {
  const template=fs.readFileSync(path.join(__dirname,'viewer-template.html'),'utf8');
  const ids=[...template.matchAll(/\bid="([^"]+)"/g)].map(match=>match[1]);
  const uiSource=fs.readFileSync(path.join(__dirname,'viewer_ui.js'),'utf8');
  ids.push(...[...uiSource.matchAll(/id: '([^']+)'/g)].map(match=>match[1]));
  ids.push(...[...uiSource.matchAll(/\['([^']+)', '[^']+', '(?:operation|service|transport|assembly)'\]/g)].map(match=>match[1]));
  assert.equal(new Set(ids).size,ids.length,'Duplicate DOM IDs break viewer handlers');
  const script=fs.readFileSync(path.join(__dirname,'viewer.js'),'utf8');
  for (const match of script.matchAll(/el\('([^']+)'\)/g)) assert.ok(ids.includes(match[1]),'Missing element '+match[1]);
  assert.match(template,/href="\.\/F2-kouzelny-kufr.zip" download/);
  assert.match(template,/__WORKSHOP_DESIGN_SYSTEM__/);
  assert.match(template,/__WORKSHOP_VIEWER_STYLE__/);
  assert.doesNotMatch(template,/<link[^>]+stylesheet/);
});


test('two touch pointers pan and pinch without rotating, transition to single orbit and cancel cleanly', () => {
  const app=viewer([],[],{viewport:[360,600]});
  const view=app.get('view'), before=app.api.state();
  view.fire('pointerdown',{pointerId:1,pointerType:'touch',clientX:100,clientY:200});
  view.fire('pointerdown',{pointerId:2,pointerType:'touch',clientX:200,clientY:200});
  view.fire('pointermove',{pointerId:1,pointerType:'touch',clientX:90,clientY:210});
  view.fire('pointermove',{pointerId:2,pointerType:'touch',clientX:230,clientY:210}); app.flush();
  const multi=app.api.state();
  assert.equal(multi.yaw,before.yaw); assert.equal(multi.pitch,before.pitch);
  assert.ok(multi.distance<before.distance,'Increasing finger distance zooms in');
  assert.notDeepEqual(multi.cameraTarget,before.cameraTarget);
  view.fire('pointerup',{pointerId:2,pointerType:'touch',clientX:230,clientY:210});
  view.fire('pointermove',{pointerId:1,pointerType:'touch',clientX:110,clientY:210}); app.flush();
  assert.notEqual(app.api.state().yaw,multi.yaw);
  view.fire('pointercancel',{pointerId:1}); app.flush();
  assert.equal(app.api.state().activePointers,0);
  const canceled=app.api.state();
  view.fire('pointermove',{pointerId:1,pointerType:'touch',clientX:1000,clientY:1000}); app.flush();
  assert.equal(app.api.state().yaw,canceled.yaw);
  assert.equal(app.api.state().pinned,'','A gesture cannot become an accidental tap');
});

test('desktop right button pans, camera target and zoom remain bounded and Home recovers whole model', () => {
  const app=viewer(), view=app.get('view'), before=app.api.state();
  view.fire('pointerdown',{pointerId:1,button:2,clientX:400,clientY:300});
  view.fire('pointermove',{pointerId:1,clientX:8000,clientY:-8000}); app.flush();
  assert.equal(app.api.state().yaw,before.yaw); assert.notDeepEqual(app.api.state().cameraTarget,before.cameraTarget);
  assert.ok(app.api.state().cameraTarget.every(value=>Math.abs(value)<1000));
  view.fire('pointerup',{pointerId:1,button:2,clientX:8000,clientY:-8000});
  for(let i=0;i<100;i++) view.fire('wheel',{deltaY:100000}); app.flush();
  assert.ok(app.api.state().distance<=1600);
  view.fire('keydown',{key:'Home'}); app.flush();
  assert.equal(app.api.state().automaticFit,true);
  assert.deepEqual(app.api.state().cameraTarget,before.cameraTarget);
});

test('camera focus ends on selected part, preserves orientation and reduced motion uses no transition', () => {
  for(const reducedMotion of [false,true]) {
    const app=viewer([],[],{reducedMotion,viewport:[360,500],devicePixelRatio:4});
    const before=app.api.state();
    app.get('object').value='esp32'; app.get('object').onchange(); app.flush();
    app.get('zoom-object').onclick();
    assert.equal(app.api.state().cameraTransition,!reducedMotion);
    app.flush();
    assert.equal(app.api.state().cameraTransition,false);
    assert.deepEqual(Array.from(app.api.state().cameraTarget),[105,105,25]);
    assert.equal(app.api.state().yaw,before.yaw);
    assert.equal(app.api.state().pitch,before.pitch);
    assert.equal(app.get('view').width,720,'Pixel ratio is capped at two');
  }
});

test('all named camera views fit the scene and remain finite', () => {
  const app=viewer([],[{name:'case',group:'case',bounds:[0,0,0,432,267,114]}],{viewport:[360,500]});
  for(const name of ['front','back','left','side','top','overview']) {
    app.api.setView(name); app.flush();
    assert.ok(Number.isFinite(app.api.state().distance));
    for(const corner of app.api.visibleCorners()) {
      const point=app.api.projectPoint(corner); assert.ok(point);
      assert.ok(point[0]>=0 && point[0]<=360); assert.ok(point[1]>=0 && point[1]<=500);
    }
  }
});

test('camera label follows actual orientation and free orbit clears preset state', () => {
  const app=viewer();
  app.api.setView('front'); app.flush();
  assert.equal(app.get('view-name').textContent,'Zepředu');
  assert.equal(app.get('view-front').getAttribute('aria-pressed'),'true');
  app.get('view').fire('keydown',{key:'ArrowLeft'}); app.flush();
  assert.equal(app.get('view-name').textContent,'Volný pohled');
  assert.equal(app.get('view-front').getAttribute('aria-pressed'),'false');
  app.get('view').fire('keydown',{key:'Home'}); app.flush();
  assert.equal(app.get('view-name').textContent,'Izometrie');
});

test('visible recovery restores mode geometry without changing assembly, overlays or fault', () => {
  const steps=[{title:'Bench deck',parts:['deck'],installation:{kind:'bench'},location:'Work table',direction:'From above',partSummary:'One deck'}];
  const app=viewer([],[{name:'deck',group:'fixed'},{name:'case',group:'case'}],{assemblyGuide:{steps}});
  app.api.setMode('assembly'); app.api.moveAssembly(1); app.flush();
  app.get('wires').checked=true; app.get('dimensions').checked=true;
  app.get('case').checked=false; app.get('case').oninput();
  app.get('object').value='deck'; app.get('object').onchange();
  app.get('isolate-object').onclick(); app.get('hide-object').onclick(); app.flush();
  assert.equal(app.get('restore-view').hidden,false);
  assert.match(app.get('selection-title').textContent,/skrytý díl/);
  app.get('restore-view').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('view'),'Recovery returns focus before its action disappears');
  assert.equal(app.api.state().assemblyIndex,0);
  assert.equal(app.get('case').checked,true);
  assert.equal(app.api.visible(app.meshes.find(mesh=>mesh.name==='case')),false,'Bench truth survives visibility recovery');
  assert.equal(app.api.visible(app.meshes.find(mesh=>mesh.name==='deck')),true);
  assert.equal(app.get('wires').checked,true); assert.equal(app.get('dimensions').checked,true);
  assert.equal(app.get('restore-view').hidden,true);
  app.get('guide-close').onclick(); app.flush();
  assert.equal(app.get('assembly-current').hidden,false);
  assert.match(app.get('assembly-current-title').textContent,/1 \/ 1 · Bench deck/);
  assert.match(app.get('assembly-current-context').textContent,/Na stole mimo kufr · Work table · From above/);
  assert.match(app.get('assembly-current-parts').textContent,/One deck/);
  app.api.setMode('service'); app.get('cover').checked=true; app.get('show-all').onclick(); app.flush();
  assert.equal(app.get('cover').checked,false,'Service recovery retains exposed electronics');
  assert.equal(+app.get('case-opacity').value,.2);
  assert.equal(app.get('assembly-current').hidden,true);

  const motion=viewer([],[],{effects:{main:[{duration_ms:1000,target:'selected',motion:'raise'}]}});
  motion.api.startEffect('main'); motion.frame(0); motion.frame(300); motion.api.stopEffect();
  const angles=Array.from(motion.api.state().trayAngles);
  motion.get('case').checked=false; motion.get('restore-view').onclick(); motion.flush();
  assert.equal(motion.api.state().effectFault,true);
  assert.deepEqual(Array.from(motion.api.state().trayAngles),angles);
});

test('guide picker supports variable steps and bench context, metadata, next state and destination', () => {
  const steps=[
    {id:'deck',title:'Deck on bench',parts:['deck'],instruction:'Wire outside case',subassembly:'electronics',location:'bench',direction:'from above',access:['driver top'],connectors:['USB slack'],removal:'Disconnect USB',installation:{kind:'bench',targets:['esp32'],clearance_mm:60,absent:['frame']},tools:['key'],materials:[]},
    {id:'place',title:'Lower deck',parts:['frame'],depends:['deck'],installation:{kind:'vertical'},tools:[],materials:[]},
  ];
  const app=viewer([],[{name:'deck',group:'fixed'},{name:'frame',group:'fixed'},{name:'case',group:'case'}],{assemblyGuide:{steps}});
  const part=name=>app.meshes.find(mesh=>mesh.name===name);
  app.api.setMode('assembly'); app.get('assembly-step').value='0'; app.get('assembly-step').onchange(); app.flush();
  assert.match(app.get('assembly-context').textContent,/PRÁCE NA STOLE/);
  assert.equal(app.api.visible(part('case')),false,'Bench preview does not falsely place modules inside case');
  assert.equal(app.api.visible(part('deck')),true);
  assert.match(app.get('assembly-access').textContent,/driver top/);
  assert.match(app.get('assembly-connectors').textContent,/USB slack/);
  assert.match(app.get('assembly-removal').textContent,/Disconnect USB/);
  assert.match(app.get('assembly-depends').textContent,/frame/);
  assert.equal(app.api.assemblyPartState(part('deck')),'current');
  assert.equal(app.api.assemblyPartState(part('frame')),'next');
  assert.match(app.get('assembly-next-parts').textContent,/Lower deck/);
  app.get('assembly-step').value='1'; app.get('assembly-step').onchange(); app.flush();
  assert.equal(app.api.visible(part('case')),true); assert.equal(app.api.visible(part('deck')),true);
  assert.equal(app.api.assemblyPartState(part('deck')),'installed');
  assert.equal(app.get('assembly-progress').value,2); assert.equal(app.get('assembly-next').disabled,true);
  app.get('assembly-exit').onclick(); app.flush(); assert.equal(app.api.state().mode,'operation');
  assert.equal(app.get('assembly-panel').getAttribute('data-open'),'false','Hidden guide must not reserve viewport area after exit');
});

test('instruction and simulation sheets close with focus returned to their dock buttons', () => {
  const app=viewer([],[],{assemblyGuide:{steps:[{title:'Build',parts:[]}]}});
  app.api.setMode('assembly'); app.flush();
  assert.equal(app.get('guide-toggle').getAttribute('aria-expanded'),'true');
  app.get('guide-close').focus(); app.get('guide-close').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('guide-toggle'));
  app.get('guide-toggle').onclick(); app.flush();
  assert.equal(app.document.activeElement,app.get('guide-close'));
  app.api.setMode('operation'); app.get('motion-toggle').onclick(); app.flush();
  assert.equal(app.get('motion-panel').getAttribute('data-open'),'true');
  app.get('motion-close').focus(); app.document.listeners.keydown({key:'Escape'}); app.flush();
  assert.equal(app.document.activeElement,app.get('motion-toggle'));
});


test('Stop remains in compact dock when simulation sheet is collapsed', () => {
  const phase={id:'rise',duration_ms:1000,motion:'lift',light:'off',tone:null,brightness:0};
  const app=viewer([],[{name:'tray_3',group:'moving'}],{effects:{main:[phase]}});
  app.get('play-main').onclick();
  assert.equal(app.get('dock-effect-stop').hidden,false);
  app.get('motion-close').onclick();
  app.get('dock-effect-stop').onclick(); app.flush();
  assert.equal(app.api.state().effectFault,true);
  assert.equal(app.get('dock-effect-stop').hidden,true);
});


test('bench prepared parts are shown without entering cumulative installed state and wiring-only steps retain deck', () => {
  const steps=[
    {id:'deck',title:'Bench deck',parts:['deck'],materials:[{id:'SHIM05',quantity:4}],installation:{kind:'bench',targets:['deck']}},
    {id:'wire',title:'Wire deck',parts:[],installation:{kind:'bench',targets:[]}},
    {id:'prepare',title:'Prepare lift',parts:[],preparedParts:['tray_1'],installation:{kind:'bench',targets:['tray_1']}},
    {id:'inspect',title:'Inspect deck',parts:[],installation:{kind:'inspection',targets:[]}},
    {id:'install',title:'Install lift',parts:['tray_1'],installation:{kind:'vertical',targets:['tray_1']}},
  ];
  const app=viewer([],[{name:'deck',group:'fixed'},{name:'tray_1',group:'moving'},{name:'case',group:'case'}],{assemblyGuide:{steps,materialCatalog:{SHIM05:{name:'washer 0.5 mm',description:'level deck'}}}});
  const part=name=>app.meshes.find(mesh=>mesh.name===name);
  app.api.setMode('assembly'); app.api.moveAssembly(1); app.flush();
  assert.match(app.get('assembly-materials').textContent,/4 × washer 0.5 mm/);
  app.api.moveAssembly(1); app.flush();
  assert.equal(app.api.visible(part('deck')),true,'Wiring-only bench operation retains earlier deck');
  assert.equal(app.api.visible(part('case')),false);
  app.api.moveAssembly(1); app.flush();
  assert.equal(app.api.visible(part('tray_1')),true,'Prepared part bypasses cumulative installed filter only on bench');
  assert.equal(app.api.assemblyPartState(part('tray_1')),'prepared');
  assert.match(app.get('assembly-parts').textContent,/ještě neosazené/);
  assert.match(app.get('assembly-parts').textContent,/tray_1/);
  app.api.moveAssembly(1); app.flush();
  assert.equal(app.api.visible(part('tray_1')),false,'Preparing a part must not install it in subsequent non-bench steps');
  app.api.moveAssembly(1); app.flush();
  assert.equal(app.api.visible(part('tray_1')),true,'Actual install stage adds part cumulatively');
});


test('actual authored guide retains bench deck context and transfers it to case only at installation', () => {
  const guide=JSON.parse(fs.readFileSync(path.join(__dirname,'assembly-guide.json'),'utf8'));
  const app=viewer([], [
    {name:'case',group:'case'},{name:'frame_panel_1',group:'fixed'},
    {name:'carrier_pca9685',group:'fixed'},{name:'left_service_carrier',group:'fixed'},
    {name:'usb_coil_wrap_1',group:'restraint'},{name:'floor_anchor_front_left',group:'fixed'},
    {name:'base_1',group:'fixed'},{name:'guide_bolt_1_16',group:'fastener'},
    {name:'guide_washer_1_16',group:'hardware',bounds:[100,100,95,110,110,95.8]},
    {name:'guide_nut_1_16',group:'hardware',bounds:[100,100,95.8,110,110,99]},
    {name:'tray_1',group:'moving'},
  ],{assemblyGuide:guide});
  const part=name=>app.meshes.find(mesh=>mesh.name===name);
  const stage=id=>{
    const index=guide.steps.findIndex(step=>step.id===id); assert.notEqual(index,-1,'Actual guide stage '+id+' exists');
    app.get('assembly-step').value=String(index); app.get('assembly-step').onchange(); app.flush();
  };
  const shown=name=>app.api.visible(part(name));
  app.api.setMode('assembly');
  for(const id of ['frame','carriers','electronics','power','signals']) {
    stage(id);
    assert.equal(shown('case'),false,id+': bench operation is outside case');
    assert.equal(shown('frame_panel_1'),true,id+': complete supporting deck stays visible');
    if(id!=='frame') assert.equal(shown('carrier_pca9685'),true,id+': carrier context stays visible');
    if(id==='carriers') {
      assert.match(app.get('assembly-parts').textContent,/Levá kazeta: dva boční rámy a tři patra/);
      assert.match(app.get('assembly-parts').textContent,/Dva boční sloupky/);
      assert.doesNotMatch(app.get('assembly-parts').textContent,/\[object Object\]/);
    }
    if(['electronics','power','signals'].includes(id)) assert.equal(shown('pca9685'),true,id+': electronics stays on deck');
    if(['power','signals'].includes(id)) assert.equal(shown('usb_coil_wrap_1'),true,id+': prepared power context stays visible');
  }
  stage('anchors');
  assert.equal(shown('case'),true); assert.equal(shown('floor_anchor_front_left'),true);
  for(const name of ['frame_panel_1','carrier_pca9685','pca9685','base_1','tray_1']) {
    assert.equal(shown(name),false,'Bench-built '+name+' is not already inside case at anchors');
  }
  stage('bases');
  assert.equal(shown('frame_panel_1'),true); assert.equal(shown('base_1'),true);
  assert.equal(shown('guide_washer_1_16'),true);
  assert.equal(app.api.movement(part('guide_washer_1_16'))[14],0,'Retention stays above integral guide towers at authored Z95');
  stage('install');
  for(const name of ['case','floor_anchor_front_left','frame_panel_1','carrier_pca9685','pca9685','base_1','guide_washer_1_16']) assert.equal(shown(name),true,name+' is present after deck insertion');
  assert.equal(shown('tray_1'),false);
  assert.equal(app.api.assemblyPartState(part('frame_panel_1')),'current','Transferred deck is highlighted on real insertion stage');
  assert.equal(app.api.assemblyPartState(part('pca9685')),'current');
  stage('moving');
  assert.equal(shown('tray_1'),true); assert.equal(shown('frame_panel_1'),false);
  assert.equal(shown('guide_washer_1_16'),false,'Caps remain on deck, not in separate upper-group bench preview');
  stage('mechanisms');
  for(const name of ['case','frame_panel_1','pca9685','base_1','tray_1','guide_washer_1_16','guide_nut_1_16']) assert.equal(shown(name),true,name+' remains after upper groups are installed');
  assert.equal(app.api.movement(part('guide_washer_1_16'))[14],0,'Caps are restored at final authored height without invalid Z-79 displacement');
  assert.equal(app.api.movement(part('guide_nut_1_16'))[14],0);
});


test('indexed geometry expands to the same winding, positions and flat unit normals as legacy facets', () => {
  const vertices=[0,0,0, 2,0,0, 0,2,0, 0,0,2];
  const triangles=[0,1,2, 0,3,1, 0,2,3, 1,3,2];
  const diagonal=-1/Math.sqrt(3);
  const legacy=[
    0,0,0,0,0,1, 2,0,0,0,0,1, 0,2,0,0,0,1,
    0,0,0,0,1,0, 0,0,2,0,1,0, 2,0,0,0,1,0,
    0,0,0,1,0,0, 0,2,0,1,0,0, 0,0,2,1,0,0,
    2,0,0,diagonal,diagonal,diagonal, 0,0,2,diagonal,diagonal,diagonal, 0,2,0,diagonal,diagonal,diagonal,
  ];
  const indexed=viewer([], [{name:'indexed',group:'fixed',vertices,triangles}]);
  const expanded=viewer([], [{name:'legacy',group:'fixed',data:legacy}]);
  assert.deepEqual(indexed.geometryUploads[2],expanded.geometryUploads[2]);
  assert.equal(indexed.meshes[2].count,12);
  assert.equal(indexed.meshes[2].vertices,undefined,'Shared source positions are released after upload');
  assert.equal(indexed.meshes[2].triangles,undefined);
  for(let offset=0;offset<indexed.geometryUploads[2].length;offset+=6) {
    const normal=indexed.geometryUploads[2].slice(offset+3,offset+6);
    assert.ok(Math.abs(Math.hypot(...normal)-1)<1e-6);
  }
});

test('indexed repeated, collinear and subthreshold triangles are omitted without NaN geometry', () => {
  const vertices=[0,0,0, 1,0,0, 2,0,0, 0,1,0, 0,1e-13,0];
  const triangles=[0,0,1, 0,1,2, 0,1,4, 0,1,3];
  const app=viewer([], [{name:'degenerate',group:'fixed',vertices,triangles}]);
  assert.equal(app.meshes[2].count,3,'Only the well-formed face is uploaded');
  assert.equal(app.geometryUploads[2].length,18);
  assert.ok(app.geometryUploads[2].every(Number.isFinite));
  const empty=viewer([], [{name:'only_degenerate',group:'fixed',vertices,triangles:[0,0,1,0,1,2]}]);
  assert.equal(empty.meshes[2].count,0);
  assert.deepEqual(empty.geometryUploads[2],[]);
});

test('indexed geometry rejects malformed positions and indices with the part identifier', () => {
  for(const geometry of [
    {vertices:[0,0],triangles:[]},
    {vertices:[0,0,NaN],triangles:[]},
    {vertices:[0,0,0],triangles:[0,0]},
    {vertices:[0,0,0],triangles:[0,0,1]},
    {vertices:[0,0,0],triangles:[0,0,-1]},
    {vertices:[0,0,0],triangles:[0,0,.5]},
  ]) assert.throws(()=>viewer([], [{name:'bad_geometry',group:'fixed',...geometry}]),/bad_geometry/);
});


test('dimension fallbacks label 430 by 269 as clear top opening and safe zone as design bounds', () => {
  const app=viewer([],[{name:'case',group:'case',bounds:[-5,-20,-5,435,289,114.3]}]);
  app.get('dimensions').checked=true; app.get('dimensions').oninput(); app.flush();
  const labels=app.get('names').children.map(node=>node.textContent).filter(Boolean).join(' | ');
  assert.match(labels,/Průchozí horní X 430 mm/);
  assert.match(labels,/Průchozí horní Y 269 mm/);
  assert.match(app.get('envelope-note').textContent,/nejsou zaručený volný kvádr/);
  assert.doesNotMatch(app.get('envelope-note').textContent,/Bezpečný vložitelný/);
  const parameters=JSON.parse(fs.readFileSync(path.join(__dirname,'parameters.json'),'utf8'));
  const actual=viewer([],[],{case:parameters.case});
  assert.match(actual.get('envelope-note').textContent,/400 × 240 × 170 mm/);
});


test('part search matches names and descriptions without changing model visibility or selection', () => {
  const app = viewer([], [{name:'case_left',group:'case'}]);
  const initial = app.api.state();
  app.get('part-search').value = 'PCA'; app.get('part-search').oninput();
  assert.equal(app.get('part-count').textContent, '1 / 3');
  assert.equal(app.get('tree').children.length, 1);
  assert.equal(app.get('tree').children[0].open, true);
  assert.equal(app.get('tree').children[0].children[1].textContent, 'pca9685');
  assert.equal(app.get('search-empty').hidden, true);
  assert.equal(app.api.state().pinned, initial.pinned);
  assert.equal(app.api.state().visualMode, initial.visualMode);
  app.get('part-search').value = 'missing'; app.get('part-search').oninput();
  assert.equal(app.get('tree').children.length, 0);
  assert.equal(app.get('search-empty').hidden, false);
  app.get('part-search').value = ''; app.get('part-search').oninput();
  assert.equal(app.get('part-count').textContent, '3 dílů');
  assert.equal(app.get('tree').children.length, 2);
});

test('theme changes viewer background and selection without modifying physical material colors', () => {
  const app = viewer();
  app.hover(1); app.click();
  const pinnedId = app.get('mesh-id').textContent;
  const colors = app.meshes.map(mesh => [...mesh.color]);
  assert.equal(app.document.documentElement.getAttribute('data-theme'), 'lord-voldemort');
  app.get('theme-toggle').onclick(); app.flush();
  assert.equal(app.document.documentElement.getAttribute('data-theme'), 'olivander');
  assert.match(app.get('theme-toggle').getAttribute('aria-label'), /Mistr hůlek.*Stínový čaroděj/);
  assert.deepEqual(app.backgroundColors.at(-1), [27/255,36/255,48/255,1]);
  assert.deepEqual(app.selectionColors.at(-1), [184/255,192/255,1]);
  assert.deepEqual(app.meshes.map(mesh => [...mesh.color]), colors);
  assert.equal(app.get('mesh-id').textContent, pinnedId, 'Theme changes preserve pinned selection');
  app.get('theme-toggle').onclick(); app.flush();
  assert.equal(app.document.documentElement.getAttribute('data-theme'), 'lord-voldemort');
  assert.deepEqual(app.backgroundColors.at(-1), [15/255,15/255,17/255,1]);
  assert.deepEqual(app.selectionColors.at(-1), [96/255,106/255,204/255]);
  const fragment = app.shaderSources.find(source => source.includes('precision mediump float'));
  assert.match(fragment, /uniform vec3 color,selectionColor/);
  assert.match(fragment, /mix\(color\*l,selectionColor,highlight\)/);
  assert.doesNotMatch(fragment, /vec3\(\.722,\.753,1\.\)/);
  assert.equal(viewer([],[],{theme:'light'}).document.documentElement.getAttribute('data-theme'), 'olivander');
});

test('opening runtime tools clears competing mobile drawers and other tools can close runtime', () => {
  const app = viewer([],[],{mobilePanels:true});
  app.get('explorer-toggle').onclick();
  app.get('twin-details').open = true;
  app.get('twin-details').fire('toggle');
  assert.equal(app.get('sidebar').getAttribute('data-open'), 'false');
  assert.equal(app.get('inspector-rail').getAttribute('data-open'), 'false');
  app.get('motion-toggle').onclick();
  assert.equal(app.get('twin-details').open, false);
});


test('UI-only preview identifies reused CAD data and never offers an unverified package download', () => {
  const app=viewer([],[],{viewerPreview:{dataDate:'2026-10-10 UTC'}});
  assert.equal(app.get('download-package').hidden, true);
  assert.match(app.get('mode-note').textContent,/2026-10-10 UTC/);
  assert.match(app.get('mode-note').textContent,/nebyla znovu generována/);
  assert.match(app.get('viewer-status').textContent,/UI náhled/);
});


test('part tree keeps repeated friendly names distinguishable by CAD identity', () => {
  const app=viewer();
  app.meshes[0].info.title='Polohovací mikroservo';
  app.meshes[1].info.title='Polohovací mikroservo';
  app.get('part-search').value='Polohovací'; app.get('part-search').oninput();
  const buttons=app.get('tree').children[0].children.slice(1);
  assert.equal(buttons[0].getAttribute('aria-label'),'Polohovací mikroservo (pca9685)');
  assert.equal(buttons[1].getAttribute('aria-label'),'Polohovací mikroservo (esp32)');
  assert.equal(buttons[0].children[0].textContent,'pca9685');
  assert.equal(buttons[1].children[0].textContent,'esp32');
});


test('lost graphics context gives a visible recovery action instead of stale ready status', () => {
  const app=viewer();
  app.get('view').fire('webglcontextlost');
  assert.equal(app.get('viewer-error').hidden,false);
  assert.match(app.get('viewer-error').textContent,/Obnovte stránku/);
  assert.equal(app.get('viewer-status').textContent,'Model není dostupný');
});
