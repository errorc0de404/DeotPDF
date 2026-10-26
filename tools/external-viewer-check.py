"""Optional independent-renderer check (MuPDF via PyMuPDF; not a project dependency).

Usage: python -I tools/external-viewer-check.py <saved.pdf> <output folder>

Renders every page with and without annotations, writes PNGs and mupdf-check.json, and reports for each PdfNotes
annotation (/NM "PdfNotes:<id>") how many pixels inside its /Rect change when annotations are drawn. A visible
appearance changes pixels; exit code 1 when any app annotation is invisible in MuPDF.
"""
import json
import os
import sys

import fitz  # PyMuPDF

path, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
doc = fitz.open(path)
scale = fitz.Matrix(2, 2)
pages = []
ok = True
for index, page in enumerate(doc):
    on = page.get_pixmap(matrix=scale, annots=True, alpha=False)
    off = page.get_pixmap(matrix=scale, annots=False, alpha=False)
    on.save(os.path.join(out, f"mupdf-page{index + 1}.png"))
    results = []
    for annot in page.annots() or []:
        name = annot.info.get("id", "")
        if not name.startswith("PdfNotes:"):
            continue
        # Annotation rect in unrotated page space -> rendered (rotated) pixel space.
        r = (annot.rect * page.rotation_matrix * scale) & fitz.IRect(0, 0, on.width, on.height)
        changed = 0
        for y in range(int(r.y0), int(r.y1)):
            for x in range(int(r.x0), int(r.x1)):
                if on.pixel(x, y) != off.pixel(x, y):
                    changed += 1
        visible = changed > 0
        ok &= visible
        results.append({"name": name, "subtype": annot.type[1], "changedPixels": changed, "visible": visible})
    pages.append({"page": index + 1, "rotation": page.rotation, "annotations": results})
report = {"renderer": f"MuPDF {fitz.VersionFitz} (PyMuPDF {fitz.VersionBind})", "file": os.path.basename(path), "allVisible": ok, "pages": pages}
with open(os.path.join(out, "mupdf-check.json"), "w", encoding="utf-8") as f:
    json.dump(report, f, ensure_ascii=False, indent=2)
print(json.dumps({"allVisible": ok, "annotations": sum(len(p["annotations"]) for p in pages)}))
sys.exit(0 if ok else 1)
