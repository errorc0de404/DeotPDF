# Contributing

Bug reports are most useful with a PDF that shows the problem. For a larger change, open an issue first.

## Building

.NET SDK as pinned in `global.json`. Windows, macOS and Linux.

```powershell
dotnet build PdfNotes.slnx -c Release
dotnet test tests/PdfNotes.Tests -c Release
dotnet test tests/PdfNotes.Desktop.Tests -c Release
dotnet run --project src/PdfNotes.Desktop -c Release -- path/to/file.pdf
```

- `PdfNotes.Tests`: model, PDF reading and writing, text engine, CLI.
- `PdfNotes.Desktop.Tests`: the app window, headless.
- Android: `PdfNotes.Android.slnx`, needs the Android workload (`dotnet workload install android`) and JDK 21.
- Package versions are in `Directory.Packages.props` and the lock files are committed. After adding or updating a package, run `dotnet restore PdfNotes.slnx --force-evaluate` and add it to `THIRD-PARTY-NOTICES.md`.

## Code layout

Each layer only references the ones below it; `ArchitectureTests` and `LayerTests` check this.

| Project | Contents |
|---|---|
| `PdfNotes.Core` | Document model, coordinates, edit commands, Undo. No UI framework, no PDF engine. |
| `PdfNotes.Pdf` | PDFium (rendering, text) and PDFsharp (reading, writing). All engine calls. |
| `PdfNotes.Text`, `PdfNotes.Text.Skia` | Text layout, shared by the app and the CLI. |
| `PdfNotes.Editor` | Tools, input, editing session, files, settings, backups, search. |
| `PdfNotes.ViewModels` | Screen state and commands. No Avalonia types. |
| `PdfNotes.Render.Skia` | Annotation drawing that matches the PDF appearance. |
| `PdfNotes.UI` | The Avalonia view shared by desktop and Android. |
| `PdfNotes.Desktop`, `PdfNotes.Android` | Window or activity, and system services. |
| `PdfNotes.Commands`, `PdfNotes.Cli` | The `deot` commands, and the executable (CLI and MCP server). |

Rules:

- The document changes only through `DocumentSession.Execute(EditCommand)`. The model is immutable.
- Stored coordinates are page points from the top left of the crop box, before rotation. Screen conversion goes through `ViewTransform`.
- Documents are written through `OpenDocument` (snapshot, write, verify, replace), never with `File.*`.
- A new annotation kind is registered everywhere at once: serialization, PDF appearance, Skia painter, geometry, clipboard, Flatten. `PaintTests.EveryKindLooksLikeItsPdfAppearance` compares the painter with the PDF appearance.
- Saved format changes follow section 12 of [docs/file-format.md](docs/file-format.md).

## UI text

Strings are in `src/PdfNotes.Editor/Localization/ko.json` and `src/PdfNotes.Desktop/Languages/en.json`, same keys in both. Korean literals in code fail `LanguagePackTests`. No explanatory captions or long tooltips in the UI.

## CLI

A change to a command, option, output field or error code updates `docs/cli.md`, `docs/cli.ko.md` and the JSON schemas in `src/PdfNotes.Commands/Schemas` in the same pull request. `CliReadTests` checks real output against the schemas. CLI messages are in English.

## Commits and pull requests

Commit subjects describe what the app does now (`A dropped image lands where the pointer is`, not `Fix drop handler`). One topic per pull request; say how it was tested, and attach a PDF or screenshot for visible changes.

Contributions are released under the project's license, GPL-3.0.
