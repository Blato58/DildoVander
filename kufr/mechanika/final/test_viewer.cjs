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
  fire(name, event = {}) { this.listeners[name]?.(event); }
  setAttribute(name, value) { this.attributes[name] = value; }
  append(node) { this.children.push(node); }
  add(node) { this.append(node); }
  replaceChildren(...nodes) { this.children = nodes; }
  getBoundingClientRect() { return {left:0,top:0,width:800,height:600}; }
  setPointerCapture() {}
}

function viewer(connections = [], extraMeshes = []) {
  const elements = new Map(), frames = [];
  let labelMeasurements = 0;
  const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  get('lid').value = '105'; get('lift').value = '0';
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
  const meshes = [...['pca9685','esp32'].map(name => ({name,group:'hardware'})),...extraMeshes].map(({name,group}) => ({
    name,group,color:[.1,.4,.2],bounds:[0,0,0,10,10,10],data:[0,0,0,0,0,1],
    info:{title:name,description:name,details:[],source:'test'},
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
    document, window, devicePixelRatio:1,
    model:{meshes,connections,sources:[],hinge:[0,0,0],mechanism:{dwell_degrees:10,travel_degrees:160,lift:16}},
    localStorage:{getItem:() => null,setItem() {}},
    Option:class {constructor(text,value) {this.textContent = text; this.value = value;}},
    requestAnimationFrame:callback => frames.push(callback),
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'viewer.js'),'utf8'), context);
  const flush = () => { while (frames.length) frames.shift()(); };
  const hover = id => { hit = id; get('view').fire('pointermove',{clientX:400,clientY:300,shiftKey:true}); flush(); };
  const click = () => { get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1}); flush(); };
  return {get,hover,click,flush,document,window,measurements:()=>labelMeasurements};
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

test('releasing Shift keeps the pin, enables orbit, and distinguishes an empty click from a drag', () => {
  const app = viewer([{from:'esp32',to:'pca9685',kind:'power',label:'3V3'}]);
  app.hover(1);app.click();
  assert.equal(app.get('inspect').attributes['aria-pressed'],'false','Pinning does not enable sticky inspect');
  app.document.listeners.keyup({key:'Shift'});app.flush();
  assert.equal(app.get('inspect-hint').hidden,true);
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  const pathBefore = app.get('overlay').children.find(node=>node.attributes.d).attributes.d;
  app.get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1});
  app.get('view').fire('pointermove',{clientX:450,clientY:320,shiftKey:false});
  app.get('view').fire('pointerup',{clientX:450,clientY:320});app.flush();
  assert.equal(app.get('mesh-id').textContent,'pca9685','Orbit must retain the pinned card');
  assert.notEqual(app.get('overlay').children.find(node=>node.attributes.d).attributes.d,pathBefore);
  app.hover(0);app.document.listeners.keyup({key:'Shift'});app.flush();
  app.get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1});
  app.get('view').fire('pointerup',{clientX:400,clientY:300});app.flush();
  assert.equal(app.get('inspector').hidden,true,'Empty click without Shift clears the pin');
});

test('parallel power, I2C, OE and ground remain separate visible paths', () => {
  for (const count of [2,4]) {
    const connections = ['power','signal','signal','ground'].slice(0,count).map((kind,index) => ({
      from:'esp32',to:'pca9685',kind,label:['3V3','I2C','OE','GND'][index],note:'test',
    }));
    const app = viewer(connections);
    app.hover(1); app.click();
    const paths = app.get('overlay').children.filter(node=>node.attributes.d);
    assert.equal(paths.length,count);
    assert.equal(new Set(paths.map(node=>node.attributes.d)).size,count);
    assert.equal(new Set(paths.map(node=>node.attributes.stroke)).size,count===4 ? 3 : 2);
    for (const node of paths) {
      const values = node.attributes.d.split(' ');
      assert.ok(Math.hypot(+values[4]-values[1],+values[5]-values[2])>1,'Coincident endpoints still need a visible curve');
    }
    assert.equal(app.get('connections').children.length,count);
  }
});

test('232-object empty-space layout reuses nodes and updates when the scene or panel changes', () => {
  const extra = [
    {name:'tray_3',group:'moving_hw'},{name:'lid_test',group:'lid'},
    {name:'cover_test',group:'cover'},{name:'case_test',group:'case'},
    {name:'wand_1',group:'wand'},
    ...Array.from({length:225},(_,i)=>({name:`test_${i}`,group:'hardware'})),
  ];
  const app = viewer([{from:'esp32',to:'pca9685',kind:'power',label:'3V3'}],extra);
  app.hover(0);
  assert.equal(app.get('names').children.length,232);
  const labels = [...app.get('names').children], leaders = [...app.get('overlay').children];
  const measurements = app.measurements();
  for (let i = 0; i < 20; i++) app.hover(0);
  assert.equal(app.measurements(),measurements,'Pointer movement must not measure labels again');
  assert.equal(app.get('names').children[0],labels[0]);
  assert.equal(app.get('overlay').children[0],leaders[0]);

  function changed(action) {
    const previous = app.get('names').children[0];
    action(); app.flush();
    assert.notEqual(app.get('names').children[0],previous);
  }
  const endpoint = name => {
    const index = app.get('names').children.findIndex(node=>node.textContent===name);
    return JSON.stringify(app.get('overlay').children[index].attributes);
  };
  const tray = endpoint('tray_3'), lid = endpoint('lid_test');
  changed(()=>{app.get('lift').value='160';app.get('lift').oninput();});
  assert.notEqual(endpoint('tray_3'),tray);
  changed(()=>{app.get('lid').value='0';app.get('lid').oninput();});
  assert.notEqual(endpoint('lid_test'),lid);
  for (const id of ['cover','case','beds','hardware']) {
    changed(()=>{app.get(id).checked=false;app.get(id).oninput();});
    changed(()=>{app.get(id).checked=true;app.get(id).oninput();});
  }
  changed(()=>app.get('view').fire('wheel',{deltaY:20,preventDefault() {}}));
  const beforeDrag = endpoint('tray_3');
  app.document.listeners.keyup({key:'Shift'});app.flush();
  app.get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1});
  app.get('view').fire('pointermove',{clientX:450,clientY:320,shiftKey:false});
  app.get('view').fire('pointerup');app.flush();
  app.document.listeners.keydown({key:'Shift'});app.flush();
  assert.notEqual(endpoint('tray_3'),beforeDrag,'Camera drag updates leader endpoints');
  changed(()=>{app.get('view').clientWidth=900;app.window.listeners.resize();});
  app.hover(1); app.click(); app.hover(0);
  const pinnedLabels = app.get('names').children[0];
  assert.equal(app.get('names').children.length,1,'Pinned inspection only labels its connected peer');
  app.get('color').value='#112233';app.get('color').oninput();app.flush();
  assert.equal(app.get('names').children[0],pinnedLabels,'Color-only edits retain the layout');
  changed(()=>{app.get('inspector').offsetHeight=300;app.get('color').oninput();});
  app.get('unpin').onclick();app.flush();
  assert.equal(app.get('object-details').hidden,true);
  app.hover(2); app.hover(0);
  assert.equal(app.get('names').children.length,232,'Hover exit restores all names');
  changed(()=>app.get('closed').onclick());
  assert.equal(app.get('names').children.some(node=>node.textContent==='wand_1'),false);
});
