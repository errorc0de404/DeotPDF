# 편집 메타데이터 스키마 v3 — D08 영역 지우개 조각

> 현재 일반 저장·클립보드의 최신 버전은 [schema v4](schema-v4.md)다. 이 문서는 해당 이전 버전의 계약과 호환 정책을 유지한다.

코드: `Core/Serialization/SchemaV3.cs`, `Core/Geometry/StrokeRegion.cs`, `Core/Commands/EraseGesture.cs`. v1/v2의 DTO·좌표·강체 transform·UTF-16 범위·자원·v2 검색 본문 계약을 그대로 쓰고, `ink`/`freeHighlight`에 선택 필드 `outline` 하나를 더한다. 새 일반 저장은 Header와 모든 Envelope의 schemaVersion=3이다.

## 변경 이유와 호환성

- 영역 지우개는 지나간 원 부분만 지운다(사양 7.4). 굵은 획의 가장자리만 지운 경우처럼 남은 모양은 중심선+굵기로 표현할 수 없으므로, 남은 칠 영역 자체를 저장한다.
- `outline`: `[[x0, y0, x1, y1, ...], ...]` — 획의 local 좌표(point) 고리 목록, even-odd 채움. 고리마다 3점 이상. 이 필드가 있으면 `points`는 빈 배열이고 `widthPt`는 그린 굵기 기록일 뿐 모양을 바꾸지 않는다. 없으면 기존 v1 의미 그대로다.
- 조각 하나는 바깥 고리 1개 + 그 안의 구멍으로 된 연결된 영역 하나다. 한 획이 여러 조각으로 갈라지면 각각 독립 주석이다(가장 큰 조각이 원래 ID, 나머지는 새 ID, 원래 쌓임 위치에 연속 배치).
- 읽기: `SchemaV3.Read`가 v3를 읽고 v2/v1은 각자의 리더로 넘겨 같은 모델로 복원한다. Header/Envelope 버전 혼합은 거절한다. v1/v2 파일 안의 `outline`은 편집 데이터로 받지 않는다(Rejected → 외형 보기 전용).
- 구버전 앱: Header v3를 미지원 버전으로 보고 외형을 보기 전용으로 보존한다. `UnmappedMemberHandling.Disallow`라 필드만 끼워 넣은 경우도 거절된다. 지운 모양이 조용히 원래 획으로 되돌아가지 않는다.
- `SchemaV1.Serialize`/`SchemaV2.Serialize`는 조각이 있는 모델을 거절한다(`InvalidDataException`). 앱 클립보드도 v3로 쓴다(v1/v2 클립보드 데이터 읽기 유지).

## PDF 외형

- 조각: `/GS0`(ca/CA = opacity) 아래 `rg` 색으로 고리들을 `m … l … h` 후 `f*` 한 번. 형광펜 조각도 한 번 합성된다. 지문(`/PdfNotesAP`)은 기존 기본 지문(스트림·BBox·알파)과 같다.
- 흰 도형으로 덮지 않는다. 지운 자리는 칠이 없으므로 배경 PDF(격자 선 포함)가 그대로 보인다(`EraserPdfTests`).

## 기하

- 획의 칠 영역 = 중심선을 굵기/2만큼 둥근 끝·둥근 이음으로 키운 다각형(Clipper2 `InflatePaths`), 점 하나는 원. 화면 painter가 그리는 모양과 같다.
- 지우개 한 걸음 = 이전 입력점→현재 입력점 선분을 반지름만큼 키운 캡슐. 빠르게 움직여도 사이가 지워진다.
- 정밀도: 0.001pt 격자(`StrokeRegion.Precision`), 원호 평탄화 0.01pt, 0.01pt² 미만 부스러기 제거.
- 조각의 bounds/hit/Similarity(배율은 고리 좌표에 1회 반영)/올가미 판정은 `AnnotationGeometry`·`Lasso`가 처리한다. 조각의 굵기는 편집하지 않는다(상단 굵기 비활성).

## 후속 단계

- D09: 지우개는 글자 하이라이트를 지우지 않는다(EraseGesture는 StrokeAnnotation만 후보). 올가미 혼합 선택에 PDF 글자 하이라이트의 실제 표시 영역을 `Lasso.PaintedRegion`에 추가한다.
- D10: 페이지 축소·크기 변경 시 `outline` 고리도 같은 변환을 받아야 한다.
- D12: Flatten은 조각 외형을 그대로 본문에 합친다(추가 합성 없음).
