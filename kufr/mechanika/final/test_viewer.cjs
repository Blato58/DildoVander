/* Exercise the real viewer event handlers; rendering hits are supplied by a fake GPU. */
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

class Element {
  constructor() {
    this.value = ''; this.checked = true; this.hidden = true;
    this.children = []; this.style = {}; this.listeners = {}; this.attributes = {};
    this.clientWidth = 800; this.clientHeight = 600; this.width = 800; this.height = 600;
    this.offsetWidth = 100; this.offsetHeight = 22;
  }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  fire(name, event = {}) { this.listeners[name]?.({preventDefault() {}, ...event}); }
  setAttribute(name, value) { this.attributes[name] = value; }
  append(node) { this.children.push(node); }
  add(node) { this.append(node); }
  replaceChildren(...nodes) { this.children = nodes; }
  getBoundingClientRect() { return {left:0,top:0,width:800,height:600}; }
  setPointerCapture() {}
}

function viewer(routes = [], extraMeshes = [], data = {}) {
  const elements = new Map(), frames = [];
  let labelMeasurements = 0;
  const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  get('lid').value = '105'; get('lift').value = '0';
  if (data.viewport) [get('view').clientWidth,get('view').clientHeight]=data.viewport;
  if (data.canvasRect) get('view').getBoundingClientRect=()=>data.canvasRect;
  for (const name of ['wires','dimensions','audio-preview']) get(name).checked = false;
  let hit = 0;
  const constants = new Map();
  const gl = new Proxy({
    getShaderParameter: () => true, getProgramParameter: () => true,
    getAttribLocation: () => 0, getUniformLocation: () => 0,
    checkFramebufferStatus: () => gl.FRAMEBUFFER_COMPLETE,
    readPixels: (x,y,w,h,format,type,pixel) => { pixel[0] = hit; },
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
    ...item,color:[.1,.4,.2],bounds:item.bounds || [100,100,20,110,110,30],data:[0,0,0,0,0,1],
    info:{title:item.name,description:item.name,details:[],source:'test'},
  }));
  function createElement() {
    const node = new Element();
    for (const [property,value] of [['offsetWidth',100],['offsetHeight',22]]) {
      Object.defineProperty(node,property,{get() {
        if (node.className === 'object-name') labelMeasurements++;
        return value;
      }});
    }
    return node;
  }
  const document = {
    getElementById:get, createElement, createElementNS:() => new Element(),
    listeners:{}, addEventListener(name, callback) { this.listeners[name] = callback; },
  };
  const window = {listeners:{},addEventListener(name,callback) { this.listeners[name] = callback; }};
  const context = {
    document, window, devicePixelRatio:data.devicePixelRatio || 1,
    model:{meshes,routes,sources:[],hinge:[0,0,0],mechanism:{count:6,origin_y:21,origin_z:12,cam_y:112,cam_z:36,dwell_degrees:20,travel_degrees:160,lift:16},...data},
    localStorage:{getItem:() => null,setItem() {}},
    Option:class {constructor(text,value) {this.textContent = text; this.value = value;}},
    requestAnimationFrame:callback => frames.push(callback),
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'viewer.js'),'utf8'), context);
  const flush = () => { let count=0; while (frames.length) { if (++count>1000) throw new Error('Animation did not finish'); frames.shift()(count*100); } };
  const frame = timestamp => { const pending = frames.splice(0); for (const callback of pending) callback(timestamp); };
  const hover = id => { hit = id; get('view').fire('pointermove',{clientX:400,clientY:300,shiftKey:true}); flush(); };
  const click = () => { get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1}); flush(); };
  const lights = () => vm.runInNewContext('lightStates.map(color=>color && [...color])', context);
  return {get,hover,click,flush,frame,document,window,meshes,api:window.F2,lights,measurements:()=>labelMeasurements};
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
      app.api.startEffect(variant); app.frame(0);
      const litIndexes=()=>Array.from(app.lights()).flatMap((color,index)=>color?.some(value=>value>0) ? [index] : []);
      assert.deepEqual(litIndexes(),[selected-1],'Wake lights only the selected compartment');
      for (let timestamp=100; timestamp<=20000; timestamp+=100) {
        app.frame(timestamp);
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
  app.api.startEffect('main'); app.frame(0); app.frame(500);
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
