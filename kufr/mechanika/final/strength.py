"""Conservative elastic load estimates, not qualification of a wooden case."""
from geometry import PARAMS


def calculate():
    f=PARAMS['frame']; safety=PARAMS['safety']
    force=safety['carry_design_mass_kg']*9.81*safety['shock_acceleration_g']
    reaction=force/2  # adverse tilt: only two of the four anchors carry load
    span=f['anchor_x'][1]-f['anchor_x'][0]
    end=max(f['stiffeners'][0]['upright_x'][0]-f['anchor_x'][0],
            f['anchor_x'][1]-f['stiffeners'][0]['upright_x'][1])
    b=f['stiffeners'][0]['leg']; t=f['stiffeners'][0]['thickness']
    inertia=b*t**3/12
    stress=reaction*end*(t/2)/inertia
    deflection=reaction*end**3/(3*70000*inertia)
    area=f['bond_area_each_mm2']
    # Worst useful bracket lever, bond normal stress plus bending; local edge
    # peel singularities and wood anisotropy are intentionally not certified.
    lever=f['angle_horizontal']
    modulus=f['angle_length']*f['angle_vertical']**2/6
    bond_normal=reaction/area+reaction*lever/modulus
    return {
      'design_mass_kg':safety['carry_design_mass_kg'], 'acceleration_g':safety['shock_acceleration_g'],
      'force_N':round(force,2),'adverse_two_support_reaction_N':round(reaction,2),
      'flat_end_lever_mm':round(end,2),'flat_end_inertia_mm4':round(inertia,2),
      'flat_end_stress_MPa':round(stress,2),'flat_end_deflection_mm':round(deflection,3),
      'required_minimum_aluminium_yield_MPa':150,'nominal_yield_ratio':round(150/stress,2),
      'bond_area_each_mm2':area,'average_bond_shear_MPa':round(reaction/area,4),
      'estimated_bond_normal_plus_bending_MPa':round(bond_normal,4),
      'M4_nominal_shear_MPa':round(reaction/(3.141592654*4**2/4),2),
      'bond_minimum_film_ml':round(4*area*f['bond_thickness']/1000,2),
      'proof_mass_kg':safety['carry_design_mass_kg']*safety['static_proof_factor'],
      'qualification':'NOT PHYSICALLY ACCEPTED',
      'limitations':['Simplified linear beam estimate excludes holes, torsion, local bearing and fatigue',
        'Adverse two-support case; 1.3 nominal metal margin is not an overall assembly safety factor',
        'Glue to varnish prohibited; actual bare wood cohesive strength and peel unmeasured',
        'PETG creep, rail attachment and actual mass require assembled proof test',
        'Ordinary transport only; no crash, drop or hot-vehicle qualification']}
