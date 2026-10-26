# Third-party notices

DeotPDF itself is licensed under GPL-3.0 (see [LICENSE](LICENSE)). It ships with, or is built from, the components below. Each keeps its own license.

| Component | Version | Used for | License |
|---|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) | 156.0.8076 | Rendering pages, extracting text | BSD-3-Clause / Apache-2.0, plus the licenses of the libraries it bundles (FreeType, ICU, libjpeg-turbo, libpng, zlib, OpenJPEG, Little CMS, AGG, Abseil, fast_float, simdutf, LLVM libc). Full texts in [`third_party/PDFium/licenses`](third_party/PDFium/licenses) |
| [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (`bblanchon.PDFium.Win32`) | 156.0.8076 | Prebuilt `pdfium.dll` | Packaging: Apache-2.0 (NuGet) / MIT ([`third_party/PDFium/LICENSE`](third_party/PDFium/LICENSE)) |
| [PDFsharp](https://github.com/empira/PDFsharp) | 6.2.4 | Reading and writing PDF objects | MIT, © empira Software GmbH |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | 1.5.4 | Polygon clipping for the eraser and lasso | Boost Software License 1.0, © Angus Johnson ([`third_party/Clipper2/License.txt`](third_party/Clipper2/License.txt)) |
| [.NET](https://github.com/dotnet/runtime) / [WPF](https://github.com/dotnet/wpf) | 10.0 | Runtime and UI framework (included in the portable build) | MIT, © .NET Foundation and Contributors |
| System.Security.Cryptography.Pkcs, Microsoft.Extensions.* abstractions | 10.0 | Pulled in by PDFsharp | MIT, © Microsoft Corporation |

Test-only packages (xUnit, Microsoft.NET.Test.Sdk, coverlet) are used when running the test suite and are not included in the release.

The PDFium and Clipper2 license files are also included in the release zips under `licenses/`.
