# 편집 메타데이터 스키마 v2 — D06 외부 텍스트 선택·검색

> 현재 일반 저장·클립보드의 최신 버전은 [schema v4](schema-v4.md)다. 이 문서는 해당 이전 버전의 계약과 호환 정책을 유지한다.

코드: `Core/Serialization/SchemaV2.cs`, `Pdf/Writing/SearchableText.cs`. 기존 v1 DTO/좌표/강체 transform/UTF-16 범위/자원 계약은 그대로 사용한다. 새 일반 저장은 Header와 모든 Envelope의 schemaVersion=2다. 스키마를 모르는 구버전은 외형을 보기 전용으로 보존한다.

## 변경 이유와 호환성

- v1의 텍스트 외형은 윤곽선만 있어 외부 본문 선택·검색이 불가능했다. v2는 동일한 외형과 위치에 표준 Unicode 본문 텍스트를 함께 저장한다.
- 구버전이 외형만 제거하고 편집하면 이전 검색 글자를 PDF 본문에 남길 수 있으므로 새 파일을 v1로 선언하지 않는다. 기존 v1 직렬화 API는 과거 형식 검증/마이그레이션용으로 유지한다.
- 현재 리더는 v1과 v2를 읽어 같은 불변 모델로 복원하고, 다음 저장은 v2다. Header/Envelope의 버전 혼합은 거절한다. 미지원 버전·변경된 외형/본문·누락 글꼴은 원래 외형과 본문 글자를 함께 보기 전용으로 보존한다.
- v1의 공백 텍스트 정리/자원 검증 정책을 유지한다. JSON 필드를 추가하거나 의미를 바꾸지는 않았다. 입력 크기/깊이 제한을 유지하고, PDF API를 Core에 넣지 않는다.

## 표준 본문 텍스트와 소유 관계

- TextLayout은 WPF 편집기와 동일 레이아웃에서 Unicode scalar별 위치 및 시각적 줄을 측정한다. 동일 줄의 글자를 한 Tj 문자열로 묶고 실제 폭/위치를 Type3 글꼴에 기록한다. 코드 공간은 1바이트, 글꼴당 최대 255자, ToUnicode bfchar 블록은 최대 100개다.
- Type3 CharProcs는 실제 글자 폭/BBox를 선언하되 잉크를 그리지 않는다. 본문은 Tr=3이다. 보이는 모양은 기존 shaping 윤곽선 appearance에 한 번만 남기고 글꼴 바이너리는 임베딩하지 않는다. 한글·영문·이모지의 ToUnicode는 UTF-16BE이며 PDFium의 보조 평면 surrogate 반환도 처리한다.
- 각 텍스트 박스가 Form XObject 하나와 이를 호출하는 페이지 Contents 스트림 하나를 소유한다. 표준 PDF 검색/선택에는 앱 전용 키가 필요하지 않다.
- annotation의 `/PdfNotesTextLayer`는 해당 Contents 객체를 참조한다. `/PdfNotesTextHash`는 호출 스트림·Form·BBox·글꼴/폭/CharProcs/ToUnicode의 지문이다. 스트림에는 `/PdfNotesTextId`, `/PdfNotesTextName`, `/PdfNotesTextForm`으로 ID와 XObject 호출을 기록한다.
- 복원 전 물리 페이지의 Contents/XObject 참조와 호출 내용, 지문을 모두 검증한다. 검증된 주석을 복원할 때만 그 스트림과 XObject 등록을 함께 제거한다. 원본 PDF 스트림이나 보기 전용 요소는 제거하지 않는다. 저장 때 현재 모델의 글자를 한 번만 생성한다.
- 지문은 압축 상태/간접 객체 번호에 의존하지 않는다. 정렬된 사전 키·정규화 수치·압축 해제 스트림 지문을 사용하며 재귀/객체 수/스트림 크기 한도를 검사한다.
- 반복 저장, 글자 수정·삭제, 이전 PDF 읽기/다시 저장을 검증한다. 검색 본문을 별도의 재편집용 숨은 사본으로 설명하지 않는다. 실제 편집 데이터는 Header/Envelope이고 본문은 외부 독자를 위한 표준 텍스트다.

## 후속 단계

- D11은 앱 주석의 모델 텍스트를 검색한다. 복원된 박스의 검색 본문이 배경에서 제거되므로 원본 검색과 중복되지 않는다.
- D12 Flatten에서는 윤곽선 외형을 본문에 합치고 표준 검색 글자를 유지한다. 편집 메타데이터와 소유 표시만 제거한다. D10 페이지 변환도 두 층을 동일하게 변환해야 한다.
- 검증: `TextPdfTests`(4종 페이지 회전과 박스 회전, Unicode/위치/배경 정리, 수정·삭제, 지문 변조, v1→v2, 255자 초과), `SchemaV2Tests`(두 버전 복원·구버전 거절·혼합 버전), MuPDF 검색/영역 복사.
- 제한: 회전된 여러 줄의 전체 복사 순서는 열람기 읽기 순서 분석에 따라 달라질 수 있다. 각 줄의 Unicode·글자 수·검색 위치를 검증하며 어떤 열람기에서도 완전한 읽기 순서를 보장했다고 보고하지 않는다.
- 표준 invisible text/ToUnicode 설명: https://pdfa.org/invisible-text-a-pdf-superhero-power/
