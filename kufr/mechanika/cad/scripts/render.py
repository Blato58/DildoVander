"""Render the exported solid geometry with a local VTK offscreen camera."""
import json
import math
from dataclasses import replace
from pathlib import Path

import vtk

from common import COLORS


def mesh(shape):
    vertices, faces = shape.tessellate(0.3, 0.2)
    return [[round(v.x, 3), round(v.y, 3), round(v.z, 3)] for v in vertices], faces


def actor(item, alpha=1):
    vertices, triangles = mesh(item.shape)
    points, faces = vtk.vtkPoints(), vtk.vtkCellArray()
    for v in vertices:
        points.InsertNextPoint(*v)
    for triangle in triangles:
        faces.InsertNextCell(3)
        for index in triangle:
            faces.InsertCellPoint(index)
    poly = vtk.vtkPolyData()
    poly.SetPoints(points)
    poly.SetPolys(faces)
    normals = vtk.vtkPolyDataNormals()
    normals.SetInputData(poly)
    normals.SetFeatureAngle(50)
    mapper = vtk.vtkPolyDataMapper()
    mapper.SetInputConnection(normals.GetOutputPort())
    result = vtk.vtkActor()
    result.SetMapper(mapper)
    color = COLORS.get(item.kind, COLORS["metal"]).lstrip("#")
    result.GetProperty().SetColor(*(int(color[i:i + 2], 16) / 255 for i in [0, 2, 4]))
    result.GetProperty().SetOpacity(alpha)
    result.GetProperty().SetAmbient(.3)
    result.GetProperty().SetDiffuse(.7)
    return result


def png(items, path, title, top=False):
    renderer = vtk.vtkRenderer()
    renderer.SetBackground(.94, .95, .97)
    renderer.SetBackground2(.80, .85, .9)
    renderer.GradientBackgroundOn()
    for item in items:
        if item.kind == "space":
            if item.metadata.get("role") != "cable_route":
                continue
            item = replace(item, kind="cable")
        renderer.AddActor(actor(item))
    caption = vtk.vtkTextActor()
    caption.SetInput(title + "\nmm | PROVISIONAL CAD - physical fit not verified")
    caption.SetPosition(24, 20)
    caption.GetTextProperty().SetFontSize(22)
    caption.GetTextProperty().SetColor(.13, .16, .21)
    renderer.AddActor2D(caption)
    camera = renderer.GetActiveCamera()
    camera.SetFocalPoint(216, 135, 55)
    camera.SetPosition(216, 135, 1200) if top else camera.SetPosition(720, -760, 640)
    camera.SetViewUp(0, 1, 0) if top else camera.SetViewUp(0, 0, 1)
    camera.ParallelProjectionOn()
    renderer.ResetCamera()
    camera.Zoom(1.12)
    window = vtk.vtkRenderWindow()
    window.SetOffScreenRendering(1)
    window.SetSize(1500, 1100)
    window.AddRenderer(renderer)
    window.Render()
    output = vtk.vtkWindowToImageFilter()
    output.SetInput(window)
    output.Update()
    writer = vtk.vtkPNGWriter()
    path.parent.mkdir(parents=True, exist_ok=True)
    writer.SetFileName(str(path))
    writer.SetInputConnection(output.GetOutputPort())
    writer.Write()
    window.Finalize()


def html(items, path):
    """Offline WebGL viewer: orbit/zoom, hide categories, pick individual solids."""
    data = []
    for item in items:
        if item.kind == "space":
            if item.metadata.get("role") != "cable_route":
                continue
            item = replace(item, kind="cable")
        v, f = mesh(item.shape)
        data.append(dict(n=item.name, k=item.kind, c=COLORS.get(item.kind, COLORS["metal"]), v=v, f=f))
    template = r'''<!doctype html><html lang="cs"><meta charset="utf-8"><title>DildoVander CAD</title>
<style>body{margin:0;background:#101722;color:#e5eaf2;font:15px system-ui}aside{position:absolute;left:20px;top:16px;width:270px;background:#172334e8;padding:18px;border-radius:12px}h1{font-size:24px;margin:0 0 10px}p{line-height:1.5}label{display:block;margin:8px 0}select{width:100%;padding:6px}canvas{width:100vw;height:100vh;display:block}.note{color:#f2c07b;font-size:13px}button{margin-top:12px;padding:8px}</style>
<canvas id="view"></canvas><aside><h1>DildoVander / CAD</h1><p>Tažením otáčejte, kolečkem přibližujte. Zdroj: skutečná BREP geometrie sestavy.</p><div id="filters"></div><p>Samostatný díl:</p><select id="part"><option value="">Celá sestava</option></select><button id="reset">Výchozí pohled</button><p class="note">PROVIZORNÍ SESTAVA<br>Vnitřek 431,8 × 266,7 × 114,3 mm.<br>Stěny 3 mm – návrhová hodnota.<br>CAD ani STL nepotvrzují fyzické usazení, únosnost nebo stabilitu bez proudu.</p><p>Osa X: šířka · Y: k pantům · Z: od dna. STEP dovoluje nezávislý výběr všech součástí.</p></aside>
<script>const objects=DATA;const canvas=document.querySelector('canvas'),gl=canvas.getContext('webgl');
if(!gl)throw Error('WebGL nedostupné');
function shader(type,src){let s=gl.createShader(type);gl.shaderSource(s,src);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s}
let program=gl.createProgram();gl.attachShader(program,shader(gl.VERTEX_SHADER,'attribute vec3 a;attribute vec3 n;uniform mat4 m;varying float light;void main(){gl_Position=m*vec4(a,1.);light=.45+.55*abs(dot(normalize(n),normalize(vec3(.3,-.6,1.))));}'));gl.attachShader(program,shader(gl.FRAGMENT_SHADER,'precision mediump float;uniform vec3 color;varying float light;void main(){gl_FragColor=vec4(color*light,1.);}'));gl.linkProgram(program);gl.useProgram(program);
const a=gl.getAttribLocation(program,'a'),n=gl.getAttribLocation(program,'n'),matrix=gl.getUniformLocation(program,'m'),color=gl.getUniformLocation(program,'color');
const categories=[...new Set(objects.map(o=>o.k))],enabled=new Set(categories);for(let k of categories){let label=document.createElement('label');label.innerHTML='<input type="checkbox" checked> '+k;label.querySelector('input').onchange=e=>{e.target.checked?enabled.add(k):enabled.delete(k);draw()};document.querySelector('#filters').append(label)}
objects.forEach((o,index)=>{let positions=[],normals=[];for(let t of o.f){let p=o.v[t[0]],q=o.v[t[1]],r=o.v[t[2]],u=q.map((v,i)=>v-p[i]),v=r.map((v,i)=>v-p[i]),nn=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]];for(let i of t){positions.push(...o.v[i]);normals.push(...nn)}}o.count=positions.length/3;o.buffers=[positions,normals].map(values=>{let b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(values),gl.STATIC_DRAW);return b});o.rgb=[1,3,5].map(i=>parseInt(o.c.slice(i,i+2),16)/255);let option=document.createElement('option');option.value=index;option.textContent=o.n;document.querySelector('#part').append(option);delete o.v;delete o.f});
let az=-.8,el=.65,scale=850;function draw(){canvas.width=canvas.clientWidth*devicePixelRatio;canvas.height=canvas.clientHeight*devicePixelRatio;gl.viewport(0,0,canvas.width,canvas.height);gl.clearColor(.065,.09,.135,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);const aspect=canvas.width/canvas.height,ca=Math.cos(az),sa=Math.sin(az),ce=Math.cos(el),se=Math.sin(el),right=[ca,-sa,0],up=[sa*se,ca*se,ce],dep=[-sa*ce,-ca*ce,se],center=[216,135,135],sx=2/(scale*aspect),sy=2/scale,sz=-1/1500,dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0);let m=[right[0]*sx,up[0]*sy,dep[0]*sz,0,right[1]*sx,up[1]*sy,dep[1]*sz,0,right[2]*sx,up[2]*sy,dep[2]*sz,0,-dot(right,center)*sx+.2,-dot(up,center)*sy,-dot(dep,center)*sz,1];gl.uniformMatrix4fv(matrix,false,new Float32Array(m));let selected=document.querySelector('#part').value;objects.forEach((o,i)=>{if(!enabled.has(o.k)||(selected!==''&&Number(selected)!==i))return;for(let j=0;j<2;j++){let at=j?n:a;gl.bindBuffer(gl.ARRAY_BUFFER,o.buffers[j]);gl.enableVertexAttribArray(at);gl.vertexAttribPointer(at,3,gl.FLOAT,false,0,0)}gl.uniform3fv(color,o.rgb);gl.drawArrays(gl.TRIANGLES,0,o.count)})}
let previous=null;canvas.onpointerdown=e=>{previous=[e.clientX,e.clientY];canvas.setPointerCapture(e.pointerId)};canvas.onpointerup=()=>previous=null;canvas.onpointermove=e=>{if(!previous)return;az+=(e.clientX-previous[0])*.008;el=Math.max(-1.5,Math.min(1.5,el+(e.clientY-previous[1])*.008));previous=[e.clientX,e.clientY];draw()};canvas.onwheel=e=>{e.preventDefault();scale=Math.max(80,Math.min(1800,scale*Math.exp(e.deltaY*.001)));draw()};window.onresize=draw;document.querySelector('#part').onchange=draw;document.querySelector('#reset').onclick=()=>{az=-.8;el=.65;scale=850;document.querySelector('#part').value='';draw()};draw();</script></html>'''
    path.write_text(template.replace("DATA", json.dumps(data, separators=(",", ":"))), encoding="utf-8")
