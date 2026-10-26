"""Compare the files two builds wrote with `PdfNotes.Probe d14f-resave` (D14f: same inputs opened and saved again).

Usage: python -I tools/resave-compare.py <before folder> <after folder> [<report.json>]
Per output pair: byte equality, else page count/boxes/rotation, the catalog /PdfNotes header, every annotation (subtype,
rect, /NM, decoded /PdfNotesData, normal appearance stream), page content streams, extracted text and a 72 dpi render.
Also checks that resave.json (open status, restored/read-only/converted counts, errors) matches. Needs PyMuPDF.
Prints one line per differing file and a summary; exit code 1 when anything differs.
IDs are compared by order of first appearance: opening a PDF without app data gives the document and its pages new
random IDs on every run, in either build.
"""

import json, os, re, sys
import fitz

UUID = re.compile(rb'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
                  rb'|(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])')


class Ids:
    """Replaces each GUID (D or N format) with its order of first appearance in one file."""

    def __init__(self):
        self.seen = {}

    def __call__(self, data):
        if data is None:
            return None
        text = data if isinstance(data, bytes) else data.encode('utf-8')

        def number(m):
            key = m.group(0).replace(b'-', b'').lower()
            return b'<id%d>' % self.seen.setdefault(key, len(self.seen))
        return UUID.sub(number, text)


def stream(doc, xref):
    try:
        return doc.xref_stream(xref)
    except Exception:
        return None


def ref(doc, xref, key):
    t, v = doc.xref_get_key(xref, key)
    return int(v.split()[0]) if t == 'xref' else None


def describe(path):
    doc = fitz.open(path)
    ids = Ids()
    header = ref(doc, doc.pdf_catalog(), 'PdfNotes')
    header = ids(stream(doc, header)) if header else None
    pages = []
    for page in doc:
        annots = []
        for a in page.annots() or []:
            data = ref(doc, a.xref, 'PdfNotesData')
            ap = doc.xref_get_key(a.xref, 'AP/N')
            ap_stream = stream(doc, int(ap[1].split()[0])) if ap[0] == 'xref' else None
            annots.append((a.type[1], tuple(round(c, 3) for c in a.rect), ids(doc.xref_get_key(a.xref, 'NM')[1]),
                           ids(stream(doc, data)) if data else None, ids(ap_stream)))
        pix = page.get_pixmap(dpi=72, alpha=False)
        pages.append({
            'boxes': (tuple(round(c, 3) for c in page.mediabox), tuple(round(c, 3) for c in page.cropbox), page.rotation),
            'contents': ids(b''.join(stream(doc, x) or b'' for x in page.get_contents())),
            'annots': annots,
            'text': ids(page.get_text()),
            'pixels': pix.samples,
        })
    return {'header': header, 'pages': pages}


def first_difference(a, b):
    if a['header'] != b['header']:
        return 'catalog /PdfNotes header'
    if len(a['pages']) != len(b['pages']):
        return f"page count {len(a['pages'])} -> {len(b['pages'])}"
    for i, (pa, pb) in enumerate(zip(a['pages'], b['pages']), 1):
        for key in ('boxes', 'contents', 'text'):
            if pa[key] != pb[key]:
                return f'page {i} {key}'
        if len(pa['annots']) != len(pb['annots']):
            return f"page {i} annotation count {len(pa['annots'])} -> {len(pb['annots'])}"
        for j, (x, y) in enumerate(zip(pa['annots'], pb['annots']), 1):
            for k, name in enumerate(('subtype', 'rect', 'NM', '/PdfNotesData', 'appearance')):
                if x[k] != y[k]:
                    return f'page {i} annotation {j} {name}'
        if pa['pixels'] != pb['pixels']:
            return f'page {i} render'
    return None


def main(before, after, report=None):
    rows_a = {(r['Index'], r['Foreign']): r for r in json.load(open(os.path.join(before, 'resave.json'), encoding='utf-8'))}
    rows_b = {(r['Index'], r['Foreign']): r for r in json.load(open(os.path.join(after, 'resave.json'), encoding='utf-8'))}
    result = {'identicalBytes': 0, 'sameContent': 0, 'bothFailedSame': 0, 'different': []}
    for key in sorted(rows_a.keys() | rows_b.keys()):
        ra, rb = rows_a.get(key), rows_b.get(key)
        if ra != rb:
            result['different'].append({'key': key, 'what': 'resave.json row', 'before': ra, 'after': rb})
            continue
        if 'Error' in ra:
            result['bothFailedSame'] += 1
            continue
        fa, fb = os.path.join(before, ra['Output']), os.path.join(after, rb['Output'])
        if open(fa, 'rb').read() == open(fb, 'rb').read():
            result['identicalBytes'] += 1
            continue
        diff = first_difference(describe(fa), describe(fb))
        if diff:
            result['different'].append({'key': key, 'what': diff, 'input': ra['Input']})
        else:
            result['sameContent'] += 1
    for d in result['different']:
        print(f"DIFF {d['key']}: {d['what']}  {d.get('input', '')}")
    print(f"pairs {len(rows_a)}: identical bytes {result['identicalBytes']}, same content {result['sameContent']}, "
          f"same failure {result['bothFailedSame']}, different {len(result['different'])}")
    if report:
        json.dump(result, open(report, 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=str)
    return 1 if result['different'] else 0


if __name__ == '__main__':
    sys.exit(main(*sys.argv[1:4]))
