"""Independent MuPDF D10bc fixture check (optional PyMuPDF, not an app dependency).
Usage: python -I tools/page-transform-check.py <D10bc probe directory> <output directory>
"""
import json
import sys
from pathlib import Path
import fitz

fixture, output = map(Path, sys.argv[1:])
output.mkdir(parents=True, exist_ok=True)
checks = {}
with fitz.open(fixture / "source.pdf") as original, fitz.open(fixture / "editable.pdf") as clipped, fitz.open(fixture / "reedited.pdf") as grown:
    checks["page_copy_order"] = len(clipped) == len(grown) == 4
    checks["all_clipped_paper_sizes"] = all(abs(p.rect.width * 25.4 / 72 - (120 if i == 3 else 100)) < .001 and abs(p.rect.height * 25.4 / 72 - (100 if i == 3 else 120)) < .001 for i, p in enumerate(clipped))
    checks["reopened_page_grown"] = abs(grown[0].rect.width * 25.4 / 72 - 210) < .001 and abs(grown[0].rect.height * 25.4 / 72 - 297) < .001
    checks["original_unicode_search_and_copy"] = all(grown[0].search_for(w) and any(w in grown[0].get_textbox(r) for r in grown[0].search_for(w)) for w in ["Original PDF", "English 123", "복사 가능한"])
    checks["edited_unicode_search_and_copy"] = all(grown[0].search_for(w) and any(w in grown[0].get_textbox(r) for r in grown[0].search_for(w)) for w in ["English lines", "다시 편집", "추가"])
    checks["no_duplicate_text_layer"] = grown[0].get_text().count("English lines") == grown[0].get_text().count("Original PDF") == 1
    old = original[0].search_for("Original PDF")[0]
    new = grown[0].search_for("Original PDF")[0]
    checks["source_text_position_and_size_at_80_percent"] = max(abs(new[i] - .8 * old[i]) for i in range(4)) < .05
    checks["source_vector_elements_kept"] = len(grown[0].get_drawings()) >= len(original[0].get_drawings()) > 0
    checks["no_rasterized_page_background"] = all(not p.get_images() for p in grown)
    appearances = []
    for label, document in [("clipped", clipped), ("grown", grown)]:
        for index, page in enumerate(document):
            on = page.get_pixmap(matrix=fitz.Matrix(1.5, 1.5), annots=True)
            off = page.get_pixmap(matrix=fitz.Matrix(1.5, 1.5), annots=False)
            on.save(output / f"{label}-{index + 1}.png")
            for annotation in page.annots() or []:
                rectangle = annotation.rect * page.rotation_matrix * fitz.Matrix(1.5, 1.5)
                visible = rectangle & fitz.Rect(0, 0, on.width, on.height)
                changed = sum(on.pixel(x, y) != off.pixel(x, y) for y in range(max(0, int(visible.y0)), min(on.height, int(visible.y1))) for x in range(max(0, int(visible.x0)), min(on.width, int(visible.x1))))
                appearances.append({"file": label, "page": index + 1, "id": annotation.info.get("id"), "outside": visible.is_empty, "changedPixels": changed})
    checks["in_paper_appearances_visible"] = all(a["outside"] or a["changedPixels"] > 0 for a in appearances)
report = {"renderer": f"MuPDF {fitz.VersionFitz}", "passed": all(checks.values()), "checks": checks, "appearances": appearances}
(output / "page-transform-check.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(report, ensure_ascii=True))
sys.exit(0 if report["passed"] else 1)
