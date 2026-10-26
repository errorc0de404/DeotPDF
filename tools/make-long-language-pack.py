"""Long-string test language pack (D14e): every Korean text plus 75 % of itself, placeholders kept.
Usage: python -I tools/make-long-language-pack.py src/PdfNotes.Editor/Localization/ko.json <out.json>"""
import json, re, sys, math
src, out = sys.argv[1], sys.argv[2]
d = json.load(open(src, encoding="utf-8"))
hole = re.compile(r"\{\{|\}\}|\{[^{}]+\}")
for k, v in d["strings"].items():
    plain = hole.sub("", v).replace("\n", " ")
    extra = plain[: max(2, math.ceil(len(plain) * 0.75))]
    d["strings"][k] = v + extra
d["meta"] = {"language": "xx-long", "name": "긴 문자열 시험", "version": 1}
json.dump(d, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print(len(d["strings"]))
