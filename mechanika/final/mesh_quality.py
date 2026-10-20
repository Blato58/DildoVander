"""Triangle crossing screen using the VTK runtime already bundled with CadQuery."""
import numpy as np
from vtkmodules.util.numpy_support import numpy_to_vtk, numpy_to_vtkIdTypeArray
from vtkmodules.vtkCommonCore import vtkPoints, vtkIdList
from vtkmodules.vtkCommonDataModel import vtkCellArray, vtkPolyData, vtkStaticCellLocator, vtkTriangle


def self_intersections(triangles, tolerance=.0003):
    """Check crossings beyond normal shared vertices/edges, at stated tolerance.

    Shared boundary points move inward by tolerance before intersection tests;
    their legitimate contact is omitted while folded/overlapping neighbours
    still intersect. BREP validity and closed/wound topology are separate gates.
    """
    triangles=np.ascontiguousarray(triangles,dtype=np.float64)
    if triangles.ndim!=3 or triangles.shape[1:]!=(3,3) or not np.isfinite(triangles).all():
        raise ValueError('Finite Nx3x3 mesh required')
    if len(triangles)==0 or not 0<tolerance<=.001:
        raise ValueError('Nonempty mesh and positive sub-micron tolerance required')
    points=vtkPoints();points.SetData(numpy_to_vtk(triangles.reshape(-1,3),deep=True))
    cells=vtkCellArray()
    cells.SetData(numpy_to_vtkIdTypeArray(np.arange(0,3*len(triangles)+1,3,dtype=np.int64),deep=True),
                  numpy_to_vtkIdTypeArray(np.arange(3*len(triangles),dtype=np.int64),deep=True))
    poly=vtkPolyData();poly.SetPoints(points);poly.SetPolys(cells)
    locator=vtkStaticCellLocator();locator.SetDataSet(poly);locator.BuildLocator()
    low,high=triangles.min(axis=1),triangles.max(axis=1)
    keys=[{tuple(v) for v in np.round(triangle,5)} for triangle in triangles]
    ids=vtkIdList();tested=0;hits=[]
    for index,triangle in enumerate(triangles):
        bbox=[value for axis in range(3) for value in (low[index,axis]-1e-8,high[index,axis]+1e-8)]
        locator.FindCellsWithinBounds(bbox,ids)
        candidates=np.asarray([ids.GetId(j) for j in range(ids.GetNumberOfIds())],dtype=np.int64)
        candidates=candidates[candidates>index]
        candidates=candidates[np.all(np.minimum(high[index],high[candidates])-np.maximum(low[index],low[candidates])>=-1e-8,axis=1)]
        for other in candidates:
            a,b=triangle,triangles[other]
            shared=keys[index]&keys[other]
            tested+=1
            if len(shared)==3:
                hits.append([index,int(other)])
            else:
                if shared:
                    a,b=a.copy(),b.copy()
                    for part in (a,b):
                        centre=part.mean(axis=0)
                        for vertex in part:
                            if tuple(np.round(vertex,5)) in shared:
                                delta=centre-vertex
                                length=np.linalg.norm(delta)
                                if length: vertex+=delta*min(tolerance/length,.01)
                if vtkTriangle.TrianglesIntersect(*a,*b):
                    hits.append([index,int(other)])
            if len(hits)>=10:
                return {'status':'FAIL_SELF_INTERSECTION','pairs':hits,'truncated':True,
                        'tested_candidate_pairs':tested,'tolerance_mm':tolerance}
    return {'status':'PASS_SELF_INTERSECTION' if not hits else 'FAIL_SELF_INTERSECTION',
            'pairs':hits,'truncated':False,'tested_candidate_pairs':tested,'tolerance_mm':tolerance,
            'method':'VTK static-cell broad phase and triangle intersection; shared topology contact excluded at stated tolerance'}
