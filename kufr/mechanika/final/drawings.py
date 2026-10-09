"""Dimensioned drawings derived from the same parameters as the CAD."""
import math
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle
import numpy as np

from geometry import PARAMS
from build import OUT
import chassis
import mechanism as m


def dimension(ax,a,b,label,offset=0):
    ax.annotate("",xy=b,xytext=a,arrowprops={"arrowstyle":"<->","color":"#65566b","lw":1})
    ax.text((a[0]+b[0])/2,(a[1]+b[1])/2+offset,label,ha="center",va="center",fontsize=10,
            bbox={"facecolor":"#faf8f3","edgecolor":"none","pad":2})


def main():
    plt.rcParams.update({"font.family":"DejaVu Sans","font.size":10,"svg.fonttype":"none"})
    fig,(ax,side)=plt.subplots(1,2,figsize=(16,8),gridspec_kw={"width_ratios":[1.7,1]},facecolor="#faf8f3")
    for a in (ax,side):a.set_facecolor("#faf8f3")
    X,Y=chassis.X,chassis.Y
    ax.add_patch(Rectangle((0,0),X,Y,fill=False,lw=3,color="#573822"))
    ax.add_patch(Rectangle((4,4),X-8,Y-8,fill=False,lw=1,ls="--",color="#80644e"))
    for i in range(6):
        x=m.M["origin_x"]+i*m.M["pitch"]
        ax.add_patch(Rectangle((x,34),46,212,facecolor="#d8ccdF",edgecolor="#53435a"))
        ax.text(x+23,140,f"{i+1}",ha="center",fontsize=22,color="#49374f")
        ax.text(x+8,170,"212 × 44",ha="center",rotation=90,fontsize=8)
    for x in (7,368):ax.add_patch(Rectangle((x,7),64,257,facecolor="#ece2cf",edgecolor="#7b6a52"))
    ax.add_patch(Rectangle((18.5,123),26.5,139,facecolor="#7c8590"))
    ax.text(32,193,"PB 139 × 80 × 26,5\nstojí na hraně",rotation=90,ha="center",va="center",fontsize=9,color="white")
    for x in (18,403):ax.add_patch(Rectangle((x,19),21,100,facecolor="#9a8c7c"))
    ax.text(396,201,"Měnič\nPCB\nrozvod",ha="center",va="center")
    ax.text(395,65,"ESP\nkamera",ha="center")
    for x,label,color in ((22,"H","#a5a5a5"),(56,"P","#a5a5a5"),(38,"B","white")):
        y=19 if label=="B" else 40.5
        ax.add_patch(plt.Circle((x,y),6 if label=="B" else 10,fc=color,ec="#45374d"))
        ax.text(x,y,label,ha="center",va="center",fontsize=8)
    dimension(ax,(0,-18),(X,-18),f"{X:.1f} mm",-5)
    dimension(ax,(-16,0),(-16,Y),f"{Y:.1f}",-3)
    dimension(ax,(100,282),(148,282),"rozteč 48",4)
    ax.text(220,-45,"OBSLUHA / návštěvník   (Y−)",ha="center",fontsize=12,weight="bold")
    ax.set_xlim(-35,460);ax.set_ylim(304,-60);ax.set_aspect("equal");ax.axis("off")
    ax.set_title("Půdorys a servisní zóny · všechny rozměry v mm",loc="left",pad=15)
    side.add_patch(Rectangle((0,0),274.7,118.3,fill=False,lw=2,color="#573822"))
    side.add_patch(Rectangle((0,119.3),274.7,30,fill=False,lw=2,color="#573822"))
    for z,col,label in ((72,"#86718e","lůžko v klidu"),(88,"#49374f","lůžko nahoře")):
        side.add_patch(Rectangle((34,z),212,3,facecolor=col))
        side.text(138,z+5,label,ha="center",fontsize=9,color=col)
    side.add_patch(Rectangle((40,93),55,28,fill=False,ls="--",edgecolor="#9b642f"))
    side.text(47,130,"hlavice při prezentaci\naž Z121; jen otevřený kufr",fontsize=8)
    dimension(side,(286,0),(286,149.3),"149,3",0)
    dimension(side,(257,72),(257,88),"16",0)
    side.text(0,-8,"Stěny / dno 4 · víko 30 · spára 1\nPodložka 220 × 220 · největší díl 214 × 202,7",fontsize=11,va="top")
    side.text(0,-42,"B = jediné viditelné bílé tlačítko\nH = skrytý hlavní vypínač\nP = skryté odpojení pohonů\n\nRozměry hůlky a kamery obsahují předpoklady.\nKování a pružné kabely ověřit fyzicky.",fontsize=10,va="top")
    side.set_xlim(-12,310);side.set_ylim(-100,180);side.set_aspect("equal");side.axis("off")
    side.set_title("Výšková kontrola",loc="left")
    fig.suptitle("F1 — NOVÝ KOUZELNÝ KUFR",fontsize=23,weight="bold",x=.05,ha="left")
    fig.tight_layout(rect=(0,0,1,.94))
    fig.savefig(OUT/"layout.svg",facecolor=fig.get_facecolor())
    fig.savefig(OUT/"layout.png",dpi=150,facecolor=fig.get_facecolor())
    plt.close(fig)
    angles=np.linspace(0,160,641)
    lift=np.array([m.cam_law(a)[0]-m.M["cam_low_radius"] for a in angles])
    fig,(left,right)=plt.subplots(1,2,figsize=(13,5),facecolor="#faf8f3")
    points=np.array(m.cam_points())
    left.plot(points[:,0],points[:,1],color="#72552d",lw=3,label="střed drážky")
    left.scatter([0],[0],color="#49374f",s=50,label="osa serva")
    left.scatter([0],[10],color="#337272",s=50,label="dolní poloha válečku")
    left.set_aspect("equal");left.set_xlabel("Y od osy [mm]");left.set_ylabel("Z od osy [mm]")
    left.legend(loc="lower left",fontsize=8);left.grid(alpha=.2)
    right.plot(angles,lift,color="#49374f",lw=3)
    right.axvspan(0,20,color="#b5cfc0",alpha=.4);right.axvspan(140,160,color="#b5cfc0",alpha=.4)
    right.set_xlabel("mechanický úhel vačky [°]");right.set_ylabel("zdvih [mm]")
    right.set_ylim(-1,19);right.grid(alpha=.2)
    right.set_title("Kruhové konce + plynulý přechod")
    fig.suptitle("F1 · uzavřená drážka aktivně zvedá i vrací lůžko",fontsize=17)
    fig.tight_layout(rect=(0,0,1,.92));fig.savefig(OUT/"kinematika.svg",facecolor=fig.get_facecolor());plt.close(fig)
    print("Dimensioned SVG/PNG drawings written",flush=True)


if __name__=="__main__":main()
