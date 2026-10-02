"""Offline visualisation and manufacturing list for model.py."""
import csv
import json
from collections import Counter

import cadquery as cq
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Poly3DCollection


def make_case_layout(p, parts, lift, purchased, mechanics):
    from model import block, cylinder, holes_z
    items = []
    movable = {"tray_1", "tray_2", "tray_3", "tray_splice", "lift_follower",
               "vibration_cage", "wand_saddle", "U059 envelope (unmeasured)"}
    for index in range(6):
        dx = (index-2.5)*p["compartment_pitch_mm"]
        for source in lift+purchased:
            item = dict(source)
            item.update(shape=source["shape"].translate((dx, -75, 4)),
                        group="selected" if index == 3 and source["name"] in movable else "fixed")
            items.append(item)
        wand = cylinder(p["wand_max_diameter_mm"]/2, p["wand_max_length_mm"],
                        (dx, -75-p["wand_max_length_mm"]/2, p["tray_floor_low_mm"]+24), (0,1,0))
        items.append(dict(name=f"wand_{index+1}", shape=wand, color="#56392f",
                          group="selected" if index == 3 else "fixed"))
    positions = {
        "power": (-65, 120, 4), "safety": (-65, 120, 54),
        "speaker": (-65, 120, 154),
        "controller": (90, 114, 4), "pca9685": (90, 114, 54),
        "audio": (90, 184, 4), "microphone": (85, 184, 54)}
    for key, position in positions.items():
        w,d,h = p["boxes"][key]
        for suffix, dz in [("box",0), ("lid",h), ("pcb_panel",8)]:
            name=f"{key}_{suffix}"
            if name in parts:
                items.append(dict(name=name, shape=parts[name].translate((position[0],position[1],position[2]+dz)),
                                  color="#99b2be" if suffix != "lid" else "#b5c5cd", group="fixed"))
    for name, pos in [("powerbank_strap_cradle",(-65,120,109)), ("control_panel",(85,184,99))]:
        items.append(dict(name=name, shape=parts[name].translate(pos), color="#8496a4", group="fixed"))
    bx,by,bz=p["powerbank_body_xyz_mm"]
    items.append(dict(name="powerbank envelope (unmeasured)",shape=block(bx,by,bz,(-65,120,112)),
                      color="#344754",group="fixed"))
    for key,w,x,levels in [("power_shelf",176,-65,[0,50,105,150]),
                           ("logic_shelf",120,90,[0,50,95])]:
        depth=145
        pts=[(u,v) for u in [-w/2+4,w/2-4] for v in [-depth/2+4,depth/2-4]]
        shelf=holes_z(block(w,depth,4),pts,4.5,depth=6)
        for u in [-w/3,0,w/3]:
            for v in [-45,0,45]:
                shelf=shelf.cut(block(14,3,6,(u,v,-1)))
        parts[key]=shelf.clean()
        for z in levels:
            items.append(dict(name=key,shape=shelf.translate((x,135,z)),color="#607782",group="fixed"))
        for u,v in pts:
            items.append(dict(name="M4 shelf post (hardware)",shape=cylinder(2,max(levels)+4,(x+u,135+v,0)),
                              color="#b5c6d1",group="fixed"))
    # Dimensions are a provisional interior envelope, not a model of Marketos walls.
    bboxes=[i["shape"].BoundingBox() for i in items]
    bounds=[min(b.xmin for b in bboxes),max(b.xmax for b in bboxes),
            min(b.ymin for b in bboxes),max(b.ymax for b in bboxes),
            min(b.zmin for b in bboxes),max(b.zmax for b in bboxes)]
    case=p["case_inside_mm"]
    if bounds[0]<-case[0]/2 or bounds[1]>case[0]/2 or bounds[2]<-case[1]/2 or bounds[3]>case[1]/2 or bounds[4]<0 or bounds[5]>case[2]:
        raise ValueError(f"Assembly exceeds provisional case envelope: {bounds} vs {case}")
    return items


def mesh(shape):
    vertices, triangles=shape.tessellate(0.7,0.4)
    return [[round(v.x,3),round(v.y,3),round(v.z,3)] for v in vertices], triangles


def draw(ax, items, title, elev=34, azim=-62):
    xs,ys,zs=[],[],[]
    for item in items:
        vv,tt=mesh(item["shape"])
        polys=[[vv[k] for k in t] for t in tt]
        collection=Poly3DCollection(polys,facecolors=item["color"],edgecolors="none",alpha=1)
        ax.add_collection3d(collection)
        xs.extend(v[0] for v in vv);ys.extend(v[1] for v in vv);zs.extend(v[2] for v in vv)
    bounds=[(min(xs)-8,max(xs)+8),(min(ys)-8,max(ys)+8),(min(zs)-8,max(zs)+8)]
    ax.set(xlim=bounds[0],ylim=bounds[1],zlim=bounds[2])
    ax.set_box_aspect([b-a for a,b in bounds]); ax.view_init(elev=elev,azim=azim)
    ax.set_title(title,fontsize=12,pad=15);ax.set_axis_off()


def write_preview(out,p,case_items,lift_items,mechanics):
    fig=plt.figure(figsize=(16,10),facecolor="#f5f3ee")
    a=fig.add_subplot(121,projection="3d")
    draw(a,case_items,"6 přihrádek + servisní část • prostor 450 × 310 × 200 mm",55,-48)
    b=fig.add_subplot(122,projection="3d")
    draw(b,lift_items,"Detail zdvihu • klika, vedení, držák vibrací",22,-135)
    fig.suptitle("AI kufr • parametrický mechanický prototyp",fontsize=20,y=.94)
    fig.text(.5,.07,"Hůlky do 200 mm · zdvih 28,3 mm · oranžová: přihrádky · zelená: vibrace\n"
             "Rozměry modulů jsou pracovní odhady. Pasivní zajištění při ztrátě proudu není ověřené.",
             ha="center",fontsize=11,color="#4c5358")
    fig.savefig(out/"nahled.png",dpi=150,bbox_inches="tight");plt.close(fig)
    # Self-contained canvas viewer, no CDN, no web server or publication.
    records=[]
    for item in case_items:
        vertices,triangles=mesh(item["shape"])
        records.append(dict(v=vertices,t=triangles,c=item["color"],g=item["group"]))
    payload=json.dumps(records,separators=(",",":"))
    html='''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AI kufr – CAD prototyp</title><style>
body{margin:0;background:#101a23;color:#edf1f4;font:16px system-ui}header{padding:18px 24px}
h1{font-size:24px;margin:0 0 8px}p{margin:8px 0;color:#bdc8d0;max-width:950px}
canvas{display:block;width:100%;height:65vh;touch-action:none}label,button{margin-right:18px}
button{background:#284655;color:white;border:1px solid #668a9c;padding:8px 14px;border-radius:6px}
footer{padding:12px 24px;color:#bdc8d0}input{vertical-align:middle}
</style><header><h1>AI kufr · 6 přihrádek · 3D prototyp</h1>
<p>Kufr 450 × 310 × 200 mm, hůlky do 200 mm. Tažením otáčej, kolečkem přibližuj. Náhled je sestavený z CAD geometrie.</p>
<label>Zdvih přihrádky 4 <input id="lift" type="range" min="0" max="100" value="0"> <span id="mm">0 mm</span></label>
<button id="top">Pohled shora</button><button id="iso">Prostorový pohled</button>
</header><canvas id="view"></canvas><footer>Pracovní rozměry součástek a vnitřní rozměry kufru potřebují měření. Pohyb v náhledu zobrazuje zdvih vložky; servo a klika zůstávají v klidové poloze. Mechanismus nemá ověřené pasivní zajištění při výpadku napájení. Volitelný router není osazený.</footer>
<script>const data=PAYLOAD,stroke=STROKE;
const cv=document.getElementById('view'),ctx=cv.getContext('2d');let yaw=-.45,pitch=.83,zoom=1,drag=false,last=null;
function render(){const dpr=devicePixelRatio||1,w=cv.clientWidth,h=cv.clientHeight;cv.width=w*dpr;cv.height=h*dpr;ctx.scale(dpr,dpr);ctx.clearRect(0,0,w,h);
 const sc=Math.min(w/650,h/540)*zoom,cy=Math.cos(yaw),sy=Math.sin(yaw),cp=Math.cos(pitch),sp=Math.sin(pitch);
 const dz=+document.getElementById('lift').value/100*stroke;document.getElementById('mm').textContent=dz.toFixed(1)+' mm';
 const project=(v,z)=>{let x=v[0],y=v[1],zz=v[2]+z-75;let a=x*cy-y*sy,b=x*sy+y*cy;return [w/2+a*sc,h/2+(b*sp-zz*cp)*sc,b*cp+zz*sp]};
 let polys=[];for(const m of data){const vv=m.v.map(v=>project(v,m.g==='selected'?dz:0));for(const t of m.t){const a=vv[t[0]],b=vv[t[1]],c=vv[t[2]];polys.push({v:[a,b,c],z:(a[2]+b[2]+c[2])/3,c:m.c})}}
 polys.sort((a,b)=>a.z-b.z);for(const p of polys){ctx.fillStyle=p.c;ctx.beginPath();ctx.moveTo(p.v[0][0],p.v[0][1]);ctx.lineTo(p.v[1][0],p.v[1][1]);ctx.lineTo(p.v[2][0],p.v[2][1]);ctx.closePath();ctx.fill()}}
cv.onpointerdown=e=>{drag=true;last=[e.clientX,e.clientY];cv.setPointerCapture(e.pointerId)};
cv.onpointermove=e=>{if(!drag)return;yaw+=(e.clientX-last[0])*.008;pitch=Math.max(-1.5,Math.min(1.56,pitch+(e.clientY-last[1])*.008));last=[e.clientX,e.clientY];render()};
cv.onpointerup=()=>drag=false;cv.onpointercancel=()=>drag=false;cv.onwheel=e=>{e.preventDefault();zoom=Math.max(.4,Math.min(3,zoom*Math.exp(-e.deltaY*.001)));render()};
document.getElementById('lift').oninput=render;document.getElementById('top').onclick=()=>{yaw=0;pitch=1.56;render()};
document.getElementById('iso').onclick=()=>{yaw=-.45;pitch=.83;zoom=1;render()};addEventListener('resize',render);render();</script></html>'''
    (out/"nahled.html").write_text(html.replace("PAYLOAD",payload).replace("STROKE",str(mechanics["lift_mm"])),encoding="utf-8")


def write_bom(out,p,parts,lift):
    counts=Counter(i["name"] for i in lift)
    rows=[]
    for name,shape in parts.items():
        qty=counts[name]*6 if name in counts else 1
        if name=="cable_clip":qty=12
        if name=="pcb_standoff":qty=24
        if name=="power_shelf":qty=4
        if name=="logic_shelf":qty=3
        if name.startswith("router_optional"):qty=0
        b=shape.BoundingBox()
        rows.append([name,qty,round(b.xlen,1),round(b.ylen,1),round(b.zlen,1),
                     "PETG", "Change orientation/supports in slicer; see NAVOD.md"])
    with (out/"tiskovy-kusovnik.csv").open("w",encoding="utf-8-sig",newline="") as file:
        writer=csv.writer(file);writer.writerow(["part","quantity","x_mm","y_mm","z_mm","material","note"]);writer.writerows(rows)
