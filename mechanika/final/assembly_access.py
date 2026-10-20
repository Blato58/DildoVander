"""BREP installation/service checks for the authored manufacturing sequence.

Tool shafts are continuous solids; complex material translation is sampled.
Connector reservations are finite design allowances, not measured plugs.
"""
import json
import math
from geometry import ROOT, PARAMS, Item, bounds, box, cylinder, curve_tube
from check_fit import overlaps, scene
from service_geometry import SERVICE_MOUNTS, SHAFT_RADIUS, HEAD_RADIUS, SEAT_Z, SERVICE_LIFT
import layout
import flex_routing

CLAMP_ACCESS_DEPENDENCIES={
    'left_service_carrier_tier_2_left':('left_service_carrier_tier_1',),
}

CONTROL_SERVICE_STAGES=(
    ('red_control',('red_main',),45),
    ('green_control',('green_drives',),45),
    ('ptt_control',('white_ptt',),45),
    ('plate',('switch_shelf','cover_nut_control_cover_1','cover_nut_control_cover_2'),120),
    ('right_ladder',('switch_frame_right','switch_joint_nut_2','switch_joint_nut_4'),120),
)
CONTROL_JOINT_SCREWS=tuple(f'switch_joint_screw_{number}' for number in range(1,5))
CONTROL_SERVICE_REMOVED={name for _,names,_ in CONTROL_SERVICE_STAGES for name in names}|set(CONTROL_JOINT_SCREWS)


def service_fixture():
    """Open-case service state; the two plate nuts stay when its cover leaves."""
    items=scene(covers=False,transport=True)
    retained={f'cover_nut_control_cover_{number}' for number in (1,2)}
    items += [item for item in layout.items(True,False) if item.name in retained]
    assert len({item.name for item in items})==len(items)
    return items


def cassette_clamp_tool_paths():
    """Bench tightening order, using a continuous 25 mm straight tool shaft."""
    parts,joints=layout.service_components()
    for joint in joints:
        axis=joint['axis']
        start=tuple(joint['head_face'][k]-axis[k]*1.65 for k in range(3))
        tool=cylinder(1.7,25,start,tuple(-value for value in axis))
        removed=CLAMP_ACCESS_DEPENDENCIES.get(joint['id'],())
        for name,part in parts.items():
            if name not in removed:
                assert tool.intersect(part.shape).Volume()<1e-7,('Blocked bench clamp',joint['id'],name)
    return {'continuous_tool_paths':len(joints),'shaft_diameter_mm':3.4,'shaft_length_mm':25,
            'remove_before_access':CLAMP_ACCESS_DEPENDENCIES,
            'sequence':'Tighten left tier 2 before closing tier 1; remove tier 1 before middle-clamp service',
            'physical_tool_handle_clearance_verified':False}


def usb_tail_installation_paths(obstacles=None):
    """Dress two local R20 crossings after bench tightening, before coiling.

    Both plugs remain disconnected. This is sampled placement of an uncoiled
    flexible cable through open edges, not rigid translation of its entire U.
    """
    if obstacles is None:
        children,_=layout.service_components()
        obstacles=[Item(name,part.shape,'fixed','white') for name,part in children.items()
                   if name.startswith('left_service_carrier_')]
        obstacles += [item for item in layout.service_joint_hardware()
                      if item.name.startswith('left_service_carrier_')]
    route=flex_routing.usb_bank_tail(
        PARAMS['connector_reservations']['bank_usb']['endpoint'],
        PARAMS['mechanism']['origin_x']-2.4,PARAMS['usb_coil']['origin'][1])
    turn=route['primitives'][3]
    assert turn['kind']=='arc' and turn['radius_mm']==20
    first=2*math.pi-math.acos((67.75-turn['centre'][0])/20)
    def point(angle):
        return [turn['centre'][i]+20*(turn['u'][i]*math.cos(angle)+turn['v'][i]*math.sin(angle))
                for i in range(3)]
    fore=curve_tube([{'kind':'arc','start':point(first),
        'mid':point((first+2*math.pi)/2),'end':point(2*math.pi)}],2.55)
    rear=curve_tube([route['primitives'][5]],2.55)
    fixed=[(item,bounds(item.shape)) for item in obstacles]
    comparisons=0
    brep_pairs=0
    def check(shape,stage,offset):
        nonlocal comparisons,brep_pairs
        extent=bounds(shape)
        for item,other in fixed:
            comparisons+=1
            if overlaps(extent,other):
                brep_pairs+=1
                assert shape.intersect(item.shape).Volume()<1e-7,('Blocked USB edge insertion',stage,offset,item.name)
    for stage,local,direction in (('fore',fore,-1),('rear',rear,1)):
        for step in range(41):
            check(local.translate((0,direction*step/2,0)),stage,step/2)
    for name in ('bank_usb','pd_usb'):
        reservation=PARAMS['connector_reservations'][name]
        assert sorted(reservation['size'])==[12,12,20]
        check(box(*reservation['size'],reservation['origin']),name,0)
    return {'status':'PASS','local_R20_sections':2,'samples_per_section':41,
            'sample_spacing_mm':.5,'approach_distance_mm':20,'finite_radius_mm':2.55,
            'closed_cassette_obstacles':len(obstacles),'comparisons':comparisons,
            'brep_pairs_checked':brep_pairs,'plug_reservations':['bank_usb','pd_usb'],
            'sequence':'Tighten bench clamps first; dress both unplugged cable ends through open edges; form coil last',
            'physical_assembly_performed':False,
            'limitations':['Sampled local crossings do not prove simultaneous rigid placement of the whole preformed U',
                           'Actual flexible cable dressing, plug dimensions and supplier bend limits require dry fit']}


def intersection_errors(moving, fixed):
    errors=[]
    cached=[(item,bounds(item.shape)) for item in fixed]
    for item in moving:
        extent=bounds(item.shape)
        for other,other_extent in cached:
            if overlaps(extent,other_extent):
                volume=item.shape.intersect(other.shape).Volume()
                if volume>.03:
                    errors.append([item.name,other.name,round(volume,4)])
    return errors


def cassette_items(name, items):
    members=layout.LEFT_STACK if name.startswith('left_') else layout.RIGHT_STACK
    result=[item for item in items if item.name==name or item.name.startswith(name+'_') or item.name in members
            or any(item.name.startswith('component_tie_'+member+'_') for member in members)]
    if name.startswith('left_'):
        result += [item for item in items if item.name in ('capacitor_1','capacitor_2')
                   or item.name=='usb_park_clip'
                   or item.name.startswith(('capacitor_tie_1','capacitor_tie_2','usb_clip_'))]
    return result


def control_panel_service_paths(items=None):
    """Release the top plate and free right ladder; the left deck joint stays.

    Disconnect leads and remove the cover, bank and left speaker first. Neither
    original underside-headed deck screw needs access during cassette service.
    """
    if items is None:
        items=service_fixture()
    removed={'powerbank','speaker_left'}
    current=[item for item in items if item.name not in removed
             and not item.name.startswith(('component_tie_powerbank_','component_tie_speaker_left_'))
             and not item.group.startswith('lid') and item.group!='cover']
    assert any(item.group=='case' for item in current),'Service proof requires the real wooden case'
    errors={}
    counts={}
    def check_stage(stage,names,distance):
        nonlocal current
        moving=[item for item in current if item.name in names]
        assert {item.name for item in moving}==set(names),('Incomplete controls service stage',stage)
        fixed=[item for item in current if item.name not in names]
        samples=range(0,distance+1,5)
        for offset in samples:
            shifted=[Item(item.name,item.shape.translate((0,0,offset)),item.group,item.color) for item in moving]
            hits=intersection_errors(shifted,fixed)
            if hits: errors.setdefault(stage,[]).extend([[offset,*hit] for hit in hits])
        counts[stage]=len(samples)
        current=fixed
    for stage,names,distance in CONTROL_SERVICE_STAGES[:3]:
        check_stage(stage,names,distance)
    for number,(x,y,_) in enumerate(layout.SWITCH_JOINTS,1):
        owner=f'switch_joint_screw_{number}'
        fixed=[item for item in current if item.name!=owner]
        driver=Item('control_hex_driver',cylinder(1.7,25,(x,y,105)),'access','white')
        hits=intersection_errors([driver],fixed)
        if hits: errors.setdefault('control_driver',[]).extend(hits)
        screw=next(item for item in current if item.name==owner)
        for offset in range(13):
            shifted=Item(owner,screw.shape.translate((0,0,offset)),screw.group,screw.color)
            hits=intersection_errors([shifted],fixed)
            if hits: errors.setdefault('control_screw_withdrawal',[]).extend([[offset,*hit] for hit in hits])
        current=fixed
    for stage,names,distance in CONTROL_SERVICE_STAGES[3:]:
        check_stage(stage,names,distance)
    retained={'switch_frame_left','switch_joint_nut_1','switch_joint_nut_3'}|{
        f'carrier_{kind}_switch_shelf_{number}' for kind in ('shaft','head','nut') for number in (1,2)}
    assert retained<={item.name for item in current},'The bolted left ladder must remain installed'
    assert not errors,'Controls service intersections: '+json.dumps(errors)[:5000]
    return {'status':'PASS','continuous_driver_paths':4,'driver_diameter_mm':3.4,'driver_length_mm':25,
            'screw_withdrawal_samples_per_joint':13,'screw_withdrawal_distance_mm':12,
            'translation_samples':counts,'removed_before_left_cassette':sorted(CONTROL_SERVICE_REMOVED),
            'retained_original_deck_fixture':sorted(retained),'physical_assembly_performed':False,
            'sequence':'Disconnect leads; remove cover/bank/left speaker; extract three controls; undo four top joints; lift plate then free right ladder; release cassette',
            'limitations':['Purchased-control latch release, actual tool handles and flexible leads need physical qualification',
                           'Translation is sampled every 5 mm; screw withdrawal every 1 mm']}


def service_obstacles(name, items):
    moving={item.name for item in cassette_items(name,items)}
    # The clip is screwed to this cassette, so travels with it. Remove the
    # speaker and unpark the disconnected USB plug before lifting the cassette.
    removed={'powerbank','speaker_left'} if name.startswith('left_') else set()
    if name.startswith('left_'):
        removed |= CONTROL_SERVICE_REMOVED
    return [item for item in items if item.name not in moving|removed
            and not (name.startswith('left_') and item.name.startswith('component_tie_speaker_left_'))
            and not item.name.startswith(('carrier_shaft_left_service_', 'carrier_head_left_service_',
                                          'carrier_shaft_right_service_', 'carrier_head_right_service_'))
            and item.group!='cover' and not item.group.startswith('lid')]


def validate():
    clamp_access=cassette_clamp_tool_paths()
    usb_access=usb_tail_installation_paths()
    items=service_fixture()
    controls_access=control_panel_service_paths(items)
    errors={}
    tool_count=0
    for name,points in SERVICE_MOUNTS.items():
        fixed=service_obstacles(name,items)
        for x,y in points:
            tool=Item('service_hex_shaft',cylinder(SHAFT_RADIUS,115-SEAT_Z,
                      (x,y,SEAT_Z+1.65)),'access','white')
            hits=intersection_errors([tool],fixed+cassette_items(name,items))
            if hits: errors.setdefault('cassette_tool_access',[]).extend(hits)
            head=Item('service_screw_head_sweep',cylinder(HEAD_RADIUS,SERVICE_LIFT,
                      (x,y,SEAT_Z)),'access','white')
            hits=intersection_errors([head],fixed+cassette_items(name,items))
            if hits: errors.setdefault('cassette_screw_withdrawal',[]).extend(hits)
            tool_count+=1
        moving=cassette_items(name,items)
        for offset in range(0,int(SERVICE_LIFT)+1,5):
            shifted=[Item(item.name,item.shape.translate((0,0,offset)),item.group,item.color) for item in moving]
            hits=intersection_errors(shifted,fixed)
            if hits: errors.setdefault(name+'_removal',[]).extend([[offset,*hit] for hit in hits])
        print('Assembly access: '+name+' sampled',flush=True)
    # A populated deck goes in vertically before the tall moving upper groups.
    # Permanent anchors, their captive nuts and the reference case are the only
    # installed obstacles. No electronics need to be threaded through a frame.
    fixed=[item for item in items if item.name.startswith(('floor_anchor_','wood_bond_',
           'anchor_nut_','module_nut_')) or item.group=='case']
    from validate_assembly import matches
    guide=json.loads((ROOT/'assembly-guide.json').read_text(encoding='utf-8'))
    install=next(step for step in guide['steps'] if step['id']=='install')
    patterns=install['caseParts']
    deck=[item for item in items if any(matches(item.name,pattern) for pattern in patterns)]
    right_members={item.name for item in cassette_items('right_service_carrier',items)}
    deck=[item for item in deck if item.name not in {'speaker_left','speaker_right','powerbank'}|right_members
          and not item.name.startswith(('component_tie_speaker_', 'carrier_head_right_service_',
                                        'carrier_shaft_right_service_'))
          and not any(matches(item.name,pattern) for pattern in install['parts'])]
    for offset in range(0,121,5):
        shifted=[Item(item.name,item.shape.translate((0,0,offset)),item.group,item.color) for item in deck]
        hits=intersection_errors(shifted,fixed)
        if hits: errors.setdefault('deck_insertion',[]).extend([[offset,*hit] for hit in hits])
    import chassis
    anchor_access=[item for item in deck if item.name not in right_members
                   and not item.name.startswith(('component_tie_speaker_','carrier_head_right_service_',
                                                 'carrier_shaft_right_service_'))]
    for x,y in chassis.rack_holes():
        shaft=Item('anchor_hex_shaft',cylinder(2,100,(x,y,15.5)),'access','white')
        hits=intersection_errors([shaft],anchor_access)
        if hits: errors.setdefault('four_anchor_tool_paths',[]).extend(hits)
    # All six complete upper groups follow the reverse maintenance path.
    # Disconnect cables, release transport restraints and remove the owning
    # guide caps/servo straps first; adjacent mechanisms remain installed.
    import mechanism
    service=[item for item in scene(covers=False) if not item.group.startswith('lid')
             and item.group!='wand' and item.name!='powerbank'
             and not item.name.startswith('component_tie_powerbank_')]
    for index in range(6):
        number=index+1
        owner=mechanism.module(index,with_wand=False)
        moving=[item for item in owner if item.group in ('moving','moving_hw')
                or item.name==f'servo_{number}' or item.name.startswith('coupler_')]
        removed={item.name for item in moving}
        removed.add(f'soft_liner_{number}')
        removed.update(item.name for item in owner if item.name.startswith(
            (f'guide_washer_{number}_',f'guide_nut_{number}_','servo_tie_')))
        obstacles=[item for item in service if item.name not in removed]
        for offset in range(0,121,5):
            shifted=[Item(item.name,item.shape.translate((0,0,offset)),item.group,item.color) for item in moving]
            hits=intersection_errors(shifted,obstacles)
            if hits: errors.setdefault('upper_group_'+str(number),[]).extend([[offset,*hit] for hit in hits])
        print('Assembly access: upper group '+str(number)+' sampled',flush=True)
    assert not errors, 'Assembly/service access intersections: '+json.dumps(errors)[:5000]
    return {'cassette_top_tool_paths':tool_count,'cassette_removal_samples_per_module':25,
            'cassette_bench_clamps':clamp_access,
            'cassette_usb_insertion':usb_access,
            'controls_release_before_left_cassette':controls_access,
            'cassette_screw_head_continuous_paths':tool_count,
            'deck_insertion_samples':25,'case_holes_added':0,
            'frame_anchor_top_tool_paths':4,
            'upper_group_paths':6,'upper_group_samples_per_path':25,
            'evidence':'BREP tools continuous; translation sampled every 5 mm',
            'physical_assembly_performed':False,
            'limitations':['Actual plugs, fingers, cable slack, wood/epoxy and transport need prototype checks',
                           'Disconnect inter-module leads and remove stated obstacles before service']}
