"""BREP installation/service checks for the authored manufacturing sequence.

Tool shafts are continuous solids; complex material translation is sampled.
Connector reservations are finite design allowances, not measured plugs.
"""
import json
from geometry import ROOT, Item, bounds, cylinder
from check_fit import overlaps, scene
from service_geometry import SERVICE_MOUNTS, SHAFT_RADIUS, HEAD_RADIUS, SEAT_Z, SERVICE_LIFT
import layout


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
    result=[item for item in items if item.name==name or item.name in members
            or any(item.name.startswith('component_tie_'+member+'_') for member in members)]
    if name.startswith('left_'):
        result += [item for item in items if item.name in ('capacitor_1','capacitor_2')
                   or item.name=='usb_park_clip'
                   or item.name.startswith(('capacitor_tie_1','capacitor_tie_2','usb_clip_'))]
    return result


def service_obstacles(name, items):
    moving={item.name for item in cassette_items(name,items)}
    # The clip is screwed to this cassette, so travels with it. Remove the
    # speaker and unpark the disconnected USB plug before lifting the cassette.
    removed={'powerbank','speaker_left'} if name.startswith('left_') else set()
    return [item for item in items if item.name not in moving|removed
            and not (name.startswith('left_') and item.name.startswith('component_tie_speaker_left_'))
            and not item.name.startswith(('carrier_shaft_left_service_', 'carrier_head_left_service_',
                                          'carrier_shaft_right_service_', 'carrier_head_right_service_'))
            and item.group!='cover' and not item.group.startswith('lid')]


def validate():
    items=scene(covers=False,transport=True)
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
            'cassette_screw_head_continuous_paths':tool_count,
            'deck_insertion_samples':25,'case_holes_added':0,
            'frame_anchor_top_tool_paths':4,
            'upper_group_paths':6,'upper_group_samples_per_path':25,
            'evidence':'BREP tools continuous; translation sampled every 5 mm',
            'physical_assembly_performed':False,
            'limitations':['Actual plugs, fingers, cable slack, wood/epoxy and transport need prototype checks',
                           'Disconnect inter-module leads and remove stated obstacles before service']}
