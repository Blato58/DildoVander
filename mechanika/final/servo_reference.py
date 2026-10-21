"""Owner-supplied SG90 reference, aligned to the cam axis; not a measured unit."""
from functools import lru_cache

import cadquery as cq

from geometry import ROOT, PARAMS


@lru_cache
def servo():
    # STEP: shaft runs along +Y through (X=5.625, Z=0). Rotate it toward
    # module +X, with its 3 mm output stub entering the stock horn hub.
    mechanism = PARAMS["mechanism"]
    shape = cq.importers.importStep(str(ROOT / "reference" / "SG90-Servo.stp")).val()
    return shape.rotate((0, 0, 0), (0, 0, 1), -90).translate((
        mechanism["cam_x"] - 17.25,
        mechanism["cam_y"] + 5.625,
        mechanism["cam_z"],
    ))
