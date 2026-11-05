DeotPDF {VERSION} (덧PDF) for Linux
https://github.com/errorc0de404/DeotPDF

[English]
Run ./DeotPDF. ./deot is the command-line tool (see docs/cli.md on GitHub).
No .NET install is needed. Korean text needs a Korean font such as Noto Sans CJK KR
(Debian/Ubuntu: sudo apt install fonts-noto-cjk).
To add DeotPDF to "Open with" for PDF files (current user only):
  ./register-open-with.sh
  (./register-open-with.sh --unregister removes it)
Settings and backups are kept in ~/.config/PdfNotes (or $XDG_CONFIG_HOME/PdfNotes).

[한국어]
./DeotPDF를 실행하면 됩니다. ./deot는 명령줄 도구입니다(GitHub의 docs/cli.ko.md 참고).
.NET을 따로 설치하지 않아도 됩니다. 한글을 보려면 Noto Sans CJK KR 같은 한글 글꼴이 있어야 합니다
(Debian/Ubuntu: sudo apt install fonts-noto-cjk).
PDF '다른 프로그램으로 열기'에 덧PDF를 넣으려면(현재 사용자만):
  ./register-open-with.sh
  (지우려면 ./register-open-with.sh --unregister)
설정과 백업은 ~/.config/PdfNotes(또는 $XDG_CONFIG_HOME/PdfNotes)에 있습니다.

License: GPL-3.0 (LICENSE). Third-party licenses: THIRD-PARTY-NOTICES.md, licenses/
