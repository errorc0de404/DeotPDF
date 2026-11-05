# Third-party notices

DeotPDF itself is licensed under GPL-3.0 (see [LICENSE](LICENSE)). It ships with, or is built from, the components below. Each keeps its own license.

| Component | Version | Used for | License |
|---|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) | 156.0.8076 | Rendering pages, extracting text | BSD-3-Clause / Apache-2.0, plus the licenses of the libraries it bundles (FreeType, ICU, libjpeg-turbo, libpng, zlib, OpenJPEG, Little CMS, AGG, Abseil, fast_float, simdutf, LLVM libc). Full texts in [`third_party/PDFium/licenses`](third_party/PDFium/licenses) |
| [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (`bblanchon.PDFium.Win32`, `.Linux`, `.macOS`) | 156.0.8076 | Prebuilt PDFium library for each system | Packaging: Apache-2.0 (NuGet) / MIT ([`third_party/PDFium/LICENSE`](third_party/PDFium/LICENSE)) |
| [PDFsharp](https://github.com/empira/PDFsharp) | 6.2.4 | Reading and writing PDF objects | MIT, © empira Software GmbH |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | 1.5.4 | Polygon clipping for the eraser and lasso | Boost Software License 1.0, © Angus Johnson ([`third_party/Clipper2/License.txt`](third_party/Clipper2/License.txt)) |
| [.NET](https://github.com/dotnet/runtime) / [WPF](https://github.com/dotnet/wpf) | 10.0 | Runtime (included in the portable and macOS/Linux builds); WPF is the UI of the Windows app `DeotPDF.exe` | MIT, © .NET Foundation and Contributors |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | 12.1.4 | UI framework of the cross-platform app (`DeotPDF.Next` on Windows, `DeotPDF` on macOS and Linux) | MIT, © The AvaloniaUI Project ([`third_party/Avalonia/LICENSE.txt`](third_party/Avalonia/LICENSE.txt)) |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (`Avalonia.Angle.Windows.Natives`) | 2.1.27548 | Graphics on Windows for the cross-platform app | BSD-3-Clause, © The ANGLE Project Authors ([`third_party/Avalonia/ANGLE-LICENSE.txt`](third_party/Avalonia/ANGLE-LICENSE.txt)) |
| [MicroCom](https://github.com/kekekeks/MicroCom) (`MicroCom.Runtime`), [Tmds.DBus](https://github.com/tmds/Tmds.DBus) (`Tmds.DBus.Protocol`) | 0.11.6, 0.94.1 | Pulled in by Avalonia (Windows and macOS interop, Linux desktop services) | MIT, © Nikita Tsukanov; © Tom Deseyn ([`third_party/Avalonia`](third_party/Avalonia)) |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / [Skia](https://skia.org) | 3.119.4 | Drawing and the text engine of the cross-platform app and CLI | SkiaSharp MIT, © Microsoft Corporation; Skia BSD-3-Clause and the libraries it bundles, listed in [`third_party/SkiaSharp/THIRD-PARTY-NOTICES.txt`](third_party/SkiaSharp/THIRD-PARTY-NOTICES.txt) |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) / [HarfBuzz](https://github.com/harfbuzz/harfbuzz) | 8.3.1.5 | Text shaping of the cross-platform app and CLI | HarfBuzzSharp MIT, © Microsoft Corporation; HarfBuzz Old MIT (in the same notices file) |
| System.Security.Cryptography.Pkcs, Microsoft.Extensions.* abstractions | 10.0 | Pulled in by PDFsharp and the MCP SDK | MIT, © Microsoft Corporation |
| [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol.Core`) | 2.3.0 | `deot mcp` | Apache-2.0 (older contributions MIT), © Model Context Protocol a Series of LF Projects, LLC ([`third_party/ModelContextProtocol/LICENSE`](third_party/ModelContextProtocol/LICENSE)) |
| Microsoft.Extensions.AI.Abstractions | 10.11.0 | Pulled in by the MCP SDK | MIT, © Microsoft Corporation |
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (`@fluentui/svg-icons`) | 1.1.345 | Icons of the cross-platform app (`DeotPDF.Next`), as path data in `IconData.g.cs` | MIT, © Microsoft Corporation ([`third_party/FluentUISystemIcons/LICENSE`](third_party/FluentUISystemIcons/LICENSE)) |

Test-only packages (xUnit, Microsoft.NET.Test.Sdk, coverlet) are used when running the test suite and are not included in the release.

The license files of PDFium, Clipper2, the MCP SDK, Avalonia and SkiaSharp are also included in the release packages under `licenses/`.
