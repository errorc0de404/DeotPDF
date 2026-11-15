# Contributing

Thanks for looking. Bug reports with a PDF that shows the problem are the most useful thing you can send; small fixes are welcome too. For a larger change, open an issue first so we can agree on the shape before you spend time on it.

## Building

You need the .NET SDK pinned in `global.json`. Windows, macOS and Linux all work.

```powershell
dotnet build PdfNotes.slnx -c Release
dotnet test tests/PdfNotes.Tests -c Release
dotnet test tests/PdfNotes.Desktop.Tests -c Release
dotnet run --project src/PdfNotes.Desktop -c Release -- path/to/file.pdf
```

`PdfNotes.Tests` covers the model, PDF reading and writing, the text engine and the CLI. `PdfNotes.Desktop.Tests` runs the app window headless. You rarely need both for one change: run the tests near what you touched, and the whole set once before you open the pull request.

The Android app is a separate solution, `PdfNotes.Android.slnx`, and needs the Android workload (`dotnet workload install android`) and JDK 21.

Package versions live in `Directory.Packages.props`, and the lock files are committed. If you add or update a package, run `dotnet restore PdfNotes.slnx --force-evaluate`, and add the package to `THIRD-PARTY-NOTICES.md`.

## How the code is laid out

Each layer only knows the ones below it. `ArchitectureTests` and `LayerTests` fail the build when that slips.

| Project | What it holds |
|---|---|
| `PdfNotes.Core` | The document model, coordinates, edit commands, Undo. No UI framework and no PDF engine. |
| `PdfNotes.Pdf` | PDFium (rendering, text) and PDFsharp (reading, writing). Engine calls stay here. |
| `PdfNotes.Text`, `PdfNotes.Text.Skia` | Text layout, shared by the app and the CLI. |
| `PdfNotes.Editor` | Tools, input, the editing session, file handling, settings, backups, search. |
| `PdfNotes.ViewModels` | Screen state and commands. No Avalonia types. |
| `PdfNotes.Render.Skia` | Draws annotations so they match what is written to the PDF. |
| `PdfNotes.UI` | The Avalonia view shared by desktop and Android. |
| `PdfNotes.Desktop`, `PdfNotes.Android` | The window or activity and the system services of each platform. |
| `PdfNotes.Commands`, `PdfNotes.Cli` | The `deot` commands and the executable (CLI and MCP server). |

A few rules that are easy to miss:

- The document changes only through `DocumentSession.Execute(EditCommand)`. The model is immutable.
- Stored coordinates are page points from the top left of the crop box, before rotation. Screen conversion goes through `ViewTransform`.
- Documents are written through `OpenDocument` (snapshot, write, verify, replace). Don't write a document with `File.*`.
- A new annotation kind has to be registered everywhere at once: serialization, PDF appearance, the Skia painter, geometry, clipboard, Flatten. `PaintTests.EveryKindLooksLikeItsPdfAppearance` compares the painter with the PDF appearance.
- A change to the saved format follows section 12 of [docs/file-format.md](docs/file-format.md).

## Text on screen

UI strings are in `src/PdfNotes.Editor/Localization/ko.json` and `src/PdfNotes.Desktop/Languages/en.json`, with the same keys in both. Korean literals in code fail `LanguagePackTests`. The UI avoids explanatory captions and long tooltips; if a control needs a paragraph to explain it, the control is probably wrong.

## The CLI

If you change a command, an option, an output field or an error code, update `docs/cli.md` and `docs/cli.ko.md` in the same pull request, and the JSON schemas under `src/PdfNotes.Commands/Schemas`. `CliReadTests` checks real output against them. CLI messages are in English.

## Commits and pull requests

Write the commit subject as a sentence about what the app does now, not what you did to the code (`A dropped image lands where the pointer is`, rather than `Fix drop handler`). Keep a pull request to one topic, say how you tested it, and attach a PDF or a screenshot when the change is visible.

By contributing you agree that your work is released under the project's license, GPL-3.0.
