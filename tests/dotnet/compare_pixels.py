"""Compare actual browser captures; keep a visible diff alongside the report."""
import json
import sys
from PIL import Image, ImageChops

reference, current, output = sys.argv[1:]
with Image.open(reference) as before, Image.open(current) as after:
    before, after = before.convert("RGB"), after.convert("RGB")
    if before.size != after.size:
        print(json.dumps({"matches": False, "reference_size": before.size, "current_size": after.size}))
        sys.exit(0)
    diff = ImageChops.difference(before, after)
    # Identical Chromium/font/assets should match exactly. A difference is
    # reported, never silently accepted via a broad percentage threshold.
    changed = sum(pixel != (0, 0, 0) for pixel in diff.getdata())
    if changed:
        diff.point(lambda value: min(255, value * 8)).save(output)
    print(json.dumps({"matches": changed == 0, "changed_pixels": changed,
                      "total_pixels": before.width * before.height,
                      "difference_box": diff.getbbox()}))
