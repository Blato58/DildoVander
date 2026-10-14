"""Render actual BREP tessellations with VTK; export standalone WebGL viewer."""
from __future__ import annotations

import json
import math
import argparse
import html
import vtk
import numpy as np

from geometry import ROOT,COLORS,PARAMS,bounds,Item,box
from build import OUT
from check_fit import scene
import mechanism
import chassis
import inspect_data
import harness
import layout
from viewer_style import snapshot_styles
from viewer_document import render_document


def mesh(shape):
    vertices,faces=shape.tessellate(0.15,0.15)
    return [[round(v.x,5),round(v.y,5),round(v.z,5)] for v in vertices],faces


def actor(item):
    vertices,faces=mesh(item.shape)
    points=vtk.vtkPoints()
    for v in vertices:
        points.InsertNextPoint(*v)
    cells=vtk.vtkCellArray()
    for face in faces:
        cells.InsertNextCell(3)
        for i in face:
            cells.InsertCellPoint(i)
    poly=vtk.vtkPolyData()
    poly.SetPoints(points)
    poly.SetPolys(cells)
    normals=vtk.vtkPolyDataNormals()
    normals.SetInputData(poly)
    normals.SetFeatureAngle(45)
    mapper=vtk.vtkPolyDataMapper()
    mapper.SetInputConnection(normals.GetOutputPort())
    result=vtk.vtkActor()
    result.SetMapper(mapper)
    result.GetProperty().SetColor(*COLORS[item.color])
    result.GetProperty().SetAmbient(0.35)
    result.GetProperty().SetDiffuse(0.65)
    result.GetProperty().SetSpecular(0.1)
    return result


def cable_actor(route,transport=False):
    """The exact harness polyline; tube radius is display-only, not bundle size."""
    coordinates=(route.get("transport_points") or route["points"]) if transport else route["points"]
    points=vtk.vtkPoints()
    for coordinate in coordinates:
        points.InsertNextPoint(*coordinate)
    line=vtk.vtkPolyLine()
    line.GetPointIds().SetNumberOfIds(len(coordinates))
    for index in range(len(coordinates)):
        line.GetPointIds().SetId(index,index)
    cells=vtk.vtkCellArray()
    cells.InsertNextCell(line)
    poly=vtk.vtkPolyData()
    poly.SetPoints(points)
    poly.SetLines(cells)
    for key in ("id","from","to","net","bundle"):
        metadata=vtk.vtkStringArray()
        metadata.SetName(key)
        metadata.InsertNextValue(str(route.get(key,"unknown")))
        poly.GetFieldData().AddArray(metadata)
    tube=vtk.vtkTubeFilter()
    tube.SetInputData(poly)
    tube.SetRadius(.55)
    tube.SetNumberOfSides(6)
    tube.CappingOn()
    mapper=vtk.vtkPolyDataMapper()
    mapper.SetInputConnection(tube.GetOutputPort())
    result=vtk.vtkActor()
    result.SetMapper(mapper)
    result.GetProperty().SetColor(*{"power":(.7,.16,.12),"signal":(.15,.4,.65),"ground":(.3,.26,.2)}.get(route["kind"],(.6,.4,.15)))
    return result


def render(items,path,title,camera,target,parallel=300,wires=False,transport=False):
    renderer=vtk.vtkRenderer()
    renderer.SetBackground(0.945,0.929,0.898)
    for item in items:
        rendered=actor(item)
        if transport and item.group in ("case","lid","cover"):
            rendered.GetProperty().SetOpacity(.10 if item.group=="lid" else .22)
        renderer.AddActor(rendered)
    if wires:
        for route in harness.routes():
            renderer.AddActor(cable_actor(route,transport))
    text=vtk.vtkTextActor()
    text.SetInput(title)
    text.GetTextProperty().SetFontSize(26)
    text.GetTextProperty().SetColor(0.14,0.10,0.17)
    text.SetPosition(35,1025)
    renderer.AddActor2D(text)
    camera_obj=renderer.GetActiveCamera()
    camera_obj.SetPosition(*camera)
    camera_obj.SetFocalPoint(*target)
    camera_obj.SetViewUp(0,1,0) if abs(camera[0]-target[0])+abs(camera[1]-target[1])<1 else camera_obj.SetViewUp(0,0,1)
    camera_obj.ParallelProjectionOn()
    camera_obj.SetParallelScale(parallel)
    window=vtk.vtkRenderWindow()
    window.SetOffScreenRendering(1)
    window.SetSize(1600,1100)
    window.SetMultiSamples(4)
    window.AddRenderer(renderer)
    renderer.ResetCamera()
    camera_obj.SetParallelScale(camera_obj.GetParallelScale()*1.12)
    renderer.ResetCameraClippingRange()
    window.Render()
    capture=vtk.vtkWindowToImageFilter()
    capture.SetInput(window)
    capture.Update()
    writer=vtk.vtkPNGWriter()
    writer.SetFileName(str(path))
    writer.SetInputConnection(capture.GetOutputPort())
    writer.Write()
    window.Finalize()


def viewer():
    inspect_data.snapshot_sources()
    inspect_data.assert_snapshot_current()
    evidence=json.loads(inspect_data.SNAPSHOT.read_text(encoding="utf-8"))["sources"]
    sections=[]
    for record,anchor,title in zip(evidence,("inventar","zapojeni"),("Inventář a rozměry","Potvrzené elektrické zapojení")):
        sections.append(f'<section id="{anchor}"><h2>{title}</h2><p>{html.escape(record["name"])}</p>'
                        f'<p>SHA-256: <code>{record["sha256"]}</code></p><pre>{html.escape(record["text"])}</pre></section>')
    document='<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Podklady F2 · Arcane Workshop</title>'
    document+='<style>'+snapshot_styles().read_text(encoding="utf-8")+'</style>'
    document+='<style>body{max-width:1100px;margin:32px auto;padding:0 20px}pre{white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.5;font-family:var(--aw-font-mono)}code{overflow-wrap:anywhere}section{margin-top:40px;padding-top:24px;border-top:1px solid var(--aw-border)}nav{display:flex;flex-wrap:wrap;gap:16px}nav a{min-height:44px;display:inline-flex;align-items:center}</style><body class="aw-root">'
    document+='<h1>Úplné zdrojové podklady F2</h1><p>Ověřený otisk zdrojových dokumentů přibalený pro samostatné použití. Text včetně původních odkazů je zachovaný; odkazy uvnitř přepisu odkazují do původního repozitáře.</p>'
    document+='<nav aria-label="Zdrojové podklady"><a href="#inventar">Inventář a rozměry</a><a href="#zapojeni">Zapojení</a><a href="viewer.html">3D sestava</a></nav>'+''.join(sections)+'</body></html>'
    OUT.mkdir(parents=True,exist_ok=True)
    (OUT/"reference-sources.html").write_text(document,encoding="utf-8")
    meshes=[]
    items=[item for item in scene(lid=0) if not item.name.startswith("spare_")]
    normal_names={item.name for item in items}
    transport_items=[item for item in scene(lid=0,transport=True) if not item.name.startswith("spare_")]
    transport_names={item.name for item in transport_items}
    items += [item for item in transport_items if item.name not in normal_names]
    for item in items:
        vertices,faces=mesh(item.shape)
        # Preserve exact tessellated facets without repeating every position
        # and normal in JSON. The viewer expands flat normals once for the GPU.
        meshes.append({"name":item.name,"group":item.group,"color":COLORS[item.color],
                       "transportOnly":item.name not in normal_names,
                       "operationOnly":item.name not in transport_names,
                       "bounds":[round(v,4) for v in bounds(item.shape)],
                       "info":inspect_data.describe(item),
                       "vertices":[round(float(x),4) for vertex in vertices for x in vertex],
                       "triangles":[int(index) for face in faces for index in face]})
    names = {item.name for item in items}
    def optional_json(name,default):
        path=ROOT/name
        return json.loads(path.read_text(encoding="utf-8")) if path.exists() else default
    payload={"revision":"F2","meshes":meshes,"hinge":chassis.HINGE,"mechanism":mechanism.M,
             "case":PARAMS["case"],"routes":harness.routes(),
             "endpoints":harness.endpoint_registry(),
             "cameraFpc":harness.CAMERA_FPC,"supplyConnector":harness.SUPPLY_CONNECTOR,
             "assemblyGuide":optional_json("assembly-guide.json",{"steps":[]}),
             "effects":optional_json("effects.json",{}),
             "connections":inspect_data.connection_data(names) +
                           [edge for edge in harness.CONNECTIONS if edge["from"] in names and edge["to"] in names],
             "sources":inspect_data.source_evidence()}
    OUT.mkdir(parents=True,exist_ok=True)
    (OUT/"viewer.html").write_text(render_document(payload),encoding="utf-8")


def main():
    (OUT/"images").mkdir(parents=True,exist_ok=True)
    render(scene(160),OUT/"images"/"open.png","F2 | Wood case insert - tray 3 raised; dimensions provisional",(750,-640,650),(216,133,65),245)
    print("Rendered open",flush=True)
    service=[i for i in scene(covers=False) if not i.group.startswith("lid") and i.group!="wand" and not i.name.startswith("tray_")]
    render(service,OUT/"images"/"service.png","F2 | Service - real CAD; no physical acceptance",(680,-540,760),(216,133,40),205)
    top=[i for i in scene() if not i.group.startswith("lid")]
    render(top,OUT/"images"/"top.png","F2 | Top - six independent trays and two service zones",(216,133,900),(216,133,40),205)
    render(service,OUT/"images"/"wiring.png","F2 | Actual planned wire routes; pins and flexible loops unmeasured",(680,-540,760),(216,133,40),205,wires=True)
    print("Rendered service",flush=True)
    render(scene(lid=0,transport=True),OUT/"images"/"transport.png","F2 | Transport - transparent case; six ties; bank and wands removed",(700,-650,520),(216,133,65),220,wires=True,transport=True)
    anchors=[i for i in scene(covers=False) if i.name.startswith(("anchor_","floor_anchor_","wood_bond_"))]
    render(anchors,OUT/"images"/"anchors.png","F2 | Four printed floor cradles; frame hidden; bond untested",(650,-500,600),(216,133,8),220)
    module=mechanism.module(0,80,False)
    render(module,OUT/"images"/"mechanism.png","F2 | Captured cam and lower stops; pulse-off behaviour untested",(230,-170,200),(100,140,55),120)
    # Cutaway shows the actual integral peg and lug without the bed hiding it.
    detail=[i for i in mechanism.module(0,80,False) if not i.name.startswith(("tray_","u059_","led_"))]
    m=PARAMS["mechanism"]
    bed=next(i for i in module if i.name=="tray_1")
    lug=bed.shape.intersect(box(12,16,110,(m["origin_x"]+35,m["origin_y"]+104,m["origin_z"])))
    detail.append(Item("tray_lug_cutaway",lug,"moving","tray"))
    render(detail,OUT/"images"/"cam.png","F2 | Bed cutaway: integral sliding peg in captured cam",(360,80,140),(100,145,45),105)
    print("Rendered mechanism and transport",flush=True)
    left,right=mechanism.tray_halves()
    render([Item('bed_left',left.translate((-12,0,0)),'fixed','mechanism'),
            Item('bed_right',right.translate((12,0,0)),'fixed','tray')],
           OUT/'images'/'bed-exploded.png','CAD exploded bed | 2 keyed halves; offsets illustrative',
           (230,-170,200),(23,106,-5),120)
    for carrier,centre in (('left_service_carrier',(42,88,55)),('right_service_carrier',(388,194,55))):
        exploded=[]
        for name,part in layout.service_components()[0].items():
            if not name.startswith(carrier+'_'): continue
            offset=(-15,0,0) if name.endswith('frame_left') else (15,0,0) if name.endswith('frame_right') else (0,0,16*int(name[-1]))
            exploded.append(Item(name,part.shape.translate(offset),'fixed','mechanism' if '_frame_' in name else 'tray'))
        render(exploded,OUT/'images'/f'{carrier}-exploded.png',
               'CAD exploded cassette | 2 frames + 3 tiers; bench assembly',
               (centre[0]+180,centre[1]-190,210),centre,80)
    for group,shapes,centre,filename in (
        ('camera',layout.camera_parts(),(215.5,248,60),'camera-exploded.png'),
        ('controls',layout.switch_parts(),(44,39,67),'controls-exploded.png')):
        exploded=[]
        for name,shape in shapes.items():
            offset=(-12,0,0) if name.endswith('left') else (12,0,0) if name.endswith('right') else (0,0,18)
            exploded.append(Item(name,shape.translate(offset),'fixed','mechanism' if name.endswith(('left','right')) else 'tray'))
        render(exploded,OUT/'images'/filename,f'CAD exploded {group} | 2 sides + plate; offsets illustrative',
               (centre[0]+160,centre[1]-170,205),centre,72)
    print('Rendered five split-assembly illustrations',flush=True)
    viewer()
    print("Standalone viewer written",flush=True)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description="Render F2 CAD previews or regenerate only the offline viewer.")
    parser.add_argument("--viewer-only",action="store_true",help="Skip PNG rendering and regenerate exports/viewer.html only")
    args=parser.parse_args()
    if args.viewer_only:
        viewer()
        print("Standalone viewer written",flush=True)
    else:
        main()
