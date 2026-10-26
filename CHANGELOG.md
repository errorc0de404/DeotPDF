# Changelog

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
