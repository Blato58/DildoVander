"""Assumed printed-anchor load screens; physical qualification is still pending."""
import math

import chassis
from geometry import PARAMS


def inverse_tensor(tensor):
    """Invert a positive-definite 2D integral/sum r*r^T tensor."""
    xx,xy=tensor[0]; yx,yy=tensor[1]
    if not all(math.isfinite(v) for row in tensor for v in row):
        raise ValueError("Non-finite moment tensor")
    if not math.isclose(xy,yx,rel_tol=1e-12,abs_tol=1e-12):
        raise ValueError("Moment tensor must be symmetric")
    determinant=xx*yy-xy*yx
    trace=xx+yy
    if xx<=0 or yy<=0 or determinant<=1e-12*trace**2:
        raise ValueError("Singular or ill-conditioned 2D support geometry")
    return [[yy/determinant,-xy/determinant],[-xy/determinant,xx/determinant]]


def key_group_tensor(points):
    if len(points)<3 or not all(math.isfinite(v) for point in points for v in point):
        raise ValueError("Need at least three finite, non-collinear key positions")
    centre=[sum(point[axis] for point in points)/len(points) for axis in (0,1)]
    offsets=[[p[axis]-centre[axis] for axis in (0,1)] for p in points]
    xx=sum(r[0]**2 for r in offsets); yy=sum(r[1]**2 for r in offsets)
    xy=sum(r[0]*r[1] for r in offsets)
    tensor=[[xx,xy],[xy,yy]]
    inverse_tensor(tensor)
    return centre,tensor


def normal_distribution(points,centre,tensor,measure,force,first_moment):
    """Equal-stiffness key forces or linear wood traction, from equilibrium."""
    inv=inverse_tensor(tensor)
    gradient=[sum(inv[i][j]*first_moment[j] for j in (0,1)) for i in (0,1)]
    return [force/measure+sum((p[i]-centre[i])*gradient[i] for i in (0,1)) for p in points]


def normal_load_envelope(points,centre,tensor,measure,applications,force,horizontal_force,height):
    """Exact extrema over all headings AND actual vertical application points.

    Positive normal load is upward pullout. Heading h is the physical horizontal
    force direction (+X -> +Y); first moment q=P*e-H*horizontal_force*h.
    G=sum r*r^T (keys), J=integral r*r^T dA (wood). With v=tensor^-1*r,
    maximum is P/measure+P*e.v+H*horizontal_force*||v|| at h=-v/||v||.
    """
    if not math.isfinite(measure) or measure<=0 or not applications or not points:
        raise ValueError("Positive measure and nonempty evaluation/application positions required")
    if not all(math.isfinite(v) for p in list(points)+list(applications)+[centre] for v in p):
        raise ValueError("Non-finite evaluation/application positions")
    if not all(math.isfinite(v) and v>=0 for v in (force,horizontal_force,height)):
        raise ValueError("Loads and height must be finite and nonnegative")
    inv=inverse_tensor(tensor)
    samples=[]
    for index,point in enumerate(points):
        r=[point[i]-centre[i] for i in (0,1)]
        v=[sum(inv[i][j]*r[j] for j in (0,1)) for i in (0,1)]
        norm=math.hypot(*v)
        eccentric=[force*sum((p[i]-centre[i])*v[i] for i in (0,1)) for p in applications]
        extrema={}
        for label,sign,select in (("maximum",1,max),("minimum",-1,min)):
            application_index=select(range(len(applications)),key=lambda j:eccentric[j])
            heading=[-sign*component/norm for component in v] if norm else [1.0,0.0]
            application=applications[application_index]
            moment=[force*(application[i]-centre[i])-height*horizontal_force*heading[i] for i in (0,1)]
            extrema[label]={"value":force/measure+eccentric[application_index]+sign*height*horizontal_force*norm,
                "sample_index":index,"sample_xy_mm":list(point),"application_index":application_index,
                "application_xy_mm":list(application),"horizontal_force_unit_xy":heading,
                "heading_degrees":math.degrees(math.atan2(heading[1],heading[0]))%360,
                "first_moment_Nmm":moment}
        samples.append(extrema)
    return {"centroid_xy_mm":list(centre),"tensor":tensor,"samples":samples,
            "maximum":max((s["maximum"] for s in samples),key=lambda s:s["value"]),
            "minimum":min((s["minimum"] for s in samples),key=lambda s:s["value"])}


def calculate():
    safety=PARAMS["safety"]
    force=safety["carry_design_mass_kg"]*9.81*safety["shock_acceleration_g"]
    reaction=force/2
    metrics=chassis.floor_anchor_metrics()
    assumed={"petg_bearing_MPa":10.0,"petg_layer_shear_MPa":3.0,
             "epoxy_neck_tensile_MPa":3.0,"wood_bond_average_MPa":.3,
             "steel_screw_shear_MPa":80.0}
    results={}
    for name,data in metrics.items():
        module=[r for r in data["nut_roofs"] if r["kind"]=="module"]
        frame=[r for r in data["nut_roofs"] if r["kind"]=="frame"]
        # Concentrate the whole adverse support reaction at one M3 or M4.
        # Equal sharing among six modules or their 24 screws is not assumed.
        bearing=reaction/min(r["bearing_area_mm2"] for r in module)
        m4_bearing=reaction/min(r["bearing_area_mm2"] for r in frame)
        # Entry tunnel removes half the hex pocket support perimeter. Nominal
        # punching shear cannot certify local bending and notch stresses.
        shear_area=min(r["pocket_perimeter_mm"]*.5*r["roof_mm"] for r in module)
        roof_shear=reaction/shear_area
        m4_shear_area=min(r["pocket_perimeter_mm"]*.5*r["roof_mm"] for r in frame)
        count=data["key_count"]
        neck_area=count*math.pi*(PARAMS["frame"]["floor_anchors"]["key_neck_diameter"]/2)**2
        key_bearing=reaction/data["key_head_retaining_area_mm2"]
        lever=PARAMS["case"]["safe_zone"][2]
        keys=chassis.floor_anchor_keys(name)
        points=[(key["x"],key["y"]) for key in keys]
        centre,tensor=key_group_tensor(points)
        nuts=chassis.anchor_nuts(name)
        applications=[(nut["x"],nut["y"]) for nut in nuts]
        key_envelope=normal_load_envelope(points,centre,tensor,count,applications,reaction,reaction,lever)
        key_envelope["tensor_units"]="mm2; sum r*r^T for equal-stiffness keys"
        key_envelope["value_units"]="N"
        key_peak=key_envelope["maximum"]["value"]
        controlling_key=key_envelope["maximum"]
        controlling_key["application_nut"]=nuts[controlling_key["application_index"]]
        forces=normal_distribution(points,centre,tensor,count,reaction,controlling_key["first_moment_Nmm"])
        key_envelope["controlling_heading_key_forces_N"]=forces
        key_envelope["equilibrium_force_residual_N"]=sum(forces)-reaction
        key_envelope["equilibrium_first_moment_residual_Nmm"]=[
            sum(f*(p[i]-centre[i]) for f,p in zip(forces,points))-controlling_key["first_moment_Nmm"][i]
            for i in (0,1)]
        neck_each=neck_area/count
        shoulder_each=data["key_head_retaining_area_mm2"]/count
        # Nominal outer-diameter punching screen only. The printed retention
        # face now tapers 45 degrees; this cylinder does not qualify radial
        # splitting, cone bending, creep or adhesion. Prototype pull testing
        # remains required even when the nominal screen passes.
        key_lip_mm=PARAMS["frame"]["floor_anchors"]["key_head_bottom"]-PARAMS["frame"]["floor_anchors"]["z"]
        key_lip_shear_area=math.pi*PARAMS["frame"]["floor_anchors"]["key_head_diameter"]*key_lip_mm
        wood_shear=reaction/data["wood_bond_area_mm2"]
        wood_envelope=normal_load_envelope(data["wood_bond_boundary_vertices_xy_mm"],
            data["wood_bond_centroid_xy_mm"],data["wood_bond_area_second_moment_tensor_mm4"],
            data["wood_bond_area_mm2"],applications,reaction,reaction,lever)
        wood_peel_screen=wood_envelope["maximum"]["value"]
        wood_envelope["tensor_units"]="mm4; integral r*r^T dA"
        wood_envelope["value_units"]="MPa"
        wood_envelope["maximum"]["application_nut"]=nuts[wood_envelope["maximum"]["application_index"]]
        results[name]={**data,
            "M3_single_nut_roof_bearing_MPa":bearing,
            "M4_single_nut_roof_bearing_MPa":m4_bearing,
            "M3_effective_roof_shear_area_mm2":shear_area,
            "M3_nominal_roof_pullout_shear_MPa":roof_shear,
            "M4_effective_roof_shear_area_mm2":m4_shear_area,
            "M4_nominal_roof_pullout_shear_MPa":reaction/m4_shear_area,
            "M3_sideways_roof_hole_bearing_MPa":reaction/(3*3),
            "nominal_roof_row_net_section_shear_MPa":reaction/min(data["nut_row_roof_net_sections_mm2"].values()),
            "key_shoulder_bearing_MPa":key_bearing,
            "epoxy_key_neck_tensile_MPa":reaction/neck_area,
            "sideways_170mm_moment_peak_key_force_N":key_peak,
            "key_force_all_heading_envelope":key_envelope,
            "wood_stress_all_heading_envelope":wood_envelope,
            "sideways_peak_key_shoulder_bearing_MPa":key_peak/shoulder_each,
            "sideways_peak_epoxy_neck_tensile_MPa":key_peak/neck_each,
            "key_bottom_lip_depth_mm":key_lip_mm,
            "sideways_peak_key_lip_punching_shear_MPa":key_peak/key_lip_shear_area,
            "wood_average_shear_or_normal_MPa":wood_shear,
            "wood_normal_plus_170mm_moment_MPa":wood_peel_screen,
            "assumed_M3_bearing_ratio":assumed["petg_bearing_MPa"]/bearing,
            "assumed_M4_bearing_ratio":assumed["petg_bearing_MPa"]/m4_bearing,
            "assumed_roof_shear_ratio":assumed["petg_layer_shear_MPa"]/roof_shear,
            "assumed_M4_roof_shear_ratio":assumed["petg_layer_shear_MPa"]/(reaction/m4_shear_area),
            "assumed_roof_row_net_section_ratio":assumed["petg_layer_shear_MPa"]/(reaction/min(data["nut_row_roof_net_sections_mm2"].values())),
            "assumed_M3_sideways_hole_bearing_ratio":assumed["petg_bearing_MPa"]/(reaction/(3*3)),
            "assumed_key_neck_ratio":assumed["epoxy_neck_tensile_MPa"]/(reaction/neck_area),
            "assumed_sideways_key_neck_ratio":assumed["epoxy_neck_tensile_MPa"]/(key_peak/neck_each),
            "assumed_sideways_key_shoulder_ratio":assumed["petg_bearing_MPa"]/(key_peak/shoulder_each),
            "assumed_sideways_key_lip_shear_ratio":assumed["petg_layer_shear_MPa"]/(key_peak/key_lip_shear_area),
            "assumed_wood_linear_ratio":assumed["wood_bond_average_MPa"]/wood_peel_screen}
        results[name]["normal_load_assumption_screen_passed"]=all(
            value>=1 for key,value in results[name].items() if key.startswith("assumed_"))
    return {
        "design_mass_kg":safety["carry_design_mass_kg"],
        "acceleration_g":safety["shock_acceleration_g"],"force_N":force,
        "adverse_two_support_reaction_N":reaction,
        "load_path":"modules -> 24 M3 bolts/captive nuts -> floor cradles -> cast epoxy keys/film -> bare wooden floor; frame/electronics -> four M4 bolts -> same cradles",
        "directions_screened":["upright compression","inverted uplift","sideways shear and 170 mm overturning moment"],
        "assumed_allowables_not_measured":assumed,"anchors":results,
        "normal_load_assumption_screen_passed":all(d["normal_load_assumption_screen_passed"] for d in results.values()),
        "moment_convention":"Positive normal=P uplift; physical horizontal heading h; q=P*(nut-centroid)-P*170mm*h. G=sum r*r^T, J=integral r*r^T dA",
        "load_combination":"Conservative simultaneous normal P and horizontal P, P=73.575N; full normal reaction applied at each actual nut in turn, not at the key/wood centroid",
        "M3_nominal_screw_shear_MPa":reaction/(math.pi*3**2/4),
        "M4_nominal_screw_shear_MPa":reaction/(math.pi*4**2/4),
        "bond_minimum_film_and_keys_ml":sum(v["epoxy_volume_mm3"] for v in metrics.values())/1000,
        "proof_mass_kg":safety["carry_design_mass_kg"]*safety["static_proof_factor"],
        "qualification":"NOT PHYSICALLY ACCEPTED",
        "limitations":[
            "Printed/epoxy/wood allowables are explicit design assumptions, not material certification",
            "Nominal roof shear excludes local bending, tunnel notches, print defects and preload",
            "The 45-degree epoxy-key retention cone uses a nominal cylindrical punching screen; radial splitting and cone flexure are unqualified",
            "PETG creep, layer orientation, hot-car exposure and cyclic load remain unmeasured",
            "Key filling, voids, bare wood cohesion, edge peel and adhesion require physical proof",
            "Discrete key moment model assumes elastic sharing; lip bending and redistribution after gaps/creep are unverified",
            "Signed elastic key forces include compression; actual cradle-floor contact has different stiffness and needs validation",
            "Horizontal torsion/shear sharing is not qualified by the normal-traction envelope",
            "Keys avoid relying on PETG chemical adhesion but do not establish wood adhesion",
            "5 kg at 3g with two active supports is an estimate; no crash/drop qualification"]}
