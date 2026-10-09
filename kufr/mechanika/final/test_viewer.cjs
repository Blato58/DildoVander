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

function viewer() {
  const elements = new Map(), frames = [];
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
  const meshes = ['pca9685','esp32'].map(name => ({
    name,group:'hardware',color:[.1,.4,.2],bounds:[0,0,0,10,10,10],data:[0,0,0,0,0,1],
    info:{title:name,description:name,details:[],source:'test'},
  }));
  const document = {
    getElementById:get, createElement:() => new Element(), createElementNS:() => new Element(),
    listeners:{}, addEventListener(name, callback) { this.listeners[name] = callback; },
  };
  const context = {
    document, window:{addEventListener() {}}, devicePixelRatio:1,
    model:{meshes,connections:[],sources:[],hinge:[0,0,0],mechanism:{dwell_degrees:10,travel_degrees:160,lift:16}},
    localStorage:{getItem:() => null,setItem() {}},
    Option:class {constructor(text,value) {this.textContent = text; this.value = value;}},
    requestAnimationFrame:callback => frames.push(callback),
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'viewer.js'),'utf8'), context);
  const flush = () => { while (frames.length) frames.shift()(); };
  const hover = id => { hit = id; get('view').fire('pointermove',{clientX:400,clientY:300,shiftKey:true}); flush(); };
  const click = () => { get('view').fire('pointerdown',{clientX:400,clientY:300,pointerId:1}); flush(); };
  return {get,hover,click,flush};
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

test('empty-space inspection shows all names while retaining the pinned card', () => {
  const app = viewer();
  app.hover(1); app.click(); app.hover(0);
  assert.equal(app.get('mesh-id').textContent,'pca9685');
  assert.equal(app.get('object-details').hidden,false);
  assert.deepEqual(app.get('names').children.map(node=>node.textContent).sort(),['esp32','pca9685']);
  app.hover(2); app.click();
  assert.equal(app.get('mesh-id').textContent,'esp32');
});
