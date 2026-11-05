DeotPDF.Next {VERSION} (덧PDF) for Windows
https://github.com/errorc0de404/DeotPDF

[English]
This is the cross-platform build of DeotPDF. Run DeotPDF.Next.exe. deot.exe is the command-line tool.
No .NET install is needed. It uses the same settings and backups as DeotPDF.exe (%LOCALAPPDATA%\PdfNotes).
To add it to the "Open with" menu for PDF files (current user only, no admin):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\register-open-with.ps1
  (add -Unregister to remove it)

[한국어]
덧PDF의 크로스플랫폼 판입니다. DeotPDF.Next.exe를 실행하면 됩니다. deot.exe는 명령줄 도구입니다.
.NET을 따로 설치하지 않아도 됩니다. DeotPDF.exe와 같은 설정·백업(%LOCALAPPDATA%\PdfNotes)을 씁니다.
PDF 우클릭 '연결 프로그램'에 넣으려면(현재 사용자만, 관리자 권한 불필요):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\register-open-with.ps1
  (지우려면 -Unregister)

License: GPL-3.0 (LICENSE). Third-party licenses: THIRD-PARTY-NOTICES.md, licenses\
