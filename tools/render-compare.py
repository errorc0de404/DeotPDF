"""Compare two folders of off-screen renders (PdfNotes.Probe d13-render) image by image.

Usage: python -I tools/render-compare.py <before folder> <after folder>
Prints, per PNG present in both, the share of pixels whose difference is above 8 (0-255) and the mean
difference, and writes diff-<name>.png masks into the after folder. Needs Pillow.
"""
import os
import sys

from PIL import Image, ImageChops


def main(before: str, after: str) -> None:
    for name in sorted(os.listdir(before)):
        if not name.endswith('.png') or name.startswith('diff-') or not os.path.exists(os.path.join(after, name)):
            continue
        a = Image.open(os.path.join(before, name)).convert('RGB')
        b = Image.open(os.path.join(after, name)).convert('RGB')
        if a.size != b.size:
            print(f'{name}: size {a.size} -> {b.size}')
            continue
        d = ImageChops.difference(a, b).convert('L')
        hist = d.histogram()
        total = a.size[0] * a.size[1]
        changed = sum(hist[9:])
        mean = sum(i * c for i, c in enumerate(hist)) / total
        print(f'{name}: {changed / total * 100:5.2f}% pixels differ >8, mean {mean:.2f}')
        if changed:
            d.point(lambda v: 255 if v > 8 else 0).save(os.path.join(after, 'diff-' + name))


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
