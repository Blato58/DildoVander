"""Conservative continuous translation clearance for the new bed joints only.

Positive separation of inflated axis-aligned envelopes proves clearance for
every independent lift. This does not prove cam rotation or cable bend shape.
"""
from copy import deepcopy
import math


def box_distance(a,b):
    return math.sqrt(sum(max(b[i]-a[i+3],a[i]-b[i+3],0)**2 for i in range(3)))


def moving_owners(wire,points):
    owners=[None]*len(points)
    if wire.get('point_lift_weights'):
        for key,weights in wire['point_lift_weights'].items():
            if len(weights)!=len(points):
                raise ValueError('Cable lift weights do not match centreline')
            for i,weight in enumerate(weights):
                if not math.isfinite(weight) or not 0<=weight<=1:
                    raise ValueError('Invalid cable lift weight')
                if weight:
                    if owners[i] not in (None,int(key)):
                        raise ValueError('Ambiguous moving cable ownership')
                    owners[i]=int(key)
        return owners
    for moving in wire.get('moving_endpoints',[]):
        indices=[i for i,point in enumerate(points) if point==moving['fixed_point']]
        if len(indices)!=1:
            raise ValueError('Missing or ambiguous fixed cable anchor: '+wire['id'])
        module=int(moving['component'].rsplit('_',1)[1])
        if module<1:
            raise ValueError('Invalid moving cable module')
        endpoint=moving['endpoint']
        if endpoint==wire['from']:
            selected=range(indices[0])
        elif endpoint==wire['to']:
            selected=range(indices[0]+1,len(points))
        else:
            raise ValueError('Moving endpoint does not own this wire')
        for i in selected:
            if owners[i] not in (None,module):
                raise ValueError('Ambiguous moving cable ownership')
            owners[i]=module
    return owners


def audit(wires,obstacles,lift_mm,clearance_mm=.3):
    if not math.isfinite(lift_mm) or lift_mm<=0 or not math.isfinite(clearance_mm) or clearance_mm<0:
        raise ValueError('Finite positive lift and nonnegative clearance required')
    if not obstacles or len({row['name'] for row in obstacles})!=len(obstacles):
        raise ValueError('Complete unique obstacle inventory required')
    modules={row['module'] for row in obstacles}
    for row in obstacles:
        extent=row['rest_bounds']
        if len(extent)!=6 or not all(math.isfinite(value) for value in extent) or any(extent[i]>=extent[i+3] for i in range(3)):
            raise ValueError('Invalid continuous obstacle extent')
    failures=[];minimum=None;checked=0
    for state in ('rest','transport'):
        for wire in wires:
            points=deepcopy(wire.get('transport_points') if state=='transport' and wire.get('transport_points') is not None else wire['points'])
            diameter=wire['outer_diameter_mm']
            if (len(points)<2 or not math.isfinite(diameter) or diameter<=0 or any(
                    len(p)!=3 or not all(math.isfinite(value) for value in p) for p in points)):
                raise ValueError('Invalid finite cable: '+wire['id'])
            owners=moving_owners(wire,points)
            if any(owner is not None and owner not in modules for owner in owners):
                raise ValueError('Moving cable refers to absent module')
            radius=diameter/2+clearance_mm+wire.get('curve_chord_error_mm',0)
            bank_curve=wire.get('bank_tail') if state=='rest' else None
            if bank_curve:
                # Enclose the exact circular bank tail between display samples.
                # Applying its chord error to the whole wire is conservative.
                radius+=bank_curve['minimum_radius_mm']*(1-math.cos(math.radians(bank_curve['sample_degrees_max']/2)))
            primitives=[(i,a,b,owners[i],owners[i+1]) for i,(a,b) in enumerate(zip(points,points[1:]))]
            primitives += [(f'corner-{i}',p,p,owners[i],owners[i]) for i,p in enumerate(points)]
            for segment,a,b,owner_a,owner_b in primitives:
                for obstacle in obstacles:
                    body=list(obstacle['rest_bounds'])
                    weights=wire.get('point_lift_weights',{})
                    if weights:
                        def fraction(index):
                            return max((row[index] for row in weights.values()),default=0)
                        if isinstance(segment,int):
                            weight_a,weight_b=fraction(segment),fraction(segment+1)
                        else:
                            weight_a=weight_b=fraction(int(segment.split('-')[1]))
                    else:
                        weight_a,weight_b=float(owner_a is not None),float(owner_b is not None)
                    co_moving=owner_a==owner_b==obstacle['module'] and weight_a==weight_b==1
                    body[5]+=0 if co_moving else lift_mm
                    cable=[min(a[i],b[i])-radius for i in range(3)]+[max(a[i],b[i])+radius for i in range(3)]
                    if not co_moving and (owner_a is not None or owner_b is not None):
                        cable[5]+=lift_mm*max(weight_a,weight_b)
                    gap=box_distance(cable,body)
                    row={'wire':wire['id'],'segment':segment,'obstacle':obstacle['name'],'state':state,
                         'co_moving_relative_proof':co_moving,'clearance_beyond_inflated_capsule_mm':gap,
                         'capsule_radius_mm':radius,'cable_envelope':cable,'obstacle_envelope':body}
                    checked+=1
                    if minimum is None or gap<minimum['clearance_beyond_inflated_capsule_mm']:
                        minimum=row
                    if gap<=1e-7:
                        failures.append(row)
    return {'status':'PASS' if not failures and checked else 'FAIL','pairs_checked':checked,
            'scope':'Continuous independent translation of new lower joint beams and joint hardware; conservative envelopes',
            'clearance_mm':clearance_mm,'lift_mm':lift_mm,'minimum':minimum,'collisions':failures,
            'truncated':False,'physical_verification':'NOT PERFORMED',
            'coverage':{'rest_wire_count':len(wires),'transport_wire_count':len(wires),
                        'modules':sorted(modules),'obstacle_count':len(obstacles)},
            'limitations':['Does not prove cam swept motion, cable bending or actual fastening of service loops']}


def source_obstacles():
    import harness
    import mechanism
    from geometry import bounds
    beams=[]
    for start,depth in mechanism.TRAY_LOWER_BEAMS:
        # The same stock primitives feed tray_halves(); subsequent cuts only
        # remove material. Add its deck to the conservative root envelope.
        stock=[bounds(solid) for solid in mechanism.lower_beam_solids(start,depth)]
        if len(stock)!=3:
            raise ValueError('Missing lower beam source geometry')
        extent=[min(row[i] for row in stock) for i in range(3)]+[max(row[i+3] for row in stock) for i in range(3)]
        extent[5]=max(extent[5],mechanism.M['tray_thickness'])
        beams.append(extent)
    result=[]
    for index in range(mechanism.M['count']):
        shift=(mechanism.M['origin_x']+mechanism.M['pitch']*index,
               mechanism.M['origin_y'],harness.REST_TRAY_Z)
        for number,extent in enumerate(beams,1):
            result.append({'name':f'joint_beam_{number}_{index+1}','module':index+1,
                           'rest_bounds':[extent[i]+shift[i%3] for i in range(6)]})
        joints=[item for item in mechanism.module(index,0,True) if item.name.startswith('tray_joint_')]
        if len(joints)!=3*len(mechanism.TRAY_JOINTS):
            raise ValueError('Incomplete bed joint hardware inventory')
        result += [{'name':item.name,'module':index+1,'rest_bounds':bounds(item.shape)} for item in joints]
    return result


def verify():
    import harness
    import mechanism
    if abs(harness.LIFT_MM-mechanism.M['lift'])>1e-7:
        raise ValueError('Cable and mechanical lift contracts differ')
    return audit(harness.HARNESSES,source_obstacles(),harness.LIFT_MM)
