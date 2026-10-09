"""Validate the authored assembly sequence against the real scene and wiring."""
import json
import csv
from collections import Counter
from geometry import ROOT
from check_fit import scene
import harness


def matches(name, pattern):
    return name == pattern or name.startswith(pattern[:-1] if pattern.endswith('*') else pattern if pattern.endswith('_') else pattern+'_')


def validate():
    guide=json.loads((ROOT/'assembly-guide.json').read_text(encoding='utf-8'))
    steps=guide['steps']; ids=[]; wired=[]; installed=set(); materials=Counter()
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
        installed.update(name for name in meshes if any(matches(name,p) for p in step['parts']))
        wired.extend(step['wires'])
    assert installed==meshes, 'Final assembly omits: '+str(sorted(meshes-installed))
    expected={r['id'] for r in harness.routes()}
    assert set(wired)==expected and len(wired)==len(expected), 'Wire missing or assigned twice'
    assert len(guide['transport'])>=4 and len(guide['recommission'])>=4
    with (ROOT/'mechanicky-material.csv').open(encoding='utf-8-sig',newline='') as file:
        material_list={row['id']:float(row['mnozstvi']) for row in csv.DictReader(file)}
    # Purchased bulk stock may include spare ties or labels. Fastener counts
    # must agree exactly, so the illustrated assembly cannot omit hardware.
    exact=('M3x6','M3x8','M3CSK8','M3x10','NUT3','M4x12','NUT4',
           'GUIDE4','GUIDE_NUT','GUIDE_WASHER','FOLLOWER3','COUPLER2',
           'ROLLER','SLEEVE','SPACER','SERVO_CENTRE','BANK_WRAP','COIL_TIE')
    for name in exact:
        assert materials[name]==material_list[name], f'Assembly/BOM quantity differs: {name}'
    assert materials['TIE25']<=material_list['TIE25']
    return {'steps':len(steps),'installed_meshes':len(installed),'wires':len(wired),
            'dependencies':'PASS','final_scene_coverage':'PASS','fastener_bom_counts':'PASS'}


if __name__=='__main__':
    print(json.dumps(validate(),indent=2))
