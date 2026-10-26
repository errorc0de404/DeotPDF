"""Independent MuPDF search/selection check for the D06-fixes probe fixture.
Usage: python -I tools/searchable-text-check.py <searchable.pdf> <output directory>
"""
import json
import sys
from pathlib import Path
import fitz

source, output = sys.argv[1:]
output = Path(output)
output.mkdir(parents=True, exist_ok=True)
with fitz.open(source) as document:
    page = document[0]
    text = page.get_text()
    words = ["한글", "English", "😀", "둘째 줄"]
    checks = {
        "unicode": all(word in text for word in [*words, "더블클릭"]),
        "search": all(page.search_for(word) for word in words),
        "selection_copy": all(word in "".join(page.get_textbox(rect) for rect in page.search_for(word)) for word in words),
        "one_occurrence": all(text.count(word) == 1 for word in words),
    }
    result = {"engine": f"MuPDF {fitz.VersionFitz}", "text": text, "checks": checks,
              "search_rects": {word: [list(rect) for rect in page.search_for(word)] for word in words}}
    page.get_pixmap(matrix=fitz.Matrix(2, 2), annots=True).save(output / "searchable-page.png")
    for word in words:
        for rect in page.search_for(word):
            page.add_highlight_annot(rect)
    page.get_pixmap(matrix=fitz.Matrix(2, 2), annots=True).save(output / "search-highlights.png")
(output / "search-text.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(result, ensure_ascii=True))
sys.exit(0 if all(checks.values()) else 1)
