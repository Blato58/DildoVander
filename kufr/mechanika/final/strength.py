"""Assumed printed-anchor load screens; physical qualification is still pending."""
import math

import chassis
from geometry import PARAMS


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
        count=data["key_count"]
        neck_area=count*math.pi*(PARAMS["frame"]["floor_anchors"]["key_neck_diameter"]/2)**2
        key_bearing=reaction/data["key_head_retaining_area_mm2"]
        # Sideways gravity may act anywhere in the 170 mm safe envelope.
        # Actual L-shaped interface section modulus screens eccentric moment;
        # linear stress cannot resolve a wood/epoxy edge peel singularity.
        lever=PARAMS["case"]["safe_zone"][2]
        keys=chassis.floor_anchor_keys(name)
        # Discrete-group elastic screen: a sideways moment creates uneven key
        # tension. It is unsafe to use reaction / key_count for this direction.
        key_peak=reaction/count
        for axis in ("x","y"):
            centre=sum(k[axis] for k in keys)/count
            offsets=[k[axis]-centre for k in keys]
            group_inertia=sum(offset**2 for offset in offsets)
            key_peak=max(key_peak,reaction/count+reaction*lever*max(abs(v) for v in offsets)/group_inertia)
        neck_each=neck_area/count
        shoulder_each=data["key_head_retaining_area_mm2"]/count
        # Upward cradle load bears on the bottom lip below the epoxy head.
        # Its 2 mm depth is derived from the actual neck/chamber transition.
        key_lip_mm=PARAMS["frame"]["floor_anchors"]["key_head_bottom"]-PARAMS["frame"]["floor_anchors"]["z"]
        key_lip_shear_area=math.pi*PARAMS["frame"]["floor_anchors"]["key_head_diameter"]*key_lip_mm
        wood_shear=reaction/data["wood_bond_area_mm2"]
        wood_peel_screen=wood_shear+reaction*lever/data["bond_section_modulus_min_mm3"]
        results[name]={**data,
            "M3_single_nut_roof_bearing_MPa":bearing,
            "M4_single_nut_roof_bearing_MPa":m4_bearing,
            "M3_effective_roof_shear_area_mm2":shear_area,
            "M3_nominal_roof_pullout_shear_MPa":roof_shear,
            "M3_sideways_roof_hole_bearing_MPa":reaction/(3*3),
            "nominal_roof_row_net_section_shear_MPa":reaction/min(data["nut_row_roof_net_sections_mm2"].values()),
            "key_shoulder_bearing_MPa":key_bearing,
            "epoxy_key_neck_tensile_MPa":reaction/neck_area,
            "sideways_170mm_moment_peak_key_force_N":key_peak,
            "sideways_peak_key_shoulder_bearing_MPa":key_peak/shoulder_each,
            "sideways_peak_epoxy_neck_tensile_MPa":key_peak/neck_each,
            "key_bottom_lip_depth_mm":key_lip_mm,
            "sideways_peak_key_lip_punching_shear_MPa":key_peak/key_lip_shear_area,
            "wood_average_shear_or_normal_MPa":wood_shear,
            "wood_normal_plus_170mm_moment_MPa":wood_peel_screen,
            "assumed_M3_bearing_ratio":assumed["petg_bearing_MPa"]/bearing,
            "assumed_roof_shear_ratio":assumed["petg_layer_shear_MPa"]/roof_shear,
            "assumed_key_neck_ratio":assumed["epoxy_neck_tensile_MPa"]/(reaction/neck_area),
            "assumed_sideways_key_neck_ratio":assumed["epoxy_neck_tensile_MPa"]/(key_peak/neck_each),
            "assumed_wood_linear_ratio":assumed["wood_bond_average_MPa"]/wood_peel_screen}
    return {
        "design_mass_kg":safety["carry_design_mass_kg"],
        "acceleration_g":safety["shock_acceleration_g"],"force_N":force,
        "adverse_two_support_reaction_N":reaction,
        "load_path":"modules -> 24 M3 bolts/captive nuts -> floor cradles -> cast epoxy keys/film -> bare wooden floor; frame/electronics -> four M4 bolts -> same cradles",
        "directions_screened":["upright compression","inverted uplift","sideways shear and 170 mm overturning moment"],
        "assumed_allowables_not_measured":assumed,"anchors":results,
        "M3_nominal_screw_shear_MPa":reaction/(math.pi*3**2/4),
        "M4_nominal_screw_shear_MPa":reaction/(math.pi*4**2/4),
        "bond_minimum_film_and_keys_ml":sum(v["epoxy_volume_mm3"] for v in metrics.values())/1000,
        "proof_mass_kg":safety["carry_design_mass_kg"]*safety["static_proof_factor"],
        "qualification":"NOT PHYSICALLY ACCEPTED",
        "limitations":[
            "Printed/epoxy/wood allowables are explicit design assumptions, not material certification",
            "Nominal roof shear excludes local bending, tunnel notches, print defects and preload",
            "PETG creep, layer orientation, hot-car exposure and cyclic load remain unmeasured",
            "Key filling, voids, bare wood cohesion, edge peel and adhesion require physical proof",
            "Discrete key moment model assumes elastic sharing; lip bending and redistribution after gaps/creep are unverified",
            "Keys avoid relying on PETG chemical adhesion but do not establish wood adhesion",
            "5 kg at 3g with two active supports is an estimate; no crash/drop qualification"]}
