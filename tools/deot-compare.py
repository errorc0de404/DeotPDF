"""Semantic comparison of two PDFs the CLI made (cli_plan 8, C5): regenerating from the same source and plan gives the same.

Usage: python -I tools/deot-compare.py <a.pdf> <b.pdf> [--deot <deot.exe>]
Reads both with `deot info` and `deot annot list` and compares page make-up (size, rotation, source, text layer,
annotation count) and every annotation in stacking order (kind, bounds, corners, transform, text, runs, style, quads,
points, lock, tags) with numbers within 0.01. Annotation and page IDs, batch IDs, hashes and paths are ignored: a new
run makes new ones. Prints one line per difference; exit code 1 when anything differs, 2 when deot fails.
The same comparison is CliCompare in tests/PdfNotes.Tests/Cli/CliTestSupport.cs.
"""

import json, os, subprocess, sys

DEFAULT_DEOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'PdfNotes.Cli', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'deot.exe')


def run(deot, *args):
    done = subprocess.run([deot, *args], capture_output=True)
    result = json.loads(done.stdout.decode('utf-8'))
    if not result.get('ok'):
        print(f'deot {" ".join(args)}: {result["error"]["code"]} {result["error"]["message"]}', file=sys.stderr)
        sys.exit(2)
    return result


def snapshot(deot, pdf):
    info = run(deot, 'info', pdf)
    pages = [{k: v for k, v in p.items() if k != 'pageId'} for p in info['pages']]
    annotations = []
    for a in run(deot, 'annot', 'list', pdf)['annotations']:
        a = {k: v for k, v in a.items() if k not in ('id', 'pageId')}
        if a.get('tags'):
            a['tags'] = {k: v for k, v in a['tags'].items() if k != 'batchId'}
        annotations.append(a)
    return {'pageCount': info['pageCount'], 'pages': pages, 'annotations': annotations}


def diff(x, y, path, out):
    if isinstance(x, dict) and isinstance(y, dict):
        for key in list(x) + [k for k in y if k not in x]:
            diff(x.get(key), y.get(key), f'{path}.{key}', out)
    elif isinstance(x, list) and isinstance(y, list):
        if len(x) != len(y):
            out.append(f'{path}: {len(x)} items vs {len(y)}')
        else:
            for i, (a, b) in enumerate(zip(x, y)):
                diff(a, b, f'{path}[{i}]', out)
    elif isinstance(x, (int, float)) and isinstance(y, (int, float)) and not isinstance(x, bool) and not isinstance(y, bool):
        if abs(x - y) > 0.01:
            out.append(f'{path}: {x} vs {y}')
    elif x != y:
        out.append(f'{path}: {json.dumps(x, ensure_ascii=False)} vs {json.dumps(y, ensure_ascii=False)}')


def main():
    args = sys.argv[1:]
    deot = DEFAULT_DEOT
    if '--deot' in args:
        i = args.index('--deot')
        deot = args[i + 1]
        del args[i:i + 2]
    if len(args) != 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    out = []
    diff(snapshot(deot, args[0]), snapshot(deot, args[1]), '$', out)
    for line in out:
        print(line)
    print(f'{len(out)} difference(s)')
    sys.exit(1 if out else 0)


if __name__ == '__main__':
    main()
