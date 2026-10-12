"""F2 drawings from the same parameters, CAD bounds and terminal routes."""
import cadquery as cq
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle
import numpy as np

from geometry import PARAMS, bounds
from build import OUT
import chassis
import mechanism as m
import layout
import harness


BACKGROUND = "#faf8f3"


def dimension(ax, start, end, label, offset=0):
    ax.annotate("", xy=end, xytext=start,
                arrowprops={"arrowstyle":"<->", "color":"#65566b", "lw":1})
    ax.text((start[0]+end[0])/2, (start[1]+end[1])/2+offset, label,
            ha="center", va="center", fontsize=9,
            bbox={"facecolor":BACKGROUND, "edgecolor":"none", "pad":2})


def section_lines(shape, plane, origin, axes):
    """Sample edges of the actual BREP section; no alternate outline geometry."""
    section = cq.Workplane(plane, origin=origin).newObject([shape]).section().val()
    result = []
    for edge in section.Edges():
        count = 2 if edge.geomType() == "LINE" else 129
        points = [edge.positionAt(float(t)).toTuple() for t in np.linspace(0,1,count)]
        result.append(np.array([[point[axes[0]],point[axes[1]]] for point in points]))
    return result


def plot_section(ax, shape, plane, origin, axes, **style):
    for points in section_lines(shape,plane,origin,axes):
        ax.plot(points[:,0],points[:,1],**style)


def plan_axes(ax):
    case = PARAMS["case"]
    ax.set_facecolor(BACKGROUND)
    plot_section(ax,chassis.shell(),"XY",(0,0,case["base_height"]-.1),(0,1),
                 color="#573822",lw=1.5)
    ax.add_patch(Rectangle((0,0),case["inside_x"],case["inside_y"],
                           fill=False,lw=1,ls=":",edgecolor="#35778b"))
    ax.add_patch(Rectangle((0,case["brace_y"]),case["inside_x"],case["brace_width"],
                           facecolor="#d5c9ad",edgecolor="#94783f",alpha=.35,hatch="//"))
    origin = PARAMS["frame"]["origin"]
    size = PARAMS["frame"]["size"]
    ax.add_patch(Rectangle(origin[:2],size[0],size[1],fill=False,
                           ls="--",edgecolor="#9b642f"))
    shell_bounds = bounds(chassis.shell())
    ax.set_xlim(shell_bounds[0]-20,shell_bounds[3]+25)
    ax.set_ylim(shell_bounds[4]+28,shell_bounds[1]-25)
    ax.set_aspect("equal")
    ax.axis("off")


def hardware_plan(ax, labels=True):
    for item in layout.hardware():
        x,y,z,xx,yy,zz = bounds(item.shape)
        ax.add_patch(Rectangle((x,y),xx-x,yy-y,facecolor="#deddd2",
                               edgecolor="#686655",alpha=.55))
        if labels:
            label = {"degson_1":"D1 MAIN5", "degson_2":"D2 DRIVE5",
                     "degson_3":"D3 GND"}.get(item.name,item.name)
            ax.text((x+xx)/2,(y+yy)/2,label+f"\nZ {z:.0f}..{zz:.0f}",
                    ha="center",va="center",fontsize=5.5)


def save_figure(fig, name, png=True):
    fig.tight_layout()
    fig.savefig(OUT/(name+".svg"),facecolor=fig.get_facecolor())
    if png:
        fig.savefig(OUT/(name+".png"),dpi=150,facecolor=fig.get_facecolor())
    plt.close(fig)


def main():
    plt.rcParams.update({"font.family":"DejaVu Sans","font.size":9,"svg.fonttype":"none"})
    fig,(top,side)=plt.subplots(1,2,figsize=(17,9),
                             gridspec_kw={"width_ratios":[1.7,1]},facecolor=BACKGROUND)
    plan_axes(top)
    hardware_plan(top)
    for index in range(m.M["count"]):
        x = m.M["origin_x"] + index*m.M["pitch"]
        y = m.M["origin_y"]
        top.add_patch(Rectangle((x,y),m.M["base_width"],m.M["length"],
                                fill=False,edgecolor="#53435a",lw=1.2))
        top.text(x+m.M["base_width"]/2,y+m.M["length"]/2,str(index+1),
                 ha="center",fontsize=20,color="#49374f")
    case = PARAMS["case"]
    dimension(top,(0,-30),(case["inside_x"],-30),f"clear top X {case['inside_x']:.1f} mm",-5)
    top.text(case["inside_x"]/2,case["inside_y"]+30,
             f"Clear top rectangle {case['inside_x']:.1f} x {case['inside_y']:.1f} between rim ledges; outer {case['outer_x']:.0f} x {case['outer_y']:.0f}",ha="center")
    top.text(case["inside_x"]/2,case["inside_y"]+45,
             f"Hatched brace: {case['brace_width']:.0f} mm Y x {case['brace_height']:.0f} mm Z across full X; dashed frame is design placement",ha="center",fontsize=8)
    top.annotate("",xy=(case["inside_x"]+15,case["inside_y"]),xytext=(case["inside_x"]+15,0),
                 arrowprops={"arrowstyle":"<->","color":"#35778b","lw":1})
    top.text(case["inside_x"]+20,case["inside_y"]/2,f"clear top Y {case['inside_y']:g} mm",
             rotation=90,ha="center",va="center",fontsize=8,
             bbox={"facecolor":BACKGROUND,"edgecolor":"none","pad":2})
    top.set_title("F2 / purchased case BREP, clear top opening and component footprints")
    side.set_facecolor(BACKGROUND)
    section_x = case["inside_x"]/2
    plot_section(side,chassis.shell(),"YZ",(section_x,0,0),(1,2),color="#573822",lw=2)
    plot_section(side,chassis.lid_position(chassis.shell(True),0),"YZ",(section_x,0,0),(1,2),
                 color="#573822",lw=1.2,ls="--")
    side.add_patch(Rectangle((case["brace_y"],0),case["brace_width"],case["brace_height"],
                             fc="#d5c9ad",ec="#94783f",hatch="//"))
    side.plot([0,case["inside_y"]],[case["base_height"]]*2,color="#35778b",ls=":",lw=1)
    side.text(case["inside_y"]/2,case["base_height"]+5,"Clear top Y; not full-height clear void",ha="center",fontsize=8)
    ledge_width = case.get("reinforcement_width",case["wall"])
    front_drop = case.get("front_reinforcement_height",0)
    rear_drop = case.get("rear_reinforcement_height",0)
    radius = case.get("bottom_radius",0)
    projection = ledge_width-case["wall"]
    flat_front, flat_rear = radius-projection, case["inside_y"]+projection-radius
    if radius:
        side.scatter([flat_front,flat_rear],[0,0],s=12,color="#35778b")
        side.text(case["inside_y"]/2,22,
                  f"Inner R{radius:g}; floor tangencies Y{flat_front:g}..{flat_rear:g}",
                  ha="center",fontsize=8,color="#35778b")
    if front_drop or rear_drop:
        side.text(case["inside_y"]/2,case["base_height"]+case["lid_height"]-25,
                  f"Rim width {ledge_width:g} includes wall {case['wall']:g}\nFront {front_drop:g} / rear {rear_drop:g} down from rim",
                  ha="center",fontsize=8)
    frame = PARAMS["frame"]
    side.add_patch(Rectangle((frame["origin"][1],frame["origin"][2]),frame["size"][1],frame["size"][2],fc="#82758a"))
    for raised,color in ((False,"#86718e"),(True,"#49374f")):
        z=harness.REST_TRAY_Z + (m.M["lift"] if raised else 0)
        side.add_patch(Rectangle((m.M["origin_y"],z),m.M["length"],m.M["tray_thickness"],fc=color))
        side.text(m.M["origin_y"]+m.M["length"]/2,z+5,
                  "raised" if raised else "rest",ha="center",color=color)
    dimension(side,(case["inside_y"]+10,harness.REST_TRAY_Z),
              (case["inside_y"]+10,harness.REST_TRAY_Z+m.M["lift"]),f"{m.M['lift']:.0f} mm")
    side.text(0,-15,f"Section follows chassis.shell() at X{section_x:g}.\nFloor {case['floor']:g} mm / lid {case['lid_height']:g} mm assumed.\nBounds are not guaranteed clear void; dry fit required.",va="top")
    side.set_aspect("equal");side.axis("off")
    shell_bounds=bounds(chassis.shell())
    side.set_xlim(shell_bounds[1]-10,shell_bounds[4]+40);side.set_ylim(-45,case["outer_z"]+10)
    side.set_title("BREP YZ section / rounded lower profile, rim ledges and centered brace")
    save_figure(fig,"layout")
    cable_drawing()
    kinematic_drawing()
    print("F2 dimensioned layout, wiring and kinematics drawings written",flush=True)


def cable_drawing():
    fig,ax=plt.subplots(figsize=(15,10),facecolor=BACKGROUND)
    plan_axes(ax)
    hardware_plan(ax)
    colors={"power":"#b94436","ground":"#596267","signal":"#356fb0"}
    for wire in harness.routes():
        points=np.array(wire["points"])
        ax.plot(points[:,0],points[:,1],color=colors[wire["kind"]],
                lw=max(.5,wire["outer_diameter_mm"]*.25),alpha=.45)
    for index in range(1,m.M["count"]+1):
        reserve=harness.loop_reserve(index)
        x,y,z=reserve["origin"];w,d,h=reserve["size"]
        ax.add_patch(Rectangle((x,y),w,d,fill=False,ls=":",edgecolor="#75578a"))
        ax.text(x+w/2,y+d/2,f"loop {index}",ha="center",fontsize=7,color="#75578a")
    coil=harness.USB_COIL
    ax.add_patch(Rectangle(coil["origin"][:2],coil["size"][0],coil["size"][1],
                           fill=False,ls=":",edgecolor="#a46b22"))
    ax.text(coil["origin"][0],coil["origin"][1]-4,"USB-C coil / YZ plane",fontsize=7)
    park=harness.USB_PARK_POINT
    ax.scatter([park[0]],[park[1]],marker="x",color="#a46b22")
    ax.text(park[0],park[1]-4,"unplugged park",fontsize=7)
    case=PARAMS["case"]
    ax.text(case["inside_x"]/2,case["inside_y"]+35,
            "Red power / grey GND / blue signals; independent tray loops; exact pins and cut lengths in cable-plan.json",ha="center",fontsize=8)
    ax.text(case["inside_x"]/2,-35,
            "Routing anchors require rounded bends and finite-wire clearance verification; no measured pin positions",ha="center",fontsize=8)
    ax.set_title("F2 physical wiring / all polylines from harness.py / unchanged GPIO and PCA channels")
    save_figure(fig,"cable-layout")


def kinematic_drawing():
    angles=np.linspace(0,m.M["travel_degrees"],641)
    lift=np.array([m.cam_law(a)[0]-m.M["cam_low_radius"] for a in angles])
    fig,(left,right)=plt.subplots(1,2,figsize=(13,5),facecolor=BACKGROUND)
    points=np.array(m.cam_points())
    left.plot(points[:,0],points[:,1],color="#72552d",lw=3,label="groove centre")
    left.scatter([0],[0],color="#49374f",label="servo axis")
    left.scatter([0],[m.M["cam_low_radius"]],color="#337272",label="rest follower")
    left.set_aspect("equal");left.set_xlabel("Y relative to axis [mm]");left.set_ylabel("Z relative to axis [mm]")
    left.legend(fontsize=8);left.grid(alpha=.2)
    right.plot(angles,lift,color="#49374f",lw=3)
    dwell,travel=m.M["dwell_degrees"],m.M["travel_degrees"]
    right.axvspan(0,dwell,color="#b5cfc0",alpha=.4)
    right.axvspan(travel-dwell,travel,color="#b5cfc0",alpha=.4)
    right.set_xlabel("cam angle [deg]");right.set_ylabel("lift [mm]")
    right.set_ylim(-1,m.M["lift"]+3);right.grid(alpha=.2)
    fig.suptitle("F2 / captured cam return; lower supports and removable transport ties")
    save_figure(fig,"kinematika",png=False)


if __name__=="__main__":
    main()
