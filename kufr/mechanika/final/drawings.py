"""F2 drawings from the same parameters, CAD bounds and terminal routes."""
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


def plan_axes(ax):
    case = PARAMS["case"]
    ax.set_facecolor(BACKGROUND)
    ax.add_patch(Rectangle((0,0),case["inside_x"],case["inside_y"],
                           fill=False, lw=2, edgecolor="#573822"))
    origin = PARAMS["frame"]["origin"]
    size = PARAMS["frame"]["size"]
    ax.add_patch(Rectangle(origin[:2],size[0],size[1],fill=False,
                           ls="--",edgecolor="#9b642f"))
    ax.set_xlim(-25,case["inside_x"]+25)
    ax.set_ylim(case["inside_y"]+25,-35)
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
    dimension(top,(0,-12),(case["inside_x"],-12),f"{case['inside_x']:.1f} mm",-5)
    top.text(case["inside_x"]/2,case["inside_y"]+20,
             f"Interior {case['inside_x']:.1f} x {case['inside_y']:.1f}; module pitch {m.M['pitch']:.0f}",ha="center")
    top.set_title("F2 / actual wooden case, parametric active component footprints")
    side.set_facecolor(BACKGROUND)
    side.add_patch(Rectangle((0,0),case["inside_y"],case["base_height"],
                             fill=False,lw=2,edgecolor="#573822"))
    side.add_patch(Rectangle((0,case["base_height"]),case["inside_y"],case["lid_height"],
                             fill=False,lw=2,ls="--",edgecolor="#573822"))
    frame = PARAMS["frame"]
    side.add_patch(Rectangle((frame["origin"][1],frame["origin"][2]),frame["size"][1],frame["size"][2],fc="#82758a"))
    for raised,color in ((False,"#86718e"),(True,"#49374f")):
        z=harness.REST_TRAY_Z + (m.M["lift"] if raised else 0)
        side.add_patch(Rectangle((m.M["origin_y"],z),m.M["length"],m.M["tray_thickness"],fc=color))
        side.text(m.M["origin_y"]+m.M["length"]/2,z+5,
                  "raised" if raised else "rest",ha="center",color=color)
    dimension(side,(case["inside_y"]+10,harness.REST_TRAY_Z),
              (case["inside_y"]+10,harness.REST_TRAY_Z+m.M["lift"]),f"{m.M['lift']:.0f} mm")
    side.text(0,-15,"Shell is reference geometry only.\nLid and connector details require dry fit.\nGeometry is not physical acceptance.",va="top")
    side.set_aspect("equal");side.axis("off")
    side.set_xlim(-10,case["inside_y"]+40);side.set_ylim(-45,case["outer_z"]+10)
    side.set_title("Actual interior + empty lid; measured envelope / provisional partitions")
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
    ax.text(case["inside_x"]/2,case["inside_y"]+18,
            "Red power / grey GND / blue signals; independent tray loops; exact pins and cut lengths in cable-plan.json",ha="center",fontsize=8)
    ax.text(case["inside_x"]/2,-18,
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
