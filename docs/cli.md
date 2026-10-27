# `deot` command-line guide

[한국어](cli.ko.md)

`deot` is the command-line side of DeotPDF. It reads a PDF, tells you what's on each page and where, and writes annotations: highlights, text boxes, lines, shapes, ink, images and sticky notes. It can also rearrange pages. Everything it writes uses the app's own engine, so the result looks the same as something drawn by hand in DeotPDF, and you can keep editing it there.

It was built for AI agents and scripts, which is why it works the way it does:

- Every command prints **one JSON object** to standard output. Logs and stack traces go to standard error.
- Errors have **stable codes** (`AMBIGUOUS_MATCH`, `PAGE_OVERFLOW`, ...). Decide on the code, not the message.
- `deot` **never decides for you**. If a phrase occurs twice, you get an error listing both, not a guess. Layout decisions (where a note goes, what to highlight) stay with the caller.
- The input file is **never modified**. Writing commands need `-o <output>` or `--dry-run`.

Contents: [Setup](#setup) · [Output and exit codes](#output-and-exit-codes) · [Coordinates](#coordinates-and-numbering) · [Commands](#commands) · [Writing](#writing-options) · [Formatted text](#formatted-text-and-styles) · [Tags and source keys](#tags-source-keys-and-group-keys) · [apply](#apply-many-edits-at-once) · [check](#check-rules) · [Workflow for agents](#a-workflow-that-works-well) · [MCP server](#mcp-server) · [Error codes](#error-codes)

## Setup

`deot.exe` ships in the same zip as `DeotPDF.exe` (see the [README](../README.md#download-and-run)). Add that folder to `PATH`, or call it by its full path.

From source:

```powershell
dotnet build PdfNotes.slnx -c Release
tools\PdfNotes.Cli\bin\Release\net10.0-windows\win-x64\deot.exe schema
```

`deot schema` lists every command, the exit codes and the error codes. `deot schema <command>` (for example `deot schema add text`) gives that command's options and a JSON Schema for its output. When this guide and `deot schema` disagree, `deot schema` is right.

## Output and exit codes

Success:

```json
{"ok": true, "command": "info", "sourceHash": "sha256:7cc1…", "pageCount": 120, ...}
```

Failure:

```json
{"ok": false, "error": {"code": "AMBIGUOUS_MATCH", "message": "...", "details": {"matches": [...]}}}
```

| Exit code | Meaning |
|---|---|
| 0 | OK |
| 1 | Invalid input (bad option, missing file, page that doesn't exist, ...) |
| 2 | The request is valid but can't be satisfied (no match, no space, would overflow the page, ...) |
| 3 | The file is locked, open in DeotPDF, or changed since you read it |
| 4 | Internal error |

Messages are always in English. Successful results may carry `warnings`, for example `FONT_FALLBACK` when a font isn't installed.

## Coordinates and numbering

- Units are PDF points (1/72 inch).
- The origin is the **top-left corner of the page before rotation**. x grows to the right, y grows downward. Rotating a page doesn't change these coordinates.
- Rectangles are `[x, y, w, h]` or `x,y,w,h` on the command line. Quads are `[[top-left], [top-right], [bottom-left], [bottom-right]]`. Matrices are `[a, b, c, d, e, f]` with `x' = a·x + c·y + e`, `y' = b·x + d·y + f`.
- `render` (and `--preview`) returns `pageToPixel` and `pixelToPage`, so you can go between a PNG you're looking at and page coordinates.
- Pages are numbered from 1. You can also use the page ID from `info`. For a PDF that has never been saved by DeotPDF, page IDs are derived from the file hash, so the same file always gives the same IDs.
- Character and word indexes (`text`, `find`, `layout`) start at 0. Ranges exclude the end: `--chars 10:20` is characters 10 to 19. They refer to the PDF's original text layer, so annotations or page shrinking don't shift them.

## Commands

| Group | Command | What it does |
|---|---|---|
| Read | `info <pdf>` | Pages, sizes, rotation, whether there's a text layer, annotation counts, `sourceHash` |
| | `render <pdf> -p <page> -o <png>` | Render a page or a `--region` to PNG. `--pages 1-8 --grid` makes a contact sheet. `--annotations all\|none\|tagged\|...` picks what to draw, `--overlay` draws boxes on the PNG only |
| | `text <pdf> -p <page>` | Characters, words and lines with their quads |
| | `annot list <pdf>` | Annotations with kind, bounds, style, text and tags. Filter with `-p`, `--kind`, `--tag`, `--batch-id`, `--source-key`, `--group-key` |
| Query | `find <pdf> --text <phrase>` | Where a phrase occurs. Ignores case, spacing, line breaks and hyphenation. `--within x,y,w,h` narrows it, `--fuzzy` allows near matches |
| | `layout <pdf> -p <page>` | Lines grouped into paragraphs and list items, with their character ranges and bullet/number markers |
| | `ink <pdf> -p <page>` | How much of each grid cell is drawn on. `--blocks` returns connected blobs (figures, diagrams) |
| | `space <pdf> -p <page> --size w,h` | Empty rectangles big enough for `w×h`, avoiding ink and `--avoid` areas. `--largest n` returns the biggest instead |
| Measure | `measure --text <text> --width <pt>` | Height, lines, fonts used and missing glyphs, computed exactly as `add text` will |
| | `fonts` | Font families the text engine accepts |
| Pages | `page duplicate`, `insert-blank`, `delete`, `move`, `rotate` | The usual |
| | `page shrink --scale 0.7` | Shrink content and its annotations, keeping the page size |
| | `page resize --size w,h --mode fit\|clip` | Change the page size. `clip` keeps the content at its size (handy for adding a writing margin) |
| | `page import --from other.pdf` | Insert pages from another PDF |
| | `page export --pages 1-3 -o part.pdf` | Write some pages as a separate PDF |
| Add | `add text` | Text box with formatted text. The height follows the text |
| | `add mark` | Highlight, underline or strikeout on PDF text, by `--chars`, `--words`, `--quads` or `--find` |
| | `add highlight-stroke` | Freehand highlighter stroke, or a filled rectangle (good for scanned pages) |
| | `add line` | Straight line or Bézier curve, optional arrowheads and dashes |
| | `add shape` | Rectangle, ellipse, triangle or polygon |
| | `add ink` | Pen stroke through the given points |
| | `add image` | PNG, JPEG or BMP |
| | `add note` | Sticky note |
| Edit | `annot update` | Change text, style, size, position or rotation |
| | `annot delete` | By ID, batch, source key or group key |
| | `annot order` | Bring to front, send to back, ... |
| | `annot lock` | Lock or unlock |
| | `annot copy` | Copy to another page or position |
| Check | `check --rules <json>` | Geometric checks: overlap with protected areas, outside the page, text overflow, ... |
| Batch | `apply <pdf> <plan.json>` | Run many operations in one go, all or nothing |

## Writing options

All commands that write accept:

| Option | |
|---|---|
| `-o <pdf>` | Where to write. It can't be the input file. If it exists, add `--overwrite` |
| `--dry-run` | Do everything except write the file. The JSON result is the same |
| `--preview <png or folder>` | Also render the changed pages to PNG |
| `--expect-hash <sha256:…>` | Refuse (`HASH_MISMATCH`) if the input isn't the file you read earlier |
| `--batch-id <id>` | Label everything written in this run |
| `--flatten` | Write a flattened PDF: notes merged into the page, no longer editable |

Annotation commands also take `--source-key`, `--group-key` and `--lock` (see below).

If the file is open in a DeotPDF window, writing is refused with `FILE_OPEN_IN_APP`. Close it first, or write to a different file.

## Formatted text and styles

`--text` for `add text` and `measure` accepts plain text with a few marks:

| Mark | Result |
|---|---|
| `**bold**` | **bold** |
| `*italic*` | *italic* |
| `__underline__` | underline |
| `~~strikeout~~` | ~~strikeout~~ |
| `==highlight==` | highlighted |

A backslash makes the next character literal. For full control, pass a JSON list of spans instead, or `@file.json`:

```json
[{"text": "Note: ", "bold": true, "color": "#C00000"}, {"text": "check figure 2", "highlight": "#FFF59D"}]
```

Span fields: `text`, `bold`, `italic`, `underline`, `strikeout`, `color`, `size`, `font`, `highlight`, `highlightOpacity`.

`--style` sets the box: `font`, `size`, `color`, `bold`, `italic`, `alignment` (`left`, `center`, `right`), `background`, `backgroundOpacity`, `border`, `borderColor`, `borderOpacity`, `highlightColor`, `highlightOpacity`. Anything you leave out uses the app's defaults.

Text boxes grow downward to fit. Pass `h` as `0` and let `deot` size it, or use `--no-auto-height` to treat `h` as a limit (`TEXT_OVERFLOW` if it doesn't fit). A box that would end up past the page edge fails with `PAGE_OVERFLOW` unless you add `--overflow allow`.

## Tags, source keys and group keys

Every annotation `deot` writes carries a hidden tag: author `ai`, the batch ID, and optionally a source key and group key. The tag isn't shown in the app, and it survives when someone moves or edits the annotation there.

- **Source key** (`--source-key lecture/p3/para-1`): a name for one annotation, unique in the document. Writing with a key that already exists **replaces** that annotation instead of adding a second one. The ID and stacking position stay the same. This is what makes re-running a script safe.
- **Group key** (`--group-key lecture/p3/para-1`): ties related annotations together, for example a text box, its highlight and its arrow. Use it to filter (`annot list --group-key`) or delete (`annot delete --group-key`) them together.
- Annotations without the tag (human handwriting, notes from other apps) are **read-only** to `deot`. Changing or deleting them fails with `FORBIDDEN`, unless you pass `--allow-untagged`.

Give each annotation its own source key, even when they belong together: `p3/para-1` for the text, `p3/mark-1` for the highlight. Reusing a key for a different kind of annotation fails with `SOURCE_KEY_KIND_MISMATCH`.

## `apply`: many edits at once

`apply` reads a plan and runs every operation in memory, then writes once. If any operation fails, nothing is written, and the error says which one (`details.opIndex`).

```json
{
  "batchId": "lecture-A-v3",
  "defaults": {
    "add.text": { "style": { "size": 11, "color": "#D32F2F" } }
  },
  "ops": [
    { "op": "page.resize", "pages": "3-5", "size": [1300, 540], "mode": "clip", "anchor": "left" },
    { "op": "add.mark", "page": 3, "find": "membrane potential", "sourceKey": "p3/mark-1" },
    { "op": "add.text", "page": 3, "rect": [975, 60, 305, 0],
      "text": "**Resting potential** is about -70 mV", "sourceKey": "p3/para-1", "groupKey": "p3/para-1" }
  ],
  "guards": [
    { "rule": "outsidePage", "select": { "tagged": true } }
  ]
}
```

```powershell
deot apply lecture.pdf plan.json --dry-run --preview-dir preview   # look first
deot apply lecture.pdf plan.json -o lecture-notes.pdf              # then write
```

Rules for plan files:

- `op` is the command name with dots instead of spaces: `add.text`, `page.resize`, `annot.delete`.
- Fields are the command's options in camelCase (`--source-key` becomes `sourceKey`). Unknown fields are an error, so typos don't pass silently.
- Values: rectangles as `[x, y, w, h]`, character ranges as `{"start": 10, "end": 20}`, JSON values (`points`, `style`, span lists) as JSON, flags as `true`.
- `page` numbers always mean the pages of the **input** file, even after an earlier op inserted or deleted pages. Pages created by an op can be named with `"as": "p3a"` and used later as `"page": "$p3a"`.
- Coordinates are those of the page **at the moment the op runs**. If an earlier op shrank the page, use the `matrix` in that op's result (run a `--dry-run` with just the page ops first), or target text by `chars`, `words` or `find`, which follow the text automatically.
- `defaults` gives fields per op type. Fields in the op itself win.
- `guards` are `check` rules run on the result. An error-level problem stops the write with `CHECK_FAILED` and lists the problems.
- The result has a `summary` with counts of annotations created, replaced, updated and deleted, and pages created, deleted and transformed.

## `check` rules

```powershell
deot check notes.pdf --rules '[{"rule": "overlapRegions", "regions": [[0,0,960,540]], "select": {"kinds": ["text"], "tagged": true}}]'
```

| Rule | Finds | Parameters |
|---|---|---|
| `overlapRegions` | Selected annotations that overlap any of the given areas | `regions`, `tolerance` |
| `overlapAmong` | Selected annotations that overlap each other | `tolerance` |
| `outsidePage` | Selected annotations that stick out of the page | `tolerance` |
| `textOverflow` | Text boxes taller than `maxHeight` | `maxHeight` |
| `minFontSize` | Text smaller than `size` pt | `size` |
| `missingGlyph` | Characters the font can't draw | |

Each rule takes `level` (`error` by default, or `warn`) and `select` with any of `kinds`, `ids`, `sourceKeys`, `groupKeys`, `batchIds`, `tagged`, `pages`, plus `exclude` (same fields except `pages`) to carve out exceptions, such as a small number badge you placed on purpose. `check` itself exits 0 even when it finds problems. Look at `passed`.

The checks use bounding boxes. A long diagonal arrow has a big box and can be flagged where it doesn't actually cross anything, or, more often, pass while visibly cutting through a figure. Look at a preview.

## A workflow that works well

This is roughly how an agent annotating lecture slides uses `deot`:

1. **Look.** `info`, then `render --pages 1-8 --grid --dpi 40` for an overview. Then for each page: `layout` (paragraphs and their character ranges), `find` for specific phrases, `ink --blocks` for figures, `space` for empty spots.
2. **Decide** what goes where. That part is yours. `deot` won't pick between candidates.
3. **Measure** text with `measure --width ...` before placing it. It returns exactly the size `add text` will produce.
4. **Make room first, if needed.** Put just the page operations in a plan and `--dry-run` it. The results contain `pageTransforms[].matrix` to map your coordinates. Highlights given by `chars`, `words` or `find` adjust on their own.
5. **Dry-run the whole plan** with `--preview-dir`, look at the PNGs, add `guards`, fix the plan.
6. **Always apply the full plan to the original file**, not to the previous output. Same input and same plan give the same result, and source keys make re-runs replace rather than duplicate.
7. A person opens the result in DeotPDF and finishes it by hand.

Small things learned the hard way:

- `layout` block ranges include the bullet or number at the start. Starting a highlight at `start + 1` usually looks cleaner.
- If a slide has no margin, `page.resize` with `"mode": "clip", "anchor": "left"` adds space on the right without moving anything. The matrix is the identity, so existing coordinates still work.
- To find a spot for a small badge next to something, try `space -p 3 --size 15,15 --within <area around the target> --gap 1 --cell 1`.
- Long words wrap by character, so text never overflows sideways. Height is the thing to watch (`measure --height` or the `textOverflow` rule).

## MCP server

`deot mcp` runs the same commands as an [MCP](https://modelcontextprotocol.io) server over standard input and output, so Claude Desktop, Claude Code and other MCP clients can call them as tools.

```powershell
deot mcp --root C:\Users\me\Lectures [--root <another folder>]
```

`--root` is required. The server only reads and writes files inside these folders, and a relative path starts at the first one. Anything outside is refused with `PATH_NOT_ALLOWED`.

Claude Code:

```powershell
claude mcp add deot -- C:\Tools\DeotPDF\deot.exe mcp --root C:\Users\me\Lectures
```

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "deot": {
      "command": "C:\Tools\DeotPDF\deot.exe",
      "args": ["mcp", "--root", "C:\Users\me\Lectures"]
    }
  }
}
```

The tools:

- **Reading** commands are one tool each: `schema`, `info`, `render`, `text`, `annot_list`, `find`, `layout`, `ink`, `space`, `measure`, `fonts`, `check`. Fields are the options in camelCase (`--include-annotations` becomes `includeAnnotations`), the input PDF is `pdf`, and a flag is `true`.
- **Writing** goes through `apply`. Instead of a file, pass the plan itself as `plan` (an object with `ops`, or just the list of ops). One edit is a plan with one op. Add `previews: true` to get the changed pages back as images, with or without `dryRun`.
- `page_export` is there too, since `apply` can't do what it does.
- `render` returns the PNG as an image. `out` is optional and only needed if you want to keep the file.

The text of every result is exactly the JSON the command prints, and a failed command comes back with `isError`. The commands and their output are the same as on the command line, so the rest of this guide applies as is. For an op's fields, call `schema` with that command (`{"command": "add text"}`).

## Error codes

`deot schema` returns the authoritative list as `errorCodes`.

| Code | Exit | Meaning |
|---|---|---|
| `INVALID_ARGUMENT` | 1 | Missing, unknown or out-of-range option or field |
| `UNKNOWN_COMMAND` | 1 | No such command |
| `FILE_NOT_FOUND` | 1 | An input, image, text, style, rules or plan file is missing |
| `PDF_OPEN_FAILED` | 1 | The PDF can't be opened |
| `PAGE_NOT_FOUND` | 1 | No such page number, page ID or `$name` |
| `ANNOTATION_NOT_FOUND` | 1 | No such annotation ID or source key |
| `RANGE_OUT_OF_TEXT` | 1 | `--chars` / `--words` past the end of the text |
| `NOT_TRANSFORMABLE` | 1 | Text marks can't be moved, rotated or copied like that |
| `INVALID_IMAGE` | 1 | Not a PNG, JPEG or BMP |
| `IMAGE_TOO_LARGE` | 1 | The render would be too big. Lower `--dpi` or use `--region` |
| `SAME_AS_INPUT` | 1 | The output (or preview) path is the input file |
| `PATH_NOT_ALLOWED` | 1 | `deot mcp` only: the path is outside the `--root` folders |
| `OUTPUT_EXISTS` | 1 | The output exists and `--overwrite` wasn't given |
| `NOT_FOUND` | 2 | `--find` has no exact match |
| `AMBIGUOUS_MATCH` | 2 | `--find` matched more than once (`details.matches`) |
| `EMPTY_RANGE` | 2 | No visible characters in that range |
| `NO_SPACE` | 2 | `space` found nothing |
| `REGION_OUTSIDE_PAGE` | 2 | `--region` doesn't overlap the page |
| `PAGE_OVERFLOW` | 2 | The text box would go past the page edge |
| `TEXT_OVERFLOW` | 2 | With `--no-auto-height`, the text is taller than the box |
| `LAST_PAGE` | 2 | Can't delete every page |
| `SOURCE_KEY_KIND_MISMATCH` | 2 | That source key belongs to a different kind of annotation |
| `DUPLICATE_SOURCE_KEY` | 2 | The key is used more than once, or twice in one `apply` |
| `FORBIDDEN` | 2 | Not an annotation `deot` made (use `--allow-untagged` to override) |
| `LOCKED_ANNOTATION` | 2 | Locked. Unlock with `annot lock --off`, or delete with `--include-locked` |
| `CHECK_FAILED` | 2 | `apply` guards found error-level problems (`details.problems`). Nothing written |
| `HASH_MISMATCH` | 3 | The input changed since `--expect-hash` |
| `FILE_OPEN_IN_APP` | 3 | A DeotPDF window has the file open |
| `FILE_LOCKED` | 3 | Another program is holding the input |
| `SAVE_FAILED` | 4 | Writing the output failed (3 if it was a lock) |
| `INTERNAL` | 4 | Something unexpected. `details.type` and a stack trace on standard error |

Warnings that can appear in successful results: `FONT_NOT_INSTALLED`, `FONT_FALLBACK`, `MISSING_GLYPH`, and `PAGE_OVERFLOW` / `TEXT_OVERFLOW` when you passed `--overflow allow`.
