# 편집 메타데이터 스키마 v4 — D10 페이지 콘텐츠 배치

코드: `Core/Serialization/SchemaV4.cs`, `Commands/PageTransforms.cs`, `Pdf/Writing/PageContentWriter.cs`. v1~v3의 canonical point, 주석 종류·ID·잠금·쌓임 순서·UTF-16 범위·자원·outline·검색 본문 의미를 유지한다. 새 일반 저장과 주석 클립보드는 Header/Envelope 모두 schemaVersion=4다.

## 확장 필드

Header의 각 페이지에 다음 필드를 더한다.

- `source`: `{ "kind": "pdf", "index": 0 }` 또는 `{ "kind": "generated", "index": null }`. PDF 색인은 아래 보관 원본의 페이지 색인이며 현재 문서 순서와 다르다. 생성 페이지는 모눈/줄노트를 다시 그릴 수 있도록 종류를 유지한다.
- 선택 `content`: `{ "widthPt": 612, "heightPt": 792, "transform": [0.8, 0, 0, 0.8, 61.2, 79.2] }`. 처음 원본 CropBox의 표시 치수와 원본 canonical → 현재 canonical 누적 배치다. 양의 균일 배율+이동만 허용한다. 없으면 원본과 현재 페이지 좌표가 같다. 페이지의 기존 widthPt/heightPt/rotation은 현재 종이 경계다.
- TextBox Envelope의 `annotation.layoutScale`: 양의 실수, 기본 1. 박스 너비·글꼴 크기·수동 하이라이트 높이는 이미 현재 point로 저장하고, 이 값은 고정 4pt 여백·1pt 테두리·레이아웃 최소 길이의 배율이다. 현재 크기를 이 값으로 나누어 원래 단위에서 줄바꿈한 뒤 전체 외형을 배율로 그린다. 활성 편집기도 같은 단위와 변환을 사용하며 모델에 읽어올 때 글꼴 크기를 현재 point로 돌린다.

일반 페이지 축소는 기존 표시 페이지 사각형 기준이다. `PageTransforms`가 원본 배치를 누적하고 모든 주석의 기하·길이를 한 번 변환한다. 잠긴 주석은 잠금/ID를 유지한다. PDF 글자 하이라이트의 quads도 별도 페이지 명령으로 변환한다. 새 주석에는 과거 배율을 적용하지 않는다.

## PDF 저장과 원본 경계

- Catalog `/PdfNotesOriginal`: 앱에서 정상 복원한 주석·본문과 이전 앱 Catalog 메타데이터를 제거한 최초 원본 PDF 한 개를 압축 stream으로 보관한다. `/PdfNotesOriginalHash`는 그 디코딩 bytes의 SHA-256이다. 상한은 512MiB다. 반복 저장은 이 동일 보관본에서 다시 쓰므로 이전 변환 wrapper를 중첩하지 않는다.
- 원본 page contents에 균일 CTM과 **처음 CropBox의 영구 source clip**을 앞뒤 `q/Q`로 붙인다. MediaBox/CropBox는 현재 종이로 정규화하며 UserUnit은 CTM에 흡수하고 Rotate는 유지한다. 글자·벡터·이미지 스트림을 비트맵으로 바꾸지 않는다.
- 타 앱 주석의 표준 AP를 원래 CropBox로 자르는 Form wrapper에 넣고 Rect·좌표 배열·굵기와 함께 배치한다. 원본 경계 밖 전체 주석은 현재 표시에서 제외하지만 보관 원본에는 남는다. 사용자가 줄인 종이는 source clip과 별개이므로 저장·재열기 후 다시 키워도 콘텐츠가 돌아온다.
- 각 물리 페이지 `/PdfNotesBackgroundHash`는 앱 외형·검색 본문 제거 후 contents/resources/타 앱 주석/페이지 기하의 지문이다. PDFsharp가 저장 때 넣는 기본값을 포함해 저장 후 계산한다. 외부 편집으로 지문이 달라지면 물리 PDF를 새 원본으로 사용하고 source/content를 정규화하여 외부 변경을 보존한다. 이 경우 이전 보관본의 바깥 데이터 복원은 보장하지 않는다.
- 일부 앱 주석의 지문·폰트가 유효하지 않을 때도 물리 배경을 사용하여 보기 전용 외형을 보존한다. 보관본 손상/소스 색인·치수 불일치는 편집 헤더를 거절하고 모든 외형을 보기 전용으로 남긴다.
- 원본 보관으로 최초 저장 파일·메모리가 커질 수 있다. 삭제한 페이지도 보관 원본에 남을 수 있다. 10회 반복 저장 크기 안정성을 검사하며 대형 문서 비용은 D13 측정 대상이다.

## 호환성·현재 한계

- v4 Read는 v1~v3를 각 기존 리더로 넘긴다. 과거 페이지는 물리 PDF source, 텍스트 layoutScale=1로 시작한다. v1 의미를 재해석하지 않는다.
- v1~v3 앱은 v4 Header를 Unsupported로 보고 PDF 외형을 보존한다. 구버전 Serialize는 content/layoutScale 확장을 거절한다.
- 원래 CropBox 경계에 일부 걸친 타 앱 주석에 정상 `/AP /N`이 없으면 원본 외형을 벡터로 안전하게 자를 수 없어 현재 변환을 거절한다. 앱은 `PagePlacementValidation`으로 대상 전체를 적용 전에 확인하므로 일부 페이지만 바뀌지 않는다. AP 없는 임의 종류의 외형 생성은 후속 보완 대상이다.

## 후속 계약

- D11 검색은 현재 모델 순서와 `MainWindow.PageGlyphs`를 사용한다. 색인별 원본 glyph 캐시 위에 Content.Transform을 적용하고 처음 CropBox/현재 종이 밖의 글자는 제외한다. 모델 텍스트는 현재 `TextLayout` 위치를 사용한다.
- D12 Flatten은 현재 물리 본문과 표준 AP·Unicode 검색 본문을 합치고 `/PdfNotes*` 헤더/자원/원본 보관/지문/Envelope 연결을 제거한다. 보관 원본을 다시 출력 본문으로 쓰면 안 된다. Flatten된 원래 내용은 페이지만 복사에도 남는다.
- D13은 원본 보관·썸네일·변환 배경 캐시의 대형 문서 비용을 측정한다. 렌더 키에는 ID와 Content를 포함한 PageSetup을 함께 쓴다.
