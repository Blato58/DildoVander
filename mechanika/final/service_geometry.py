"""Shared service cassette mount axes; millimetres in suitcase coordinates.

The narrow hex shafts enter from above; their handles stay above the case rim.
Nuts are loaded into the deck from below on the bench, before the insert enters
the case. No purchased PCB mounting-hole pattern is assumed.
"""
SERVICE_MOUNTS = {
    "left_service_carrier": ((67.5, 61), (19.2, 111.5), (67.5, 111.7)),
    "right_service_carrier": ((365, 155), (410, 155), (365, 247), (410, 236.5)),
}
SHAFT_RADIUS = 1.7
HEAD_RADIUS = 2.85
NUT_Z = 8.0
SEAT_Z = 15.0
SERVICE_LIFT = 120.0


def mount_points():
    return [point for rows in SERVICE_MOUNTS.values() for point in rows]
