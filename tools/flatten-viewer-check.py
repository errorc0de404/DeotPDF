"""D12 external viewer check (optional, not a project dependency).

Compares the editable PDF and the Flatten PDF written by `PdfNotes.Probe d12-logic` in two viewers that are
independent of the app's PDFium: MuPDF (PyMuPDF) and pdf.js (rendered by tools/pdfjs-render.mjs beforehand).

    python -I tools/flatten-viewer-check.py <d12-logic folder> <pdf.js output folder> <result folder>
"""
import json
import os
import sys

import fitz  # PyMuPDF

run, pdfjs_folder, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)
editable = os.path.join(run, "lecture-current.pdf")
flat = os.path.join(run, "lecture-editable_flattened.pdf")
checks = []


def check(name, passed, detail=""):
    checks.append({"name": name, "passed": bool(passed), "detail": detail})
    print(("PASS " if passed else "FAIL ") + name + ": " + str(detail))


def mean_difference(a, b):
    if (a.width, a.height) != (b.width, b.height):
        return 255.0
    x, y = a.samples, b.samples
    return sum(abs(p - q) for p, q in zip(x, y)) / len(x)


def brightest_blue(pix, x0, y0, x1, y1):
    """Largest blue channel inside a pixel rectangle (RGB pixmap)."""
    best = 0
    for y in range(y0, y1):
        row = y * pix.stride
        for x in range(x0, x1):
            best = max(best, pix.samples[row + x * pix.n + 2])
    return best


# The foreign highlight of the sample: /Rect [68 662 330 684], /CA 0.5, on a 612x792 page (scale 1, top-left origin).
HIGHLIGHT = (71, 792 - 682, 327, 792 - 666)

# ----- MuPDF -----
a, b = fitz.open(editable), fitz.open(flat)
check("MuPDF: same page count", a.page_count == b.page_count == 2, f"{a.page_count}/{b.page_count}")
left = [annot.type[1] for annot in b[0].annots()] + [annot.type[1] for annot in b[1].annots()]
check("MuPDF: only the kept annotations remain (square without appearance, hidden ink)", sorted(left) == ["Ink", "Square"], left)
text = b[0].get_text()
check("MuPDF: source text and the text box are page text", "Original PDF" in text and "Flatten 한글 텍스트" in text.replace("\n", ""), text[:120].replace("\n", " "))
worst = 0.0
for i in range(a.page_count):
    p = a[i].get_pixmap(matrix=fitz.Matrix(1, 1), alpha=False, annots=True)
    q = b[i].get_pixmap(matrix=fitz.Matrix(1, 1), alpha=False, annots=True)
    p.save(os.path.join(out, f"mupdf-editable-{i + 1}.png"))
    q.save(os.path.join(out, f"mupdf-flattened-{i + 1}.png"))
    worst = max(worst, mean_difference(p, q))
check("MuPDF: flattened pages look like the editable file", worst < 1.5, f"worst mean channel difference {worst:.3f}")
q = b[0].get_pixmap(matrix=fitz.Matrix(1, 1), alpha=False, annots=True)
blue = brightest_blue(q, *HIGHLIGHT)
check("MuPDF: the merged foreign highlight stays translucent", 100 <= blue <= 160, f"blue {blue}")

# ----- pdf.js -----
with open(os.path.join(pdfjs_folder, "lecture-current.json"), encoding="utf-8") as f:
    js_editable = json.load(f)
with open(os.path.join(pdfjs_folder, "lecture-editable_flattened.json"), encoding="utf-8") as f:
    js_flat = json.load(f)
viewer = js_flat["viewer"]
check(f"{viewer}: same page count", len(js_editable["pages"]) == len(js_flat["pages"]) == 2)
kept = sorted(s for page in js_flat["pages"] for s in page["annotations"])
check(f"{viewer}: only the kept annotations remain", kept == ["Ink", "Square"], kept)
js_text = js_flat["pages"][0]["text"]
check(f"{viewer}: source text and the text box are page text", "Original PDF" in js_text and "Flatten 한글 텍스트" in js_text, js_text[:120])
worst = 0.0
for p_page, q_page in zip(js_editable["pages"], js_flat["pages"]):
    worst = max(worst, mean_difference(fitz.Pixmap(p_page["png"]), fitz.Pixmap(q_page["png"])))
check(f"{viewer}: flattened pages look like the editable file", worst < 1.5, f"worst mean channel difference {worst:.3f}")
pix = fitz.Pixmap(js_flat["pages"][0]["png"])
if pix.alpha:
    pix = fitz.Pixmap(pix, 0)
blue = brightest_blue(pix, *HIGHLIGHT)
check(f"{viewer}: the merged foreign highlight stays translucent", 100 <= blue <= 160, f"blue {blue}")

result = {"mupdf": fitz.VersionBind, "pdfjs": viewer, "passed": all(c["passed"] for c in checks), "checks": checks}
with open(os.path.join(out, "viewer-check.json"), "w", encoding="utf-8") as f:
    json.dump(result, f, ensure_ascii=False, indent=2)
sys.exit(0 if result["passed"] else 1)
