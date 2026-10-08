"""Offline CAD viewer, annotated layout, and manufacturing bill."""
import csv
import json
from collections import Counter

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Poly3DCollection


def make_case_layout(p, module_items, electronics_items, mechanics):
    from model import pose_items
    items = []
    for index in range(p["compartment_count"]):
        origin = ((index - 2.5) * p["compartment_pitch_mm"], mechanics["compartment_origin_y_mm"], mechanics["compartment_origin_z_mm"])
        for item in module_items:
            items.append(dict(item, shape=item["shape"].translate(origin),
                              motion=item.get("motion", "fixed") if index == 3 else "fixed",
                              section="mechanism", compartment=index + 1))
    items.extend(dict(item, motion="fixed", section="electronics") for item in electronics_items)
    # Check each compartment raised, rather than only the rendered selected one.
    posed_module = pose_items(module_items, p, p["angle_high_deg"])
    envelopes = items + [dict(item, shape=item["shape"].translate(((index - 2.5) * p["compartment_pitch_mm"], mechanics["compartment_origin_y_mm"], mechanics["compartment_origin_z_mm"])))
                         for index in range(6) for item in posed_module]
    width, length, height = p["case_inside_mm"]
    for item in envelopes:
        b = item["shape"].BoundingBox()
        if b.xmin < -width / 2 - 0.01 or b.xmax > width / 2 + 0.01 or b.ymin < -length / 2 - 0.01 or b.ymax > length / 2 + 0.01 or b.zmin < -0.01 or b.zmax > height + 0.01:
            raise ValueError(f"{item['name']} exceeds provisional case envelope")
    # Electronics stay beyond the complete moving module sweep.
    for step in range(21):
        angle = p["angle_low_deg"] + (p["angle_high_deg"] - p["angle_low_deg"]) * step / 20
        raised = pose_items(module_items, p, angle)
        for index in range(6):
            origin = ((index - 2.5) * p["compartment_pitch_mm"], mechanics["compartment_origin_y_mm"], mechanics["compartment_origin_z_mm"])
            for item in raised:
                shape = item["shape"].translate(origin)
                bounds = shape.BoundingBox()
                for electronic in electronics_items:
                    other = electronic["shape"].BoundingBox()
                    if bounds.xmax <= other.xmin or bounds.xmin >= other.xmax or bounds.ymax <= other.ymin or bounds.ymin >= other.ymax or bounds.zmax <= other.zmin or bounds.zmin >= other.zmax:
                        continue
                    if shape.intersect(electronic["shape"]).Volume() > 0.01:
                        raise ValueError(f"Compartment {index + 1}: {item['name']} collides with {electronic['name']}")
    return items


def mesh(shape):
    vertices, triangles = shape.tessellate(0.6, 0.3)
    return [[round(v.x, 3), round(v.y, 3), round(v.z, 3)] for v in vertices], triangles


def draw(ax, items, title, elev=35, azim=-60):
    xs, ys, zs = [], [], []
    for item in items:
        vertices, triangles = mesh(item["shape"])
        ax.add_collection3d(Poly3DCollection([[vertices[k] for k in triangle] for triangle in triangles],
                                           facecolors=item["color"], edgecolors="none"))
        xs.extend(v[0] for v in vertices)
        ys.extend(v[1] for v in vertices)
        zs.extend(v[2] for v in vertices)
    bounds = [(min(xs) - 8, max(xs) + 8), (min(ys) - 8, max(ys) + 8), (min(zs) - 8, max(zs) + 8)]
    ax.set(xlim=bounds[0], ylim=bounds[1], zlim=bounds[2])
    ax.set_box_aspect([b - a for a, b in bounds])
    ax.view_init(elev=elev, azim=azim)
    ax.set_title(title, fontsize=12, pad=14)
    ax.set_axis_off()


def write_preview(out, p, case_items, lift_items, mechanics):
    from model import linkage_state, pose_items
    fig = plt.figure(figsize=(16, 10), facecolor="#f6f3ec")
    draw(fig.add_subplot(121, projection="3d"), case_items,
         "6 hůlek • 2 reproduktory • kamera + ESP vzadu", 48, -63)
    raised = pose_items(lift_items, p, p["angle_high_deg"])
    draw(fig.add_subplot(122, projection="3d"), raised,
         "Jedna přihrádka • zadní čep + krátké servo táhlo", 20, -135)
    fig.suptitle("AI kufr • jednoduchá tisknutelná konstrukce", fontsize=20, y=.94)
    fig.text(.5, .07, f"Hůlka 19–20 cm z fotografie · prostor pro hlavici Ø {p['wand_head_diameter_mm']} mm (odhad)\n"
             f"Přední konec se zvedá {mechanics['lift_mm']:.1f} mm · UGREEN 139 × 80,8 × 26,7 mm\n"
             "CAD je ověřená geometrie; vnitřek kufru a zatížený mechanismus potřebují zkoušku.",
             ha="center", fontsize=11, color="#465561")
    fig.savefig(out / "nahled.png", dpi=150, bbox_inches="tight")
    plt.close(fig)

    fig, ax = plt.subplots(figsize=(10, 12), facecolor="#f6f3ec")
    width, length, _ = p["case_inside_mm"]
    ax.add_patch(plt.Rectangle((-width / 2, -length / 2), width, length, fill=False, linewidth=3, color="#59636c"))
    for index in range(6):
        x = (index - 2.5) * p["compartment_pitch_mm"]
        ax.add_patch(plt.Rectangle((x - 23, -195), 46, 240, color="#ddb777", alpha=.7))
        ax.text(x, -70, str(index + 1), ha="center", fontsize=15)
    labels = [("powerbank_open_cradle", "UGREEN"), ("speaker_open_tower", "Reproduktor"),
              ("controller_camera_platform", "ESP + kamera"), ("microphone_strap_slider", "Mikrofon")]
    used = set()
    for item in case_items:
        for name, label in labels:
            if item["name"] != name:
                continue
            b = item["shape"].BoundingBox()
            key = (name, round(b.xmin))
            if key in used:
                continue
            used.add(key)
            ax.add_patch(plt.Rectangle((b.xmin, b.ymin), b.xlen, b.ylen, facecolor=item["color"], edgecolor="#425261", alpha=.8))
            ax.annotate(label, ((b.xmin + b.xmax) / 2, (b.ymin + b.ymax) / 2),
                        xytext=(0, 8), textcoords="offset points", ha="center", fontsize=10)
    ax.annotate("Návštěvník / přední strana", (0, -225), xytext=(0, -260), ha="center", fontsize=13,
                arrowprops=dict(arrowstyle="->", color="#435365"))
    ax.set(xlim=(-175, 175), ylim=(-275, 240), xlabel="Šířka X [mm]", ylabel="Délka Y [mm]",
           title="Rozvržení v kufru • pracovní vnitřní prostor 310 × 450 mm")
    ax.set_aspect("equal")
    fig.savefig(out / "rozlozeni.png", dpi=150, bbox_inches="tight")
    plt.close(fig)

    records = []
    for item in case_items:
        vertices, triangles = mesh(item["shape"])
        records.append(dict(v=vertices, t=triangles, c=item["color"], m=item.get("motion", "fixed"),
                            s=item["section"], n=item["name"]))
    states = [dict(servo_deg=p["angle_low_deg"] + (p["angle_high_deg"] - p["angle_low_deg"]) * i / 100,
                   **linkage_state(p, p["angle_low_deg"] + (p["angle_high_deg"] - p["angle_low_deg"]) * i / 100))
              for i in range(101)]
    origin_y, origin_z = mechanics["compartment_origin_y_mm"], mechanics["compartment_origin_z_mm"]
    settings = dict(hinge=[p["compartment_pitch_mm"] / 2, p["hinge_y_mm"] + origin_y, p["hinge_axis_z_mm"] + origin_z],
                    servo=[p["compartment_pitch_mm"] / 2, p["servo_axis_y_mm"] + origin_y, p["servo_axis_height_mm"] + origin_z],
                    crank=[p["compartment_pitch_mm"] / 2, states[0]["crank_y_mm"] + origin_y, states[0]["crank_z_mm"] + origin_z],
                    states=states, low=p["angle_low_deg"], case=p["case_inside_mm"])
    html = '''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AI kufr – tisknutelná konstrukce</title><style>
body{margin:0;background:#12212c;color:#eef2f5;font:16px system-ui}header,footer{padding:18px 24px}h1{font-size:24px;margin:0 0 8px}
p{margin:8px 0;color:#c0cbd3;max-width:1050px}canvas{display:block;width:100%;height:65vh;touch-action:none}
label,button{display:inline-block;margin:5px 15px 5px 0}button{background:#305566;color:white;border:1px solid #7998a4;padding:8px 14px;border-radius:6px}input{vertical-align:middle}
</style><header><h1>AI kufr · 6 hůlek · 2 reproduktory</h1>
<p>Tažením otáčej, kolečkem přibližuj. Zadní společný držák: ESP + kamera. Mikrofon je vpředu. Pracovní prostor 310 × 450 × 200 mm.</p>
<label>Prezentace hůlky 4 <input aria-label="Prezentace hůlky 4" id="lift" type="range" min="0" max="100" value="0"> <span id="mm">0 mm</span></label>
<button id="top">Pohled shora</button><button id="iso">Prostorový pohled</button><label><input id="electronics" type="checkbox" checked> Elektronika</label>
</header><canvas id="view" aria-label="3D náhled kufru"></canvas><footer>Všechny tištěné díly mají skutečné STL a STEP. Posuvník otáčí lůžko, páku i táhlo podle kinematiky. Rozměr hlavice, vnitřek kufru, FPC kamery a držení bez proudu vyžadují fyzické ověření. <a style="color:#a9cfeb" href="rozlozeni.png">Plánek rozmístění</a> · <a style="color:#a9cfeb" href="NAVOD.md">Návod</a></footer>
<script>const data=PAYLOAD,config=CONFIG;
const cv=document.getElementById('view'),ctx=cv.getContext('2d');let yaw=-.4,pitch=.78,zoom=1,drag=false,last=null;
function rotate(v,o,a){const c=Math.cos(a),s=Math.sin(a),y=v[1]-o[1],z=v[2]-o[2];return [v[0],o[1]+y*c-z*s,o[2]+y*s+z*c]}
function render(){const dpr=devicePixelRatio||1,w=cv.clientWidth,h=cv.clientHeight;cv.width=w*dpr;cv.height=h*dpr;ctx.scale(dpr,dpr);ctx.clearRect(0,0,w,h);
 const sc=Math.min(w/650,h/540)*zoom,cy=Math.cos(yaw),sy=Math.sin(yaw),cp=Math.cos(pitch),sp=Math.sin(pitch),r=Math.PI/180;
 const state=config.states[+document.getElementById('lift').value],home=config.states[0];
 document.getElementById('mm').textContent=((config.hinge[1]+195)*Math.sin(state.tilt_deg*r)).toFixed(1)+' mm / '+state.tilt_deg.toFixed(1)+'°';
 const project=v=>{const x=v[0],y=v[1],z=v[2]-85,a=x*cy-y*sy,b=x*sy+y*cy;return [w/2+a*sc,h/2+(b*sp-z*cp)*sc,b*cp+z*sp]};
 let polys=[];for(const m of data){if(m.s==='electronics'&&!document.getElementById('electronics').checked)continue;
 const vv=m.v.map(v=>{let q=v;if(m.m==='tray')q=rotate(v,config.hinge,-state.tilt_deg*r);
 else if(m.m==='crank')q=rotate(v,config.servo,(state.servo_deg-config.low)*r);
 else if(m.m==='link'){q=rotate(v,config.crank,(state.link_angle_deg-home.link_angle_deg)*r);q=[q[0],q[1]+state.crank_y_mm-home.crank_y_mm,q[2]+state.crank_z_mm-home.crank_z_mm]}return project(q)});
 for(const t of m.t){const a=vv[t[0]],b=vv[t[1]],c=vv[t[2]];polys.push({v:[a,b,c],z:(a[2]+b[2]+c[2])/3,c:m.c})}}
 polys.sort((a,b)=>a.z-b.z);for(const p of polys){ctx.fillStyle=p.c;ctx.beginPath();ctx.moveTo(p.v[0][0],p.v[0][1]);ctx.lineTo(p.v[1][0],p.v[1][1]);ctx.lineTo(p.v[2][0],p.v[2][1]);ctx.closePath();ctx.fill()}}
cv.onpointerdown=e=>{drag=true;last=[e.clientX,e.clientY];cv.setPointerCapture(e.pointerId)};
cv.onpointermove=e=>{if(!drag)return;yaw+=(e.clientX-last[0])*.008;pitch=Math.max(-1.5,Math.min(1.56,pitch+(e.clientY-last[1])*.008));last=[e.clientX,e.clientY];render()};
cv.onpointerup=()=>drag=false;cv.onpointercancel=()=>drag=false;cv.onwheel=e=>{e.preventDefault();zoom=Math.max(.4,Math.min(3,zoom*Math.exp(-e.deltaY*.001)));render()};
document.getElementById('lift').oninput=render;document.getElementById('electronics').onchange=render;
document.getElementById('top').onclick=()=>{yaw=0;pitch=1.56;render()};document.getElementById('iso').onclick=()=>{yaw=-.4;pitch=.78;zoom=1;render()};
addEventListener('resize',render);render();</script></html>'''
    (out / "nahled.html").write_text(html.replace("PAYLOAD", json.dumps(records, separators=(",", ":")))
                                    .replace("CONFIG", json.dumps(settings, separators=(",", ":"))), encoding="utf-8")


def write_bom(out, parts, case_items, filename="tiskovy-kusovnik.csv", accessories=True):
    counts = Counter(item["name"] for item in case_items)
    if accessories:
        counts.update(cable_clip=12, fit_coupon=1, head_fit_gauge=1)
    rows = []
    for name, shape in parts.items():
        b = shape.BoundingBox()
        rows.append([name, counts[name], round(b.xlen, 1), round(b.ylen, 1), round(b.zlen, 1),
                     "PETG", "Orient/support in slicer; see NAVOD.md"])
    with (out / filename).open("w", encoding="utf-8-sig", newline="") as file:
        writer = csv.writer(file)
        writer.writerow(["part", "quantity", "x_mm", "y_mm", "z_mm", "material", "note"])
        writer.writerows(rows)
