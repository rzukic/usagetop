# count-items.py <picture.png> <x> <y> <w> <h> [<gap>]: how many things are
# drawn side by side in that strip of a bar, told apart by a gap of at least
# <gap> (8) clear columns. A column is clear when no pixel in it stands out from
# the column's usual colour, which is the bar's own there. Prints the count and
# each one's span, "4 1150-1201 1211-1262 ...".
import subprocess, sys

path, x0, y0, w, h = sys.argv[1], *map(int, sys.argv[2:6])
gap = int(sys.argv[6]) if len(sys.argv) > 6 else 8
# Cut to the picture: a strip past its edge comes back narrower.
crop = ["convert", path, "-crop", f"{w}x{h}+{x0}+{y0}", "+repage", "-alpha", "off", "-depth", "8"]
w, h = map(int, subprocess.run(crop[:-4] + ["-format", "%w %h", "info:"], capture_output=True, text=True, check=True).stdout.split())
raw = subprocess.run(crop + ["rgb:-"], capture_output=True, check=True).stdout

def inked(x):
    column = [raw[(y * w + x) * 3:(y * w + x) * 3 + 3] for y in range(h)]
    usual = sorted(column, key=sum)[h // 2]
    return any(max(abs(p[k] - usual[k]) for k in range(3)) > 70 for p in column)

spans, start, clear = [], None, 0
for x in range(w):
    if inked(x):
        if start is None:
            start = x
        clear, end = 0, x
    elif start is not None:
        clear += 1
        if clear >= gap:
            spans.append((start, end))
            start = None
if start is not None:
    spans.append((start, end))
print(len(spans), " ".join(f"{x0 + a}-{x0 + b}" for a, b in spans))
