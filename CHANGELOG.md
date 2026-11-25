# Changelog

## 1.4.0 (2026-11-25)

Tables. Underline and strikeout have their own tool. Holding the pen at the end of a stroke turns it into a shape. Text boxes grow with their text.

**New**
- Tables (Write › `Table`): pick up to 10 × 10 from the grid and click the page. Cells take the same formatting as text boxes. Double-click a cell to type; Tab and Shift+Tab move between cells, Tab in the last cell adds a row. Right-click for rows and columns above, below, left, right and delete. Drag a line (outer lines included) to set a column width or row height; rows grow with their text. With a table selected: line color and width, `Header row`, and font and size for the whole table. The corner handle scales cells, not text.
- Tables are saved as standard stamp annotations with their appearance, so other viewers show them. Search finds cell text.
- `Underline & strikeout` tool next to the highlighter, also in the quick menu. The highlighter bar no longer has a kind.
- Hold the pen still for half a second before lifting it: the stroke becomes a line, arrow, ellipse, rectangle or triangle with the pen's color, width and opacity. Keep moving to resize and turn it; lift to finish. The first undo brings back the hand-drawn stroke. With the highlighter, only straight lines. Settings › Notes › `Hold to make a shape` (on by default).
- Text boxes made by a click, the quick menu or a paste are auto width: they follow the longest line and wrap at the page edge. A box made by dragging, or after dragging its width handle, keeps its width. Right-click › `Auto width` switches.

**`deot` and MCP**
- New command `add table` (op `add.table`): `--rows`, `--columns` or `--cells`, `--column-widths`, `--header`. 1 to 100 rows and columns.
- `text --fields tables` returns each table's cell texts. `annot list` gives a table's `rows`, `columns`, `cells`, `columnWidths`, `rowHeights` and `header`. `find --include-annotations` also matches table cells (`row`, `column`).
- `annot update` on a table changes position, rotation and scale only.
- `add text --at x,y` makes an auto width text box (`--rect` keeps a fixed width). `annot update --width` or `--scale` makes it fixed. `annot list` gives `autoWidth`.

**Changed**
- The first start uses the system language: Korean on a Korean system, English otherwise. English is built in (Android included).
- Sticky note cards stay open until folded. The pin is gone.
- The auto backup count (default 10) is for all backups together, not for each document.
- Favorites keep the underline/strikeout kind in the new tool's slot.
- Saving again is faster: the stored original is no longer compressed and hashed on every save, and fonts and images shared by many pages are fingerprinted once.
- Dragging pressure strokes, lasso selection on pages with many strokes, and long typing in one text box (Undo memory) cost less.

**Fixed**
- The page number box showed a scroll bar.
- The italic `I` did not fit its button.

File format: schema 9, written only when the document has a table or an auto width text box (older versions show them but can't edit them). Without either the file is written as before. Settings: `shapeRecognition` added; a favorite can hold the underline & strikeout tool.

**한국어**

표를 넣을 수 있습니다. 밑줄·취소선이 따로 도구가 됐습니다. 펜 획 끝에서 멈추면 도형이 됩니다. 텍스트 상자가 글만큼 자랍니다.

**새 기능**
- 표(필기 › `표`): 격자에서 최대 10 × 10을 고르고 페이지를 누름. 칸 글자는 텍스트 상자와 같은 서식. 칸을 두 번 눌러 입력, Tab·Shift+Tab으로 칸 이동, 마지막 칸에서 Tab은 행 추가. 우클릭으로 위·아래·왼쪽·오른쪽에 행·열 넣기와 삭제. 선(바깥 선 포함)을 끌어 열 너비·행 높이, 글이 넘치면 행이 커짐. 표를 선택하면 선 색·굵기, `머리행`, 표 전체 글꼴·크기. 모서리 손잡이는 칸 크기만 바꾸고 글자 크기는 그대로.
- 표는 외형이 있는 표준 스탬프 주석으로 저장되어 다른 뷰어에서도 보임. 검색이 칸 글자도 찾음.
- 형광펜 옆에 `밑줄·취소선` 도구, 빠른 메뉴에도. 형광펜 설정 줄에서 종류 칸이 빠짐.
- 펜을 떼기 전에 0.5초 멈추면 획이 직선·화살표·타원·사각형·삼각형으로 바뀜(펜의 색·굵기·투명도). 떼지 않고 움직이면 크기와 방향이 따라오고, 떼면 확정. 첫 실행 취소는 손으로 그린 획으로 돌아감. 형광펜은 직선만. 설정 › 필기 › `멈추면 도형으로`(기본 켬).
- 클릭·빠른 메뉴·붙여넣기로 만든 텍스트 상자는 자동 너비: 가장 긴 줄에 맞추고 페이지 끝에서 줄바꿈. 끌어서 만든 상자와 너비 손잡이를 끈 상자는 너비 고정. 우클릭 › `자동 너비`로 바꿈.

**`deot`와 MCP**
- 새 명령 `add table`(op `add.table`): `--rows`, `--columns` 또는 `--cells`, `--column-widths`, `--header`. 행·열 1~100개.
- `text --fields tables`는 표마다 칸 글자를 줌. `annot list`는 표의 `rows`·`columns`·`cells`·`columnWidths`·`rowHeights`·`header`. `find --include-annotations`가 표 칸도 찾음(`row`, `column`).
- 표에 `annot update`는 위치·회전·배율만.
- `add text --at x,y`는 자동 너비 텍스트 상자(`--rect`는 고정 너비). `annot update --width`·`--scale`을 주면 고정 너비. `annot list`에 `autoWidth`.

**바뀐 것**
- 처음 실행할 때 시스템 언어를 따름: 한국어 시스템은 한국어, 그 밖에는 영어. 영어가 내장됨(안드로이드 포함).
- 메모 카드는 접을 때까지 펼쳐져 있음. 고정 단추는 없어짐.
- 자동 백업 보관 개수(기본 10)는 문서마다가 아니라 백업 전체 개수.
- 즐겨찾기의 밑줄·취소선 칸은 새 도구 칸으로 읽음(종류 유지).

**고친 것**
- 페이지 번호 칸에 스크롤바가 보이던 것.
- 기울임 `I`가 단추에 다 들어가지 않던 것.

파일 형식: 표나 자동 너비 텍스트 상자가 있는 문서만 schema 9로 씀(예전 버전은 보기만 하고 고치지 못함). 둘 다 없으면 전과 같음. 설정: `shapeRecognition` 추가, 즐겨찾기 칸에 밑줄·취소선 도구가 들어갈 수 있음.

## 1.3.0 (2026-11-16)

An AI assistant on the same PC can read and write the document open in DeotPDF (experimental, off by default). Sticky notes can stay open side by side.

**New**
- AI tool access (Settings › Notes › `AI tool access`, off by default). A program on the same PC that runs `deot` can read the open document and write into it. DeotPDF asks first; access is read only, AI writing, or full editing. Each AI write is one undo step and is not saved until you save.
- `AI` tab: show, hide or outline AI writing, erase it by page, all or the last request, select it, and a list of AI work with "Make mine".
- After an AI write the status bar shows where it wrote, with Show and Undo. A refused AI client can be let ask again from the status bar.
- Sticky note cards: pin several open at once (View › Open notes pins every note on the page), resize from the corner, drag by the top band (a line joins a moved card to its icon). Notes written by an AI carry a small `AI` mark. None of this is saved or undone.
- Claude Desktop extension: `DeotPDF-1.3.0-win-x64.mcpb` and the macOS ones. It includes its own `deot` and asks for the PDF folders.

**`deot` and MCP**
- `--live` on every command that reads or writes a document works on the document open in DeotPDF, with `--doc <id>` when several are open. New commands `deot live docs|context|view|goto|flash`; `DEOT_CLIENT` names the client. MCP tools `live_docs`, `live_context`, `live_goto`, `live_flash`; document tools take `live` and `doc`, `render` takes `view`.
- New options: `text --region x,y,w,h`, `annot list --near-text`.
- New error codes: `LIVE_NOT_RUNNING`, `LIVE_DISABLED`, `LIVE_DOC_AMBIGUOUS`, `LIVE_DOC_NOT_FOUND`, `LIVE_CONSENT_DENIED`, `LIVE_PERMISSION`, `LIVE_UNSUPPORTED`, `LIVE_CONFLICT`, `LIVE_NOTHING_IN_VIEW` (exit 1), `LIVE_CONSENT_TIMEOUT`, `LIVE_BUSY` (exit 3), `PAGE_NOT_TRANSFORMABLE` (exit 2). New warning `AI_NOTES_HIDDEN`.
- `deot mcp --root` takes several folders after one `--root`.
- `render --out` must be a `.png` file.
- `page shrink` and `page resize` refuse pages whose annotations from other apps can't move with the content (`PAGE_NOT_TRANSFORMABLE`), as the app does.

**Changed**
- Saving is faster: unchanged annotations keep their appearance and unchanged pages are written once.
- Converting other apps' annotations is all or nothing per source PDF.
- Touch: a finger drags a scroll bar from where it lands.
- The theme option is named "Use system setting".

**Fixed**
- PDFs with CCITT fax or JBIG2 images (common in scans) could not be saved.
- PDFs with deeply nested forms could not be saved.
- A save that failed while replacing the file (network folders, sync tools) could leave the document under no name. The original is now restored.
- One setting value this version doesn't know (for example from a newer version) reset all settings, and the next save wrote the defaults over recent files, positions and favorites.
- Hidden AI writing was still hit by the text highlighter, the text tool and search.
- Search did not find a word wrapped to the next line without a space.
- Delete removed the selected annotation while the outline or the ribbon had the keyboard.
- Backspace after Korean input was sometimes ignored.
- macOS and Linux: saving keeps the file mode, follows symbolic links, and works where the file system refuses `chmod`.
- `deot`: a range of two page IDs, `--expect-hash` checked against the bytes actually read, the same ID twice in `annot delete`.
- Pen hold menu: lifting the pen no longer presses a menu item.

File format: unchanged. Settings: `liveLink` and `aiHaloColor` added; older versions keep them.

**한국어**

같은 PC의 AI가 덧PDF에 열린 문서를 읽고 쓸 수 있습니다(실험 기능, 기본은 꺼짐). 메모 카드를 여러 장 펼쳐 둘 수 있습니다.

**새 기능**
- AI 도구 연결(설정 › 필기 › `AI 도구 연결`, 기본은 꺼짐). 같은 PC에서 `deot`를 쓰는 프로그램이 열린 문서를 읽고 필기를 씁니다. 연결 전에 덧PDF가 묻고, 권한은 읽기만 / AI 필기 쓰기 / 전체 편집. AI가 한 번 쓴 것은 실행 취소 한 단계이고, 직접 저장하기 전에는 저장되지 않습니다.
- `AI` 탭: AI 필기 보기·숨기기·강조, 페이지·전체·마지막 요청 단위로 지우기, 선택, AI 작업 목록과 `내 필기로`.
- AI가 쓴 뒤 상태 표시줄에 쓴 위치와 `보기`·`되돌리기`. 거절한 AI 클라이언트는 상태 표시줄에서 다시 묻게 할 수 있음.
- 메모 카드: 여러 장 고정(보기 › 메모 펼치기는 그 페이지 메모 전부), 모서리로 크기 조절, 위쪽 띠로 옮기기(옮긴 카드는 아이콘과 선으로 이어짐). AI가 쓴 메모에 `AI` 표시. 모두 저장·실행 취소와 무관.
- Claude Desktop 확장: `DeotPDF-1.3.0-win-x64.mcpb`와 macOS용. `deot`가 들어 있고 설치할 때 PDF 폴더를 고름.

**`deot`와 MCP**
- 문서를 읽고 쓰는 모든 명령에 `--live`(덧PDF에 열린 문서), 여러 개 열려 있으면 `--doc <id>`. 새 명령 `deot live docs|context|view|goto|flash`, 클라이언트 이름은 `DEOT_CLIENT`. MCP 도구 `live_docs`·`live_context`·`live_goto`·`live_flash`, 문서 도구에 `live`·`doc`, `render`에 `view`.
- 새 옵션: `text --region x,y,w,h`, `annot list --near-text`.
- 새 오류 코드: `LIVE_NOT_RUNNING`, `LIVE_DISABLED`, `LIVE_DOC_AMBIGUOUS`, `LIVE_DOC_NOT_FOUND`, `LIVE_CONSENT_DENIED`, `LIVE_PERMISSION`, `LIVE_UNSUPPORTED`, `LIVE_CONFLICT`, `LIVE_NOTHING_IN_VIEW`(종료 1), `LIVE_CONSENT_TIMEOUT`, `LIVE_BUSY`(종료 3), `PAGE_NOT_TRANSFORMABLE`(종료 2). 새 경고 `AI_NOTES_HIDDEN`.
- `deot mcp --root` 하나 뒤에 폴더 여러 개.
- `render --out`은 `.png` 파일만.
- `page shrink`·`page resize`는 내용과 같이 옮길 수 없는 다른 프로그램 주석이 있는 페이지를 거절(`PAGE_NOT_TRANSFORMABLE`). 앱과 같은 검사.

**바뀐 것**
- 저장이 빨라짐: 바뀌지 않은 필기는 외형을 다시 만들지 않고, 바뀌지 않은 페이지는 한 번만 씀.
- 다른 프로그램 주석 변환은 원본 PDF마다 전부 아니면 전무.
- 터치: 스크롤바를 손가락이 닿은 자리에서부터 끌기.
- 테마 항목 이름을 `시스템 설정 따르기`로.

**고친 것**
- CCITT 팩스·JBIG2 이미지가 든 PDF(스캔 문서에 흔함)가 저장되지 않던 것.
- Form이 여러 겹 겹친 PDF가 저장되지 않던 것.
- 파일 교체 중 저장이 실패하면(네트워크 폴더, 동기화 프로그램) 원래 이름에 파일이 남지 않을 수 있던 것. 이제 원본을 되돌림.
- 이 버전이 모르는 설정 값 하나(새 버전이 쓴 값 등) 때문에 설정 전체가 초기화되고, 다음 저장 때 최근 문서·위치·즐겨찾기가 기본값으로 덮어써지던 것.
- 숨긴 AI 필기가 글자 형광펜, 텍스트 도구, 검색에 걸리던 것.
- 띄어쓰기 없이 다음 줄로 넘어간 낱말이 검색되지 않던 것.
- 개요나 리본에 포커스가 있을 때 Delete가 선택한 필기를 지우던 것.
- 한글 입력 뒤 Backspace가 가끔 먹지 않던 것.
- 맥·리눅스: 저장할 때 파일 권한 유지, 심볼릭 링크 따라가기, `chmod`를 거부하는 파일 시스템에서도 저장.
- `deot`: 페이지 ID 두 개로 쓴 범위, `--expect-hash`를 실제로 읽은 바이트로 확인, `annot delete`에 같은 ID 두 번.
- 펜 홀드 메뉴: 펜을 뗄 때 메뉴 항목이 눌리지 않음.

파일 형식: 변화 없음. 설정: `liveLink`, `aiHaloColor` 추가(예전 버전도 지우지 않음).

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
