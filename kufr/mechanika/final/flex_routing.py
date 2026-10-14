"""Constant-length rolling return bends; dimensions are nominal cable assumptions.

Only the return bend is a defined R8 curve. Terminal approach points remain
routing anchors requiring physical dressing, strain relief and dry fitting.
"""
import math

RADIUS_MM = 8.0
# Stay behind the fixed capacitor clips and ahead of the tray's front beam.
FIXED_Y = 79.0
MOVING_Y = 63.0
CENTRE_Z = 28.0
LIFT_MM = 16.0
SAMPLE_DEGREES = 10
CLEARANCE_MM = .3
# Inflate chord capsules by the maximum radial chord error as well as clearance.
CHORD_ERROR_MM = RADIUS_MM * (1-math.cos(math.radians(SAMPLE_DEGREES/2)))
LANE_GAP_MM = .6


def rolling_return(x, fixed_z, moving_z):
    """Return centreline and lift weights, ordered fixed tie -> moving tail."""
    if fixed_z < CENTRE_Z + LIFT_MM/2 or moving_z < CENTRE_Z:
        raise ValueError('Rolling bend requires nonnegative straight legs throughout travel')
    points = [[x, FIXED_Y, fixed_z]]
    weights = [0.0]
    for angle in range(0, 181, SAMPLE_DEGREES):
        theta = math.radians(angle)
        points.append([x, (FIXED_Y+MOVING_Y)/2+RADIUS_MM*math.cos(theta),
                       CENTRE_Z-RADIUS_MM*math.sin(theta)])
        weights.append(.5)
    points.append([x, MOVING_Y, moving_z])
    weights.append(1.0)
    return points, weights


def posed_points(wire, lifts):
    """Apply each module's declared point weights; fixed trunks remain fixed."""
    points = [list(point) for point in wire['points']]
    weights = wire.get('point_lift_weights', {})
    for module, values in weights.items():
        lift = lifts.get(int(module), 0.0)
        if not math.isfinite(lift) or not 0 <= lift <= LIFT_MM:
            raise ValueError('Cable pose outside its declared lift')
        if len(values) != len(points):
            raise ValueError('Cable lift weights do not match centreline')
        for point, weight in zip(points, values):
            if not math.isfinite(weight) or not 0 <= weight <= 1:
                raise ValueError('Invalid cable point lift weight')
            point[2] += weight*lift
    return points


def loop_reservation(module, origin_x=71.0, pitch=48.0):
    """Complete return-bend sweep, excluding legs checked in matching poses."""
    x = origin_x+(module-1)*pitch
    radius = 2.1/2+CLEARANCE_MM+CHORD_ERROR_MM
    # Four OD2.1 and three OD1.2 lanes, with .3mm clearance around each wire.
    low = [x+22.0-radius, MOVING_Y-radius,
           CENTRE_Z-RADIUS_MM-radius]
    high = [x+35.95+.6+CLEARANCE_MM+CHORD_ERROR_MM, FIXED_Y+radius,
            CENTRE_Z+LIFT_MM/2+radius]
    return {'id': f'moving_loop_{module}', 'origin': low,
            'size': [b-a for a,b in zip(low, high)], 'lift_mm': LIFT_MM,
            'status': 'nominal R8 return-bend sweep; complete cable checked at matching poses; physical dry fit required'}


def usb_bank_tail(endpoint, side_x, final_y, radius=20.0):
    """Tangent R20 plug exit, 4mm drop and return into the fixed side lane.

    Points are for display; line/arc primitives retain the exact CAD curve for
    channel construction and finite-body checks. Existing USB plug projection
    remains 20mm. Delivered cable/connector dimensions still require dry fit.
    """
    x,y,z=endpoint
    drop=4.0
    theta=math.acos(1-drop/(2*radius))
    cy=y-20-2*radius*math.sin(theta)
    height=z-drop
    lateral=x+2*radius-side_x
    if not 0<lateral<2*radius or final_y<=cy:
        raise ValueError('USB exit cannot join the declared side corridor')
    phi=math.acos(1-lateral/(2*radius))
    sy=cy+2*radius*math.sin(phi)
    if final_y<=sy:
        raise ValueError('Insufficient USB side-lane approach length')
    pieces=[{'kind':'line','start':list(endpoint),'end':[x,y-20,z]}]
    def arc(centre,u,v,first,last):
        def point(angle):
            return [centre[i]+radius*(u[i]*math.cos(angle)+v[i]*math.sin(angle)) for i in range(3)]
        pieces.append({'kind':'arc','start':point(first),'mid':point((first+last)/2),
                       'end':point(last),'centre':list(centre),'radius_mm':radius,
                       'u':list(u),'v':list(v),'first':first,'last':last})
    arc((x,y-20,z-radius),(0,0,1),(0,-1,0),0,theta)
    arc((x,cy,height+radius),(0,0,-1),(0,1,0),theta,0)
    arc((x+radius,cy,height),(1,0,0),(0,1,0),math.pi,2*math.pi)
    arc((x+radius,cy,height),(1,0,0),(0,1,0),0,phi)
    arc((side_x+radius,sy,height),(-1,0,0),(0,-1,0),phi,0)
    pieces.append({'kind':'line','start':[side_x,sy,height],'end':[side_x,final_y,height]})
    points=[list(endpoint)]
    length=0
    for piece in pieces:
        if piece['kind']=='line':
            points.append(piece['end'])
            length+=math.dist(piece['start'],piece['end'])
        else:
            count=math.ceil(abs(piece['last']-piece['first'])/math.radians(5))
            for index in range(1,count+1):
                angle=piece['first']+(piece['last']-piece['first'])*index/count
                points.append([piece['centre'][i]+radius*(piece['u'][i]*math.cos(angle)+piece['v'][i]*math.sin(angle))
                               for i in range(3)])
            length+=radius*abs(piece['last']-piece['first'])
    return {'points':points,'primitives':pieces,'exact_length_mm':length,
            'minimum_radius_mm':radius,'sample_degrees_max':5}
