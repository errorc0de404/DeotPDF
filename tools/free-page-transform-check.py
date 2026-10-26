"""Independent MuPDF checks for the d10d fixture; UI preview bitmaps must never become saved page images."""
import json
import sys
from pathlib import Path

import fitz

source, output = map(Path, sys.argv[1:3])
output.mkdir(parents=True, exist_ok=True)
checks = []
for name in ("editable.pdf", "reedited.pdf"):
    with fitz.open(source / name) as document:
        checks.append({"Name": name + " four pages", "Passed": len(document) == 4})
        checks.append({"Name": name + " no page rasterization", "Passed": all(not p.get_images() for p in document)})
        checks.append({"Name": name + " original text retained", "Passed": any("Original PDF" in p.get_text() for p in document)})
        for index, page in enumerate(document):
            page.get_pixmap(matrix=fitz.Matrix(1, 1)).save(output / f"{name}-{index + 1}.png")
result = {"Passed": all(check["Passed"] for check in checks), "Checks": checks}
(output / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))
sys.exit(0 if result["Passed"] else 1)
