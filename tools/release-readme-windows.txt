DeotPDF {VERSION} (덧PDF) for Windows
https://github.com/errorc0de404/DeotPDF

[English]
Run DeotPDF.exe. deot.exe is the command-line tool (see docs/cli.md on GitHub).
The zip without "portable" in its name needs the .NET 10 runtime (x64):
https://dotnet.microsoft.com/download/dotnet/10.0
Settings and backups stay where 1.0 and 1.1 kept them (%LOCALAPPDATA%\PdfNotes).
To add DeotPDF to the "Open with" menu for PDF files (current user only, no admin):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\register-open-with.ps1
  (add -Unregister to remove it)

[한국어]
DeotPDF.exe를 실행하면 됩니다. deot.exe는 명령줄 도구입니다(GitHub의 docs/cli.ko.md 참고).
이름에 portable이 없는 zip은 .NET 10 런타임(x64)이 필요합니다:
https://dotnet.microsoft.com/download/dotnet/10.0
설정과 백업은 1.0·1.1과 같은 곳(%LOCALAPPDATA%\PdfNotes)을 그대로 씁니다.
PDF 우클릭 '연결 프로그램'에 덧PDF를 추가하려면(현재 사용자만, 관리자 권한 불필요):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\register-open-with.ps1
  (지우려면 -Unregister)

License: GPL-3.0 (LICENSE). Third-party licenses: THIRD-PARTY-NOTICES.md, licenses\
