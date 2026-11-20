# find-icon.py <before.png> <after.png> <y0> <y1>: the centre of the leftmost
# thing that changed between the two pictures in rows y0 to y1, as "x y".
import subprocess, sys

def pixels(path):
    out = subprocess.run(["convert", path, "-alpha", "off", "-depth", "8", "rgb:-"], capture_output=True, check=True).stdout
    size = subprocess.run(["identify", "-format", "%w %h", path], capture_output=True, text=True, check=True).stdout.split()
    return out, int(size[0])

a, w = pixels(sys.argv[1])
b, _ = pixels(sys.argv[2])
y0, y1 = int(sys.argv[3]), int(sys.argv[4])
changed = {}
for y in range(y0, y1):
    row = y * w * 3
    for x in range(w):
        i = row + x * 3
        if max(abs(a[i + k] - b[i + k]) for k in range(3)) > 40:
            changed.setdefault(x, []).append(y)
if not changed:
    sys.exit(1)
left = min(changed)
right = left
while right + 1 in changed or right + 2 in changed:
    right += 1 if right + 1 in changed else 2
ys = [y for x in range(left, right + 1) for y in changed.get(x, [])]
print((left + right) // 2, (min(ys) + max(ys)) // 2)
