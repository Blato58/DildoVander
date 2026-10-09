"""Eight keyed feet and adhesive hook-loop interfaces, no cross-rail matrix."""
from common import Item, box


def build(p, feet=()):
    foot = box(46, 12, 6)
    for x in (5, 37):
        foot = foot.fuse(box(4, 4, 1, (x, 4, 6)))
    foot = foot.clean()
    items = []
    for index, at in enumerate(feet, 1):
        items.append(Item(f"cassette_foot_{index}", foot.translate(at), "print", "frame", part="cassette_foot",
                          metadata={"purpose": "Keyed glued cassette riser; broad hook-loop pad underneath. Flat on bed."}))
        items.append(Item(f"cassette_hook_loop_{index}", box(42, 10, 2, (at[0] + 2, at[1] + 1, 0)),
                          "cable", "frame", evidence="required adhesive hook-loop, nominal compressed thickness 2 mm"))
    return {"cassette_foot": foot}, items, {"feet": feet, "case_attachment": "adhesive hook-loop on eight pads; test on actual stained wood",
                                          "permanent_case_modifications": False, "required_consumables": ["PETG-compatible glue for keyed feet", "hook-loop pads"]}
