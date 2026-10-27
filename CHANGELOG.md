# Changelog

## 1.1.0 (2026-10-27)

- `deot mcp`: the CLI as an MCP server for Claude Desktop, Claude Code and other clients. Reading commands are tools of their own, writing goes through `apply` with the plan passed directly, `render` and `apply` previews come back as images, and files are limited to the `--root` folders. The command-line interface itself is unchanged.
- Settings › About: version, license and links, and a check for new versions. Release builds ask once at the first start whether to check at start (at most once a day); a newer release shows in the status bar and opens its page. Nothing is downloaded or installed.
- Release zips are now built by GitHub Actions (`tools/package-release.ps1`, `.github/workflows/release.yml`), ready for code signing through SignPath Foundation. All exe and dll files carry the product name DeotPDF and a plain version number. The app and `deot` now use the same versions of the shared Microsoft.Extensions libraries.
- `deot mcp`: CLI를 MCP 서버로 띄웁니다. 읽기 명령은 도구 하나씩, 쓰기는 계획을 바로 넘기는 `apply`로 하고, `render`와 `apply` 미리보기는 이미지로 돌려줍니다. 파일은 `--root` 폴더 안만 씁니다. 명령줄 사용법은 바뀌지 않았습니다.
- 설정 › 정보: 버전, 라이선스, 링크와 새 버전 확인. 정식 버전은 첫 실행 때 시작할 때 확인할지 한 번 묻고(하루 한 번까지), 새 버전이 있으면 상태 표시줄에 보이며 누르면 릴리스 페이지가 열립니다. 받거나 설치하지는 않습니다.
- 릴리스 zip을 GitHub Actions에서 만듭니다(`tools/package-release.ps1`, `.github/workflows/release.yml`). SignPath Foundation 코드 서명을 붙일 수 있게 준비했습니다. exe·dll 파일 속성의 제품 이름은 DeotPDF, 버전은 번호만 들어갑니다. 앱과 `deot`가 같이 쓰는 Microsoft.Extensions 라이브러리 버전을 맞췄습니다.

## 1.0.0 (2026-10-26)

First stable release. Nothing changed in the app or `deot` since 0.9.0. This version is the one that went through the full final check: all automated tests, the mouse-and-keyboard regression runs, re-saving files written by every earlier format version, rendering in pdf.js and MuPDF, performance measurements, and running with only the .NET Desktop Runtime installed (no SDK).

첫 정식 버전입니다. 앱과 `deot`의 기능은 0.9.0과 같습니다. 대신 이 버전으로 최종 점검을 모두 마쳤습니다. 자동 테스트, 실제 마우스·키보드 회귀 검사, 예전 형식으로 저장된 파일 다시 저장하기, pdf.js·MuPDF에서 보이는지 확인, 성능 측정, SDK 없이 .NET 데스크톱 런타임만 있는 상태에서 실행하기까지 확인했습니다.

## 0.9.0 (2026-10-26)

First public release. / 첫 공개 버전입니다.

**App**
- Pen, highlighter that snaps to PDF text (highlight, underline, strikeout), eraser (whole stroke or partial), text boxes with rich formatting and Korean IME, lines and curves, shapes, images, sticky notes
- Selection, lasso, move/resize/rotate, lock, copy and paste across pages and windows, undo/redo
- New notes (blank, grid, lined), page insert/delete/duplicate/rotate/reorder, shrink content, change page size, import pages, merge PDFs, export page ranges
- Search, table of contents, links, recent files
- Saves as a standard PDF that other viewers display and DeotPDF can edit again; flattened export
- Auto backup and crash recovery, light/dark themes, dark page reading mode, Korean and English UI

**`deot` CLI**
- Read: `info`, `render`, `text`, `annot list`
- Query: `find`, `layout`, `ink`, `space`, `measure`, `fonts`
- Write: `page *`, `add *`, `annot *`, `check`, and `apply` for atomic batches with guards

**Not yet**
- Installer (a script for the *Open with* menu is included), tablet pen pressure and touch, printing
