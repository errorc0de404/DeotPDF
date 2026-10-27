DeotPDF {VERSION} (덧PDF)
https://github.com/errorc0de404/DeotPDF

[English]
Run DeotPDF.exe. deot.exe is the command-line tool (see docs/cli.md on GitHub).
The non-portable zip needs the .NET 10 Desktop Runtime (x64):
https://dotnet.microsoft.com/download/dotnet/10.0
To add DeotPDF to the "Open with" menu for PDF files (current user only, no admin):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\register-open-with.ps1
  (add -Unregister to remove it)

[한국어]
DeotPDF.exe를 실행하면 됩니다. deot.exe는 명령줄 도구입니다(GitHub의 docs/cli.ko.md 참고).
portable이 아닌 zip은 .NET 10 데스크톱 런타임(x64)이 필요합니다:
https://dotnet.microsoft.com/download/dotnet/10.0
PDF 우클릭 '연결 프로그램'에 덧PDF를 추가하려면(현재 사용자만, 관리자 권한 불필요):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\register-open-with.ps1
  (지우려면 -Unregister)

License: GPL-3.0 (LICENSE). Third-party licenses: THIRD-PARTY-NOTICES.md, licenses\
