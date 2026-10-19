/** Labelled synthetic meshes exercise the actual legacy payload semantics; no CAD imports. */
export function richWorkshopModel(base) {
  const model = structuredClone(base), template = model.meshes[0];
  function block(name, group, bounds) {
    const vertices = template.vertices.map((value, index) => {
      const axis = index % 3;
      const normalized = (value - template.bounds[axis]) / (template.bounds[axis + 3] - template.bounds[axis]);
      return bounds[axis] + normalized * (bounds[axis + 3] - bounds[axis]);
    });
    return { ...structuredClone(template), id: name, name, group, bounds, vertices, info: { title: `Syntetický ${name}`, description: 'Pouze regresní fixture; nejde o díl kufru ani CAD důkaz.', details: [] } };
  }
  model.meshes.push(
    block('case_shell', 'case', [0,0,0,430,269,114.3]),
    block('lid_panel', 'lid', [0,260,114.3,430,269,220]),
    block('tray_left_1', 'moving', [30,40,20,90,90,30]),
    block('cam_1', 'moving_hw', [40,120,30,60,140,50]),
    block('pca9685', 'hardware', [180,180,20,220,210,30]),
    block('led_1', 'moving_hw', [40,40,30,80,50,35]),
    block('wand_1', 'wand', [50,50,30,55,90,100]),
    block('powerbank', 'hardware', [250,200,20,320,245,45]),
  );
  model.case = { inside_x: 430, inside_y: 269, base_height: 114.3 };
  model.hinge = [0,269,114.3];
  model.endpoints = {
    'pca9685:VCC': { component: 'pca9685', position_status: 'synthetic-design-only', physical_terminal: 'fixture-pin' },
    'tray_left_1:LED': { component: 'tray_left_1', position_status: 'synthetic-design-only', physical_terminal: 'fixture-LED' },
    'powerbank:USB': { component: 'powerbank', position_status: 'synthetic-design-only', physical_terminal: 'fixture-USB' },
    'parked:USB': { component: null, position_status: 'synthetic-design-only', physical_terminal: 'parked-fixture' },
  };
  model.routes = [
    { id: 'fixture-power', from: 'powerbank:USB', to: 'pca9685:VCC', kind: 'power', net: '5V', bundle: 'fixture-power-bundle', points: [[250,210,30],[230,210,30],[200,195,30]], transport_from: 'parked:USB', transport_points: [[240,220,20],[230,220,20],[200,195,30]] },
    { id: 'fixture-lift', from: 'pca9685:VCC', to: 'tray_left_1:LED', kind: 'signal', net: 'LED', bundle: 'fixture-tray-bundle', points: [[200,195,30],[160,150,30],[100,80,30],[50,45,30]], point_lift_weights: { '1': [0,.25,.75,1] }, moving_endpoints: [{ endpoint: 'tray_left_1:LED' }] },
    { id: 'fixture-moving', from: 'pca9685:VCC', to: 'tray_left_1:LED', kind: 'ground', net: 'GND', bundle: 'fixture-tray-bundle', points: [[200,195,30],[50,45,30]], moving_points: { '1': 'tray_left_1:LED' } },
  ];
  model.assemblyGuide.steps.push({ id: 'synthetic-install', title: 'Syntetické osazení a kabely', parts: ['pca9685','tray_left_1','cam_1','led_1'], caseParts: ['pca9685','tray_left_1','cam_1','led_1'], wires: ['fixture-power','fixture-lift','fixture-moving'], installation: { kind: 'wiring' }, instruction: 'Pouze regresní fixture. Toto není montážní postup.' });
  return model;
}
