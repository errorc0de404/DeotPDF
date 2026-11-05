# Changelog

## 1.2.1 (2026-11-05)

DeotPDF now runs on Windows, macOS and Linux. The app was rebuilt on Avalonia, and the text engine, the text box editor and `deot` are the same code on all three. Files, settings and backups carry over from 1.1. (There was no 1.2.0; the version number moved from 1.1 straight to 1.2.1.)

**New**
- macOS (Apple silicon and Intel) and Linux (x64) packages. Both include the .NET runtime. On macOS, right-click › Open the first time (no Apple developer signature); on Linux, `register-open-with.sh` adds DeotPDF to *Open with*.
- Tablet pen: strokes follow pen pressure (can be turned off), the pen's eraser end erases, the side button acts as the eraser, and a resting palm is ignored. Two fingers zoom and scroll on a touch screen; drawing with a finger is a setting (off by default).
- Text boxes: undo inside a box goes word by word, and one editing session is one step in the document's undo. Copy and paste inside DeotPDF keeps the formatting.
- A text box whose font isn't installed on this computer opens with a fallback font and stays editable. The font name is kept, and DeotPDF tells you once when the file opens.
- Search: an Aa button in the search bar turns on case-sensitive matching. It is off each time the app starts. `deot find` is unchanged.
- `deot` runs on macOS and Linux too.

**Changed**
- The Windows zip without "portable" now needs the .NET 10 runtime instead of the .NET 10 Desktop Runtime.
- Text is laid out by a new engine (Skia and HarfBuzz) on every system. Line breaks in text boxes made with 1.1 can differ slightly the next time you edit them.
- Saving keeps settings this version doesn't know, so an older and a newer version can share one settings folder.

**Fixed**
- A highlight on italic PDF text was drawn slanted. The band now follows the baseline.

**한국어**

덧PDF가 이제 윈도우, 맥, 리눅스에서 돌아갑니다. 앱 화면을 Avalonia로 다시 만들었고, 텍스트 엔진·텍스트 상자 편집기·`deot`도 세 시스템에서 같은 코드를 씁니다. 파일, 설정, 백업은 1.1에서 그대로 이어집니다. (1.2.0은 없습니다. 1.1에서 바로 1.2.1이 됐습니다.)

**새 기능**
- 맥(Apple 실리콘·인텔)과 리눅스(x64) 패키지. 둘 다 .NET 런타임이 들어 있습니다. 맥은 Apple 개발자 서명이 없어서 처음 한 번은 우클릭 › 열기로 실행합니다. 리눅스는 `register-open-with.sh`로 '다른 프로그램으로 열기'에 넣을 수 있습니다.
- 태블릿 펜: 필압대로 굵기가 바뀌고(끌 수 있음), 펜 뒤쪽 지우개와 측면 단추로 지우고, 화면에 손바닥을 대도 그려지지 않습니다. 터치 화면에서는 두 손가락으로 확대·이동합니다. 손가락으로 그리기는 설정에서 켭니다(기본은 꺼짐).
- 텍스트 상자: 상자 안의 실행 취소는 단어 단위이고, 한 번 편집한 것은 문서 실행 취소에서 한 단계입니다. 덧PDF 안에서 복사·붙여넣기하면 서식이 그대로 갑니다.
- 이 컴퓨터에 없는 글꼴을 쓴 텍스트 상자도 대체 글꼴로 열어서 고칠 수 있습니다. 글꼴 이름은 그대로 두고, 파일을 열 때 한 번 알려 줍니다.
- 검색: 검색 막대의 Aa 단추로 대소문자를 구분해서 찾습니다. 앱을 다시 켜면 꺼진 상태로 돌아갑니다. `deot find`는 그대로입니다.
- `deot`도 맥과 리눅스에서 돌아갑니다.

**바뀐 것**
- 윈도우의 portable이 아닌 zip은 이제 .NET 10 데스크톱 런타임이 아니라 .NET 10 런타임이 필요합니다.
- 모든 시스템에서 새 텍스트 엔진(Skia·HarfBuzz)이 글자를 배치합니다. 1.1에서 만든 텍스트 상자는 다음에 고칠 때 줄바꿈이 조금 달라질 수 있습니다.
- 설정을 저장할 때 이 버전이 모르는 항목은 지우지 않습니다. 그래서 예전 버전과 새 버전이 설정 폴더를 같이 써도 됩니다.

**고친 것**
- 기울임체 PDF 글자에 형광펜을 칠하면 띠가 기울어지던 문제. 이제 글자의 기준선을 따라 칠해집니다.

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
