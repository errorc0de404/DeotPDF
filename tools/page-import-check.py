"""Optional independent check of D13g page import (MuPDF via PyMuPDF and numpy; not project dependencies).

Usage: python -I tools/page-import-check.py <d13-import folder> <output folder>

Opens saved.pdf written by `PdfNotes.Probe d13-import` with MuPDF and compares it with the files it was made from:
pages 1, 5, 6 are pages 1-3 of document.pdf and pages 2-4 are pages 2, 4, 5 of other.pdf. For each pair the text,
the shown size and the rendered pixels must match. Writes PNGs and page-import-check.json; exit code 1 on any mismatch.
"""
import json
import os
import sys

import fitz  # PyMuPDF
import numpy

folder, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
saved = fitz.open(os.path.join(folder, "saved.pdf"))
document = fitz.open(os.path.join(folder, "document.pdf"))
other = fitz.open(os.path.join(folder, "other.pdf"))
expected = [(document, 0), (other, 1), (other, 3), (other, 4), (document, 1), (document, 2)]
scale = fitz.Matrix(1.5, 1.5)
results = []
ok = saved.page_count == len(expected)
for index, (source, number) in enumerate(expected[: saved.page_count]):
    page, original = saved[index], source[number]
    a = page.get_pixmap(matrix=scale, alpha=False)
    b = original.get_pixmap(matrix=scale, alpha=False)
    a.save(os.path.join(out, f"saved-page{index + 1}.png"))
    same_size = abs(page.rect.width - original.rect.width) < 0.5 and abs(page.rect.height - original.rect.height) < 0.5
    text_same = page.get_text().strip() == original.get_text().strip()
    differing = None
    if a.width == b.width and a.height == b.height:
        pa = numpy.frombuffer(a.samples, numpy.uint8).astype(numpy.int16).reshape(-1, 3)
        pb = numpy.frombuffer(b.samples, numpy.uint8).astype(numpy.int16).reshape(-1, 3)
        differing = int((numpy.abs(pa - pb).sum(axis=1) > 30).sum())
    passed = same_size and text_same and differing is not None and differing <= a.width * a.height // 1000
    ok = ok and passed
    results.append({"page": index + 1, "from": f"{os.path.basename(source.name)} p{number + 1}", "sameSize": same_size,
                    "sameText": text_same, "differingPixels": differing, "passed": passed})
report = {"passed": ok, "pages": saved.page_count, "results": results}
with open(os.path.join(out, "page-import-check.json"), "w", encoding="utf-8") as f:
    json.dump(report, f, ensure_ascii=False, indent=2)
print(json.dumps(report, ensure_ascii=False, indent=2))
sys.exit(0 if ok else 1)
