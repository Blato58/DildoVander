"""Quasistatic sliding-cam load screen, not PETG or servo qualification."""
import math

import mechanism
from geometry import PARAMS


def contact_load(radius_mm, derivative_mm_per_rad, axial_load_N, friction):
    """Adverse lifting flank, with guide friction already in axial_load_N.

    Circumferential cam force is F*(tan(alpha)+mu)/(1-mu*tan(alpha)).
    N is the flank-normal force; the peg carries N*sqrt(1+mu**2).
    A non-positive denominator has no finite adverse-lifting solution.
    """
    if radius_mm <= 0 or axial_load_N < 0 or friction < 0:
        raise ValueError("Positive radius and nonnegative load/friction required")
    tangent = abs(derivative_mm_per_rad)/radius_mm
    angle = math.atan(tangent)
    denominator = 1-friction*tangent
    result = {"pressure_angle_degrees": math.degrees(angle),
              "lifting_denominator": denominator,
              "singularity": denominator <= 0}
    if denominator <= 0:
        return {**result, "torque_Nm": None, "normal_force_N": None,
                "peg_resultant_N": None}
    normal = axial_load_N/(math.cos(angle)-friction*math.sin(angle))
    return {**result,
            "torque_Nm": axial_load_N*radius_mm*(tangent+friction)/denominator/1000,
            "normal_force_N": normal,
            "peg_resultant_N": normal*math.sqrt(1+friction**2)}


def calculate():
    """Sample the source cam every 0.1 degree; report friction sensitivity."""
    m, wand = PARAMS["mechanism"], PARAMS["wand"]
    printed_mass_g = mechanism.tray().Volume()/1000*PARAMS["print"]["density_g_cm3"]
    moving_mass_kg = (printed_mass_g+wand["design_payload_g"]+25)/1000
    gravity = moving_mass_kg*9.81
    guide_allowance = .5*gravity
    axial_load = gravity+guide_allowance
    samples = [(step/10, *mechanism.cam_law(step/10)[:2])
               for step in range(round(m["travel_degrees"]*10)+1)]
    pressure_angle, pressure_at = max(
        (math.degrees(math.atan(abs(derivative)/radius)), angle)
        for angle, radius, derivative in samples)
    diameter = m["sliding_follower_diameter"]
    lever = m["sliding_follower_root_x"]-m["sliding_follower_tip_x"]
    area = math.pi*diameter**2/4
    selected_mu = m["sliding_friction_assumed"]
    scenarios = []
    for friction in sorted({0.0, .2, .4, selected_mu}):
        loads = [(angle, contact_load(radius, derivative, axial_load, friction))
                 for angle, radius, derivative in samples]
        singular_angles = [angle for angle, load in loads if load["singularity"]]
        finite = [(angle, load) for angle, load in loads if not load["singularity"]]
        torque_at, torque_load = max(finite, key=lambda sample: sample[1]["torque_Nm"])
        peg_at, peg_load = max(finite, key=lambda sample: sample[1]["peg_resultant_N"])
        resultant = peg_load["peg_resultant_N"]
        # Tip loading uses the entire 4.5-mm overhang even though actual groove
        # contact is closer to the root. These are nominal unnotched stresses.
        scenarios.append({"friction_coefficient": friction,
            "singularity": bool(singular_angles),
            "singularity_angles_degrees": singular_angles,
            "max_torque_Nm": None if singular_angles else torque_load["torque_Nm"],
            "max_torque_angle_degrees": torque_at,
            "minimum_test_torque_with_1_5_margin_Nm": None if singular_angles else 1.5*torque_load["torque_Nm"],
            "max_peg_resultant_N": None if singular_angles else resultant,
            "max_peg_load_angle_degrees": peg_at,
            "max_normal_force_N": None if singular_angles else peg_load["normal_force_N"],
            "nominal_root_bending_MPa": None if singular_angles else 32*resultant*lever/(math.pi*diameter**3),
            "nominal_root_max_shear_MPa": None if singular_angles else 4*resultant/(3*area)})
    return {"sample_step_degrees": .1, "sample_count": len(samples),
        "printed_tray_mass_g": printed_mass_g, "payload_design_g": wand["design_payload_g"],
        "moving_hardware_allowance_g": 25, "moving_mass_design_kg": moving_mass_kg,
        "gravity_N": gravity, "guide_friction_allowance_N": guide_allowance,
        "guide_friction_allowance_assumed_not_measured": True,
        "axial_lifting_load_N": axial_load,
        "max_pressure_angle_degrees": pressure_angle,
        "max_pressure_angle_at_degrees": pressure_at,
        "peg_diameter_mm": diameter, "conservative_root_to_tip_lever_mm": lever,
        "scenarios": scenarios,
        "selected_scenario": next(s for s in scenarios if s["friction_coefficient"] == selected_mu),
        "material_allowable_MPa": None,
        "physical_verified": False, "qualification": "NOT PHYSICALLY ACCEPTED",
        "limitations": [
            "Quasistatic adverse lifting model; no inertia, wear, stick-slip or return-flank qualification",
            "Sliding friction 0/0.2/0.4 and separate 0.5-gravity guide allowance are unmeasured assumptions",
            "Nominal root stress excludes fillets/notches, layer defects, creep, heat and cyclic fatigue",
            "No qualified anisotropic PETG allowable or servo torque curve is available",
            "The 1.5 torque margin is a prototype test target, not acceptance or a guarantee"]}
