# Changelog

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
