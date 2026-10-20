/* Extracted F2 graphics algorithms. Data and state in; no controls or DOM ownership. */
(function(root) {
'use strict';
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
function lift(a, model) {
  const d = model.mechanism.dwell_degrees, t = model.mechanism.travel_degrees;
  if (a <= d) return 0;
  if (a >= t-d) return model.mechanism.lift;
  const x = (a-d)/(t-2*d);
  return model.mechanism.lift*(10*x**3-15*x**4+6*x**5);
}
function motionFor(mesh) {
  if (mesh.motion) return mesh.motion;
  const index = +(mesh.name.match(/_([1-6])$/)?.[1] || 0);
  if (!index) return null;
  if (/^(cam_|stock_horn_|coupler_)/.test(mesh.name)) return {type:'cam',index};
  if (/^(tray_|u059_|led_|wand_|soft_liner_|tray_foam_|tray_fabric_)/.test(mesh.name)) return {type:'lift',index};
  return null;
}
function perspective(aspect,distance,modelScale) {
  const f = 1/Math.tan(.6/2), near = Math.max(.5,distance*.001), far = Math.max(4000,distance+modelScale*4);
  return [f/aspect,0,0,0, 0,f,0,0, 0,0,(far+near)/(near-far),-1, 0,0,2*far*near/(near-far),0];
}
function movement(mesh,model,state) {
  if (mesh.group.startsWith('lid')) return rotX(-state.lid*Math.PI/180, model.hinge);
  const motion=motionFor(mesh), angle=motion ? state.angles[motion.index-1] ?? 0 : 0;
  if (motion?.type==='lift') return translate(lift(angle,model));
  if (motion?.type==='cam') return rotX(angle*Math.PI/180,[0,model.mechanism.origin_y+model.mechanism.cam_y,model.mechanism.origin_z+model.mechanism.cam_z]);
  return identity();
}
function corners(meshes,model,state) {
  const points=[];
  for(const mesh of meshes) for(let corner=0;corner<8;corner++) {
    const point=[0,1,2].map(axis=>mesh.bounds[axis+((corner>>axis)&1)*3]);
    points.push(transform(movement(mesh,model,state),[...point,1]).slice(0,3));
  }
  return points;
}
function fitScene(meshes,model,state,yaw,pitch,width,height) {
  const points=corners(meshes,model,state);if(!points.length)return null;
  const target=[0,1,2].map(axis=>(Math.min(...points.map(point=>point[axis]))+Math.max(...points.map(point=>point[axis])))/2);
  const forward=[Math.cos(pitch)*Math.cos(yaw),Math.cos(pitch)*Math.sin(yaw),Math.sin(pitch)];
  const right=norm(cross([0,0,1],forward)),up=cross(forward,right);
  const tangent=Math.tan(.6/2),aspect=Math.max(.05,width/Math.max(1,height));let distance=75;
  for(const point of points) {
    const relative=subtract(point,target);
    const extent=Math.max(Math.abs(dot(relative,right))/(tangent*aspect),Math.abs(dot(relative,up))/tangent);
    distance=Math.max(distance,dot(relative,forward)+extent*1.16);
  }
  return {target,distance};
}
function boundTarget(target,bounds,scale) {return target.map((value,axis)=>Math.max(bounds[axis][0]-scale*.6,Math.min(bounds[axis][1]+scale*.6,value)));}
function boundDistance(value,scale) {return Math.max(25,Math.min(scale*16,value));}
function panCamera(target,distance,yaw,pitch,height,dx,dy,bounds,scale) {
  const forward=[Math.cos(pitch)*Math.cos(yaw),Math.cos(pitch)*Math.sin(yaw),Math.sin(pitch)];
  const right=norm(cross([0,0,1],forward)),up=cross(forward,right),units=2*distance*Math.tan(.3)/Math.max(1,height);
  return boundTarget(target.map((value,axis)=>value+(-dx*right[axis]+dy*up[axis])*units),bounds,scale);
}
function projectPoint(position,projection,view,width,height) {
  const point=transform(projection,transform(view,[...position,1]));
  if(point[3]<=0 || Math.abs(point[2]/point[3])>1)return null;
  return [(point[0]/point[3]+1)*width/2,(1-point[1]/point[3])*height/2];
}
function componentOf(endpoint,model) {return model.endpoints?.[endpoint]?.component || (endpoint || '').split(':')[0];}
function physicalEndpoint(route,side,state) {const absent=state.mode==='transport' || !state.bank;return absent && route['transport_'+side] || route[side];}
function routePoints(route,model,state) {
  const bankAbsent=state.mode==='transport' || !state.bank;
  const parked=bankAbsent && (route.transport_points || route.disconnected_points);
  const points=(parked || route.points).map(point=>[...point]);if(parked)return points;
  if(route.point_lift_weights) {
    for(const [module,weights] of Object.entries(route.point_lift_weights)) {
      const mesh=model.meshes.find(item=>item.name==='tray_left_'+module),height=mesh ? movement(mesh,model,state)[14] : 0;
      if(weights.length!==points.length)throw new Error('Neplatný model kabelové smyčky');
      for(let i=0;i<points.length;i++)points[i][2]+=weights[i]*height;
    }
    return points;
  }
  const transforms=route.moving_points || {};
  for(let i=0;i<points.length;i++) {
    const endpoint=transforms[i] || (i===0 ? route.from : i===points.length-1 ? route.to : '');
    const mesh=model.meshes.find(item=>item.name===componentOf(endpoint,model));
    if(mesh && motionFor(mesh))points[i]=transform(movement(mesh,model,state),[...points[i],1]).slice(0,3);
  }
  return points;
}
function focusMeshes(meshes,model,state,width,height) {
  const points=corners(meshes,model,state);if(!points.length)return null;
  const low=[0,1,2].map(axis=>Math.min(...points.map(point=>point[axis]))),high=[0,1,2].map(axis=>Math.max(...points.map(point=>point[axis])));
  const radius=Math.hypot(...high.map((value,axis)=>(value-low[axis])/2));
  const angle=Math.atan(Math.tan(.3)*Math.min(1,width/Math.max(1,height)));
  return {target:low.map((value,axis)=>(value+high[axis])/2),distance:Math.max(75,radius/Math.sin(angle)*1.2)};
}
const cameraViews={top:{angles:[-Math.PI/2,Math.PI/2-.001],label:'Shora'},front:{angles:[-Math.PI/2,.01],label:'Zepředu'},back:{angles:[Math.PI/2,.01],label:'Zezadu'},left:{angles:[Math.PI,.01],label:'Zleva'},side:{angles:[0,.01],label:'Zprava'},overview:{angles:[-1.05,.66],label:'Izometrie'}};
function placeLabels(labels,width,height) {
  const cellWidth=16,rowHeight=Math.max(0,...labels.map(item=>item.h))+3;
  const columns=Math.max(0,Math.floor((width-8)/cellWidth)),rows=Math.max(0,Math.floor((height-45)/rowHeight));
  const occupied=Array.from({length:rows},()=>new Uint8Array(columns)),placements=[];
  for(const item of labels) {
    const {point,w,h}=item,required=Math.ceil((w+3)/cellWidth),desired=Math.round((point[0]-w/2-8)/cellWidth);
    let best=null,score=Infinity;
    for(let row=0;row<rows;row++) {
      let start=0;
      for(let column=0;column<=columns;column++) {
        if(column<columns && !occupied[row][column])continue;
        if(column-start>=required) {
          const candidate=Math.max(start,Math.min(column-required,desired)),x=8+candidate*cellWidth,y=45+row*rowHeight;
          const d=(x+w/2-point[0])**2+(y+h/2-point[1])**2;
          if(d<score){best={x,y,row,column:candidate};score=d;}
        }
        start=column+1;
      }
    }
    const {x,y}=best || {x:Math.max(0,Math.min(width-w,point[0])),y:Math.max(45,Math.min(height-h,point[1]))};
    if(best)occupied[best.row].fill(1,best.column,best.column+required);
    placements.push({...item,x,y});
  }
  return placements;
}
const shaders={"vertex": "\n  attribute vec3 p,n; uniform mat4 projection,view,move; varying vec3 normal;\n  void main(){gl_Position=projection*view*move*vec4(p,1.);normal=mat3(move)*n;}", "fragment": "\n  precision mediump float; uniform vec3 color,selectionColor; uniform bool picking; uniform float highlight,opacity; varying vec3 normal;\n  void main(){if(picking){gl_FragColor=vec4(color,1.);return;}\n    float l=.48+.42*max(0.,dot(normalize(normal),normalize(vec3(.4,-.7,1.))))+.1*abs(normalize(normal).z);\n    gl_FragColor=vec4(mix(color*l,selectionColor,highlight),opacity);}"};
const api={placeLabels,shaders,uploadGeometry,identity,translate,rotX,transform,subtract,dot,cross,norm,look,perspective,lift,motionFor,movement,corners,fitScene,boundTarget,boundDistance,panCamera,projectPoint,componentOf,physicalEndpoint,routePoints,focusMeshes,cameraViews};
if(typeof module!=='undefined' && module.exports)module.exports=api;else root.F2GraphicsCore=api;
})(typeof window==='undefined'?globalThis:window);
