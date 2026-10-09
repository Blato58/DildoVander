"""Render actual BREP tessellations with VTK; export standalone WebGL viewer."""
from __future__ import annotations

import json
import math
import argparse
import vtk
import numpy as np

from geometry import ROOT,COLORS,bounds
from build import OUT
from check_fit import scene
import mechanism
import chassis
import inspect_data


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


def render(items,path,title,camera,target,parallel=300):
    renderer=vtk.vtkRenderer()
    renderer.SetBackground(0.945,0.929,0.898)
    for item in items:
        renderer.AddActor(actor(item))
    text=vtk.vtkTextActor()
    text.SetInput(title)
    text.GetTextProperty().SetFontSize(26)
    text.GetTextProperty().SetColor(0.14,0.10,0.17)
    text.SetPosition(35,1025)
    renderer.AddActor2D(text)
    camera_obj=renderer.GetActiveCamera()
    camera_obj.SetPosition(*camera)
    camera_obj.SetFocalPoint(*target)
    camera_obj.SetViewUp(0,0,1)
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
    meshes=[]
    items=[item for item in scene(lid=0) if not item.name.startswith("spare_")]
    for item in items:
        vertices,faces=mesh(item.shape)
        # Flat normals make the actual printed facets and clearances legible.
        packed=[]
        v=np.array(vertices)
        for face in faces:
            tri=v[list(face)]
            n=np.cross(tri[1]-tri[0],tri[2]-tri[0])
            length=np.linalg.norm(n)
            if length<1e-12:
                continue
            n/=length
            for p in tri:
                packed.extend([*p,*n])
        meshes.append({"name":item.name,"group":item.group,"color":COLORS[item.color],
                       "bounds":[round(v,4) for v in bounds(item.shape)],
                       "info":inspect_data.describe(item),
                       "data":[round(float(x),4) for x in packed]})
    template=(ROOT/"viewer-template.html").read_text(encoding="utf-8")
    payload={"meshes":meshes,"hinge":chassis.HINGE,"mechanism":mechanism.M,
             "connections":inspect_data.connection_data({i.name for i in items}),
             "sources":inspect_data.source_evidence()}
    OUT.mkdir(parents=True,exist_ok=True)
    script=(ROOT/"viewer.js").read_text(encoding="utf-8")
    html=template.replace("__VIEWER_SCRIPT__",script).replace("__MODEL_DATA__",json.dumps(payload,separators=(",",":")))
    (OUT/"viewer.html").write_text(html,encoding="utf-8")


def main():
    (OUT/"images").mkdir(parents=True,exist_ok=True)
    render(scene(160),OUT/"images"/"open.png","F1 | Open case - bed 3 raised 16 mm",(750,-640,650),(220,165,100),245)
    print("Rendered open",flush=True)
    service=[i for i in scene(covers=False) if not i.group.startswith("lid") and i.group!="wand" and not i.name.startswith("tray_")]
    render(service,OUT/"images"/"service.png","F1 | Service view - covers and beds removed",(680,-540,760),(220,138,40),205)
    print("Rendered service",flush=True)
    render(scene(lid=0,transport=True),OUT/"images"/"transport.png","F1 | Transport - no wands or powerbank; textile carry harness",(700,-650,520),(220,130,80),220)
    module=mechanism.module(0,80,False)
    render(module,OUT/"images"/"mechanism.png","F1 | Positive-return cam and adjustable guide brake",(230,-170,200),(100,140,55),120)
    # Second view is deliberately uncapped for visibility of the actual groove.
    detail=[i for i in mechanism.module(0,80,False) if not i.name.startswith(("tray_","u059_","led_"))]
    render(detail,OUT/"images"/"cam.png","F1 | Cam groove, captured roller and servo body",(360,80,140),(100,145,45),105)
    print("Rendered mechanism and transport",flush=True)
    viewer()
    print("Standalone viewer written",flush=True)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description="Render F1 CAD previews or regenerate only the offline viewer.")
    parser.add_argument("--viewer-only",action="store_true",help="Skip PNG rendering and regenerate exports/viewer.html only")
    args=parser.parse_args()
    if args.viewer_only:
        viewer()
        print("Standalone viewer written",flush=True)
    else:
        main()
