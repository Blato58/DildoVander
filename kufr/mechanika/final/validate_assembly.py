"""Validate the authored assembly sequence against the real scene and wiring."""
import json
import csv
import re
from collections import Counter
from geometry import ROOT
from check_fit import scene
import harness


def matches(name, pattern):
    return name == pattern or name.startswith(pattern[:-1] if pattern.endswith('*') else pattern if pattern.endswith('_') else pattern+'_')


def validate_tool(tool, wood_bonding=False):
    assert not re.search(r'pil(?:ník|ka|a)|vrt|vrta|saw|drill|file', tool, re.I), 'Forbidden fitting/cutting tool: '+tool
    if 'brusný papír' in tool.lower():
        assert wood_bonding and 'dřev' in tool.lower(), 'Sandpaper is only for wood bonding footprints'


def validate_contract(guide, material_list):
    """Keep purchasing and first-install order independent of scene coverage."""
    forbidden={'FOLLOWER3','ROLLER','SLEEVE','SPACER','ANGLE40','RAIL20','M4x12'}
    assert not forbidden.intersection(guide['materialCatalog']), 'Obsolete material catalog'
    assert not forbidden.intersection(material_list), 'Obsolete purchased material'
    steps={step['id']:step for step in guide['steps']}
    order=[step['id'] for step in guide['steps']]
    assert len(order)==15, 'Assembly must retain fifteen steps'
    required_order=('fit','frame','carriers','anchors','install','mechanisms')
    assert [order.index(name) for name in required_order]==sorted(order.index(name) for name in required_order), 'Frame must precede module installation'
    for before,after in zip(required_order,required_order[1:]):
        assert before in steps[after]['depends'], f'Missing assembly dependency: {before} -> {after}'
    for step in guide['steps']:
        assert not forbidden.intersection(m['id'] for m in step['materials'])
        for tool in step['tools']:
            validate_tool(tool,wood_bonding=step['id']=='anchors')
    quantities=lambda name: Counter({m['id']:m['quantity'] for m in steps[name]['materials']})
    assert quantities('anchors')['NUT3']==24 and quantities('anchors')['NUT4']==4, 'Load captive nuts before frame'
    assert quantities('install')['M4x10']==4 and quantities('install')['NUT4']==0
    assert quantities('mechanisms')['M3x14']==24 and quantities('mechanisms')['NUT3']==0
    assert quantities('covers')['M3x10']==8 and material_list['M3x10']==8
    assert material_list['NUT3']==89 and material_list['NUT4']==4
    return {'no_fitting_tools':'PASS','printed_floor_anchor_order':'PASS'}


def validate():
    guide=json.loads((ROOT/'assembly-guide.json').read_text(encoding='utf-8'))
    steps=guide['steps']; ids=[]; wired=[]; installed=set(); materials=Counter(); first_install={}
    meshes={i.name for i in scene() if i.group not in ('case','lid','lid_hardware')}
    for step in steps:
        assert step['id'] not in ids, 'Duplicate step'
        assert all(x in ids for x in step['depends']), 'Forward dependency'
        ids.append(step['id'])
        assert all(step.get(key) for key in ('title','instruction','check','camera','partSummary'))
        assert step['camera'] in ('top','front','side','overview')
        for material in step['materials']:
            assert material['id'] in guide['materialCatalog'] and material['quantity']>0
            materials[material['id']]+=material['quantity']
        step_meshes={name for name in meshes if any(matches(name,p) for p in step['parts'])}
        for name in step_meshes:
            first_install.setdefault(name,step['id'])
        installed.update(step_meshes)
        wired.extend(step['wires'])
    assert installed==meshes, 'Final assembly omits: '+str(sorted(meshes-installed))
    expected={r['id'] for r in harness.routes()}
    assert set(wired)==expected and len(wired)==len(expected), 'Wire missing or assigned twice'
    assert len(guide['transport'])>=4 and len(guide['recommission'])>=4
    with (ROOT/'mechanicky-material.csv').open(encoding='utf-8-sig',newline='') as file:
        material_rows=list(csv.DictReader(file))
    material_list={row['id']:float(row['mnozstvi']) for row in material_rows}
    for row in material_rows:
        assert None not in row and all(value is not None for value in row.values()), 'Malformed material CSV row: '+row['id']
        validate_tool(row['nastroj'],wood_bonding=row['id']=='EPOXY')
    contract=validate_contract(guide,material_list)
    expected_anchors={f'floor_anchor_{end}_{side}' for end in ('front','rear') for side in ('left','right')}
    assert expected_anchors.issubset(meshes), 'Missing printed floor cradles'
    scene_counts={'floor_anchor*':4,'wood_bond*':4,'module_nut*':24,
                  'module_m3*':24,'module_head*':24,'anchor_nut*':4,
                  'anchor_m4*':4,'anchor_head*':4}
    for pattern,count in scene_counts.items():
        assert sum(matches(name,pattern) for name in meshes)==count, 'Scene hardware count differs: '+pattern
    first_step_patterns={'anchors':('floor_anchor*','wood_bond*','module_nut*','anchor_nut*'),
                         'install':('anchor_m4*','anchor_head*'),
                         'mechanisms':('base_*','tray_*','cam_*','module_m3*','module_head*')}
    for step_id,patterns in first_step_patterns.items():
        for pattern in patterns:
            names={name for name in meshes if matches(name,pattern)}
            assert names, 'Assembly pattern matches no scene parts: '+pattern
            assert all(first_install[name]==step_id for name in names), 'Part installed out of order: '+pattern
    # Purchased bulk stock may include spare ties or labels. Fastener counts
    # must agree exactly, so the illustrated assembly cannot omit hardware.
    exact=('M3x6','M3x8','M3CSK8','M3x10','M3x14','NUT3','M4x10','NUT4',
           'GUIDE4','GUIDE_NUT','GUIDE_WASHER','COUPLER2',
           'SERVO_CENTRE','BANK_WRAP','COIL_TIE')
    for name in exact:
        assert materials[name]==material_list[name], f'Assembly/BOM quantity differs: {name}'
    assert materials['TIE25']<=material_list['TIE25']
    return {'steps':len(steps),'installed_meshes':len(installed),'wires':len(wired),
            'dependencies':'PASS','final_scene_coverage':'PASS','fastener_bom_counts':'PASS',
            'first_hardware_install':'PASS',**contract}


if __name__=='__main__':
    print(json.dumps(validate(),indent=2))
