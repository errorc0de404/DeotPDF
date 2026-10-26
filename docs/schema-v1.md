# 편집 메타데이터 스키마 v1 (D02)

> 현재 일반 저장·클립보드의 최신 버전은 [schema v4](schema-v4.md)다. 이 문서는 해당 이전 버전의 계약과 호환 정책을 유지한다.

> D06 사용자 추가 요청 이후 새 일반 저장은 [schema-v2.md](schema-v2.md)의 v2다. 이 문서는 기존 v1 형식과 좌표/DTO 계약을 보존한다. 현재 리더는 v1을 계속 복원하며 저장 시 v2로 이전한다.

코드 기준: `src/PdfNotes.Core/Serialization/SchemaV1.cs`, `SchemaV1Dtos.cs`. 이 문서와 코드가 다르면 코드를 기준으로 이 문서를 고친다.

## 1. 저장 단위

PDF 계층(`Pdf/Writing/EditablePdfWriter`, D04 구현)은 Core가 만든 `SerializedDocument`를 다음처럼 PDF 내부 앱 전용 스트림에 저장한다. 보이는 페이지 내용이나 일반 텍스트에 넣지 않는다. 구체적인 PDF 키는 6절.

| 단위 | 내용 | PDF 저장 위치 |
|---|---|---|
| Header | 앱 ID, 스키마 버전, 단위 선언, 문서 ID, 페이지 목록(크기·회전·배경·주석 ID 순서), 이미지 자원 설명자 | 카탈로그의 앱 전용 스트림 |
| Envelope | 앱 ID, 스키마 버전, 문서 ID, 페이지 ID, 주석 1개 | 해당 주석 객체(표준 appearance를 가진 annotation)의 앱 전용 스트림 |
| Resource payload | 이미지 원본 바이트(PNG/JPEG/BMP) | 별도 스트림, 설명자의 SHA-256·길이로 검증 (`/PdfNotesResources` 사전의 `/R<자원 UUID N 형식>` 스트림, D05 구현) |

- JSON(UTF-8), camelCase, 열거형은 camelCase 문자열.
- `annotationIds` 배열 순서 = 쌓임 순서(0 = 맨 아래). 별도 zOrder 숫자는 저장하지 않는다.
- 페이지 원본 위치는 저장하지 않는다. 저장된 PDF의 페이지 순서가 곧 원본이며, 읽을 때 `PageSource.Pdf(index)`가 된다.
- 표시 상태(확대율, 스크롤, DPI, 선택, 검색 강조, 손잡이)는 어디에도 저장하지 않는다.

## 2. 좌표·단위

- `coordinateSpace = "cropbox-top-left-unrotated-pt"`: PDF point, 보이는 CropBox(∩MediaBox)의 좌상단 원점, +X 오른쪽, +Y 아래, /Rotate 적용 전, `/UserUnit` 곱한 실제 point.
- PDF 사용자 공간 → canonical: `x' = (x - CropLeft)·U`, `y' = (CropTop - y)·U` (`PdfPageBox.PdfUserToCanonical`).
- 페이지 회전: `PageFrame.CanonicalToDisplay` (시계 방향 0/90/180/270). 화면: `ViewTransform` = 회전 → `zoom·96/72` 배율 → 페이지 원점 이동(DIP) → `dpiScale`(장치 픽셀).
- `transform` = `[a b c d e f]` (PDF/WPF 행벡터 규약) 지역 좌표 → canonical. **v1 정상화 정책: transform은 항상 강체(회전+이동)**. 배율 변형은 확정 시 geometry·선 굵기·글자 크기에 반영하고 transform에 남기지 않는다(이중 스케일 방지). PDF 글자 하이라이트는 항상 identity.
- `textIndexUnit = "utf16-code-unit/grapheme-boundary"`: 텍스트 범위는 UTF-16 offset/length, 경계는 확장 grapheme cluster 경계여야 한다. 서로게이트 쌍·결합 문자를 가르는 범위는 거부한다.
- 색은 `#RRGGBB`(sRGB), 불투명도는 0~1 (`UI 투명도 T → 1 - T/100`).
- double은 System.Text.Json의 왕복 표현으로 기록되어 같은 모델은 비트 단위로 같은 값으로 복원된다.

## 3. 주석 type 태그

| type | 모델 | 주요 필드 |
|---|---|---|
| `textBox` | `TextBoxAnnotation` | text, runs[](start,length,fontFamily,sizePt,color,bold,italic,underline,strikethrough), alignment, widthPt, boxStyle, highlights[] |
| `ink` | `InkStroke` | points(평탄 배열 x,y,…), widthPt, color, opacity |
| `freeHighlight` | `FreeHighlightStroke` | ink와 동일 |
| `line` | `LineAnnotation` | lineKind(straight/quadratic/cubic), start, end, controls(0/2/4개 수), widthPt, dash, startArrow, endArrow, color, opacity |
| `pdfTextHighlight` | `PdfTextHighlight` | quads(8개 수 단위: UL,UR,LL,LR), sourceTextHint?, heightMode, manualHeightPt?, color, opacity |
| `image` | `ImageAnnotation` | resourceId, localBounds[x,y,w,h] |

- 텍스트 runs는 전체 텍스트를 빈틈없이 순서대로 덮는다. highlights는 정렬·비중첩.
- 텍스트 박스 높이는 레이아웃 결과이므로 저장하지 않는다.
- 데이터 무결성 한계(`ModelLimits`)는 UI 입력 범위(`InputLimits`)보다 넓다. 배율 확정으로 UI 최대값을 넘는 굵기·글자 크기가 생길 수 있기 때문이다.
- 각 type의 화면/외형/선택 처리기는 해당 단계(D04~D09)에서 등록한다. 외형 처리기가 없는 종류는 저장하지 않고(`NotSupportedException`) 읽을 때도 복원하지 않는다(6절). D04 등록: `ink`, `freeHighlight`. D05 추가 등록: `image`, D06 추가 등록: `textBox`(DTO 필드 변경 없음, schemaVersion=1 유지).

## 4. 읽기 규칙

| 상황 | 결과 |
|---|---|
| 헤더 없음 | `Invalid`, 모델 없음. 앱 표시 주석은 모두 읽기 전용(외형 유지). 경고 없음 |
| `app` 불일치, JSON 손상, 알 수 없는 필드, 단위 선언 불일치, ID 중복/빈 값 | `Invalid` |
| `schemaVersion != 1` | `Unsupported`, 모든 주석 읽기 전용 |
| 실제 PDF 페이지 수·크기(±0.001pt)·회전과 헤더 불일치 | `Invalid` (다른 앱이 페이지를 바꾼 것으로 보고 추측 복원하지 않음) |
| Envelope 손상/알 수 없는 type·필드/다른 문서 ID/헤더에 없는 ID/다른 페이지/중복/값 검증 실패 | 해당 주석만 `Rejected` → 외형을 배경에 남김 |
| 이미지 자원 누락·길이·SHA-256·형식 서명 불일치 | 그 자원을 쓰는 이미지 주석만 `Rejected`, 경고 |
| 공백만 있는 앱 텍스트 박스 | `DiscardedEmpty` → 복원도 표시도 하지 않음(사양 6.2) |
| 헤더에 있으나 Envelope가 없는 ID | 경고(복원 불가 개수) |

쓰기(`Serialize`)는 유효하지 않은 모델과 공백 텍스트 박스를 예외로 거절한다. 빈 박스 정리는 저장 스냅샷 생성 전 편집기(D06)의 책임이다. 참조되지 않는 자원은 기록하지 않는다.

## 5. 변경 정책

1. 직렬화 형태는 DTO 클래스만 결정한다. 모델 record를 직접 직렬화하지 않는다.
2. **어떤 필드 추가·삭제·의미 변경도 `schemaVersion`을 올린다.** v1 리더는 알 수 없는 필드를 거부한다. 구버전 앱이 신버전 데이터를 일부만 읽고 재저장해 정보를 조용히 잃는 것을 막기 위해서다.
3. 새 버전 리더는 이전 버전을 명시적 마이그레이션 함수로 읽고, 쓸 때는 항상 최신 버전으로 쓴다. 마이그레이션과 지원 범위는 이 문서에 추가한다.
4. 지원하지 않는 버전은 추측하지 않고 읽기 전용 외형으로 남긴다.
5. 영역 지우개 조각 등 새 기하 표현(D08)이 필요하면 v2로 올리고 표시·선택·저장·복사 경로를 함께 갱신한다.

## 6. PDF 저장 형식 (D04)

코드 기준: `src/PdfNotes.Pdf/Writing/{EditablePdfWriter,AnnotationAppearance,SafeFileSaver}.cs`, `Reading/EditablePdfReader.cs`.

| 위치 | 내용 |
|---|---|
| 카탈로그 `/PdfNotes` | Header JSON 스트림(FlateDecode) |
| 페이지 `/Annots`의 앱 주석 1개 | `/Subtype /Stamp`, `/F 4`(인쇄), `/NM (PdfNotes:<주석 UUID>)`, `/Rect` = 그린 범위(PDF 사용자 공간, 여유 1pt), `/AP /N` = 외형 Form XObject |
| 앱 주석의 `/PdfNotesData` | Envelope JSON 스트림(FlateDecode) |
| 앱 주석의 `/PdfNotesAP` | 외형 지문: SHA-256(외형 스트림 내용 + BBox + ExtGState CA/ca/BM + Matrix 유무). 이미지 외형은 래스터/soft-mask 스트림 및 Width/Height/BitsPerComponent/ColorSpace/Filter/Decode/DecodeParms/Mask/Interpolate까지 포함(D05) |
| 카탈로그 `/PdfNotesResources` | `/R<자원 UUID N 형식>` → 원본 PNG/JPEG/BMP 바이트의 압축 스트림. Header의 길이/SHA-256/형식과 실제 디코딩을 검증 |

- 외형 Form은 `cm`(지역 → canonical → PDF 사용자 공간, CropBox·UserUnit 반영) 하나로 canonical 좌표를 그대로 그린다. NoRotate/NoZoom 플래그가 없어 페이지 회전·확대를 따른다.
- 획: 한 경로를 `1 J 1 j`(둥근 끝·이음)로 한 번 칠한다. 점 1개는 굵기 지름의 원. 불투명도는 ExtGState `/CA /ca`(혼합 모드 Normal). 한 경로를 한 번 칠하므로 형광펜 자기 겹침은 한 번만 합성되고 다른 획끼리는 진해진다. 앱 화면도 같은 규칙(획마다 불투명도 레이어 1개)으로 그린다.
- 숫자는 소수 6자리까지 직접 서식화해 기록하므로 다시 읽은 값으로 계산한 지문이 저장 시 지문과 같다. PDFsharp `PdfReal`(소수 7자리로 기록)을 쓰는 BBox·ExtGState 값은 반드시 `AnnotationAppearance.Real`로 먼저 6자리 반올림한다. 전체 정밀도 값을 7자리→6자리로 두 번 반올림하면 6번째 자리가 달라져 자기 외형이 "외부 변경"으로 판정될 수 있다(D07에서 발견·수정, D06 텍스트에도 적용).
- 쓰기: 원본 PDF(열 때 읽은 바이트)를 수정 모드로 열고, 이전에 복원된 앱 주석을 뺀 뒤, 모델 순서로 페이지를 배치(원본 페이지는 인덱스별 최대 1회 이동/삭제, 생성 페이지는 그 자리에 배경을 그려 삽입; 같은 원본 페이지 중복은 D10 전까지 `NotSupportedException`), 모델의 회전을 `/Rotate`로 기록, 앱 주석과 Header를 새로 쓴다. 매번 전체 재작성이라 반복 저장해도 주석·크기가 늘지 않는다.
- 저장 검증: 임시 파일(`~<이름>.<guid>.tmp`, 같은 폴더)을 다시 읽어 PDFium으로 열리고, Header가 유효하고, 모든 앱 주석이 복원되며, 직렬화 결과가 저장한 스냅샷과 바이트 단위로 같을 때만 대상 파일을 `File.Replace`로 교체한다.
- 읽기: 표시(`/NM` 접두사 또는 `/PdfNotesData`)가 있는 주석만 후보다. `/Subtype /Stamp`, `/NM`의 UUID, 외형 존재, 지문 일치를 통과한 후보만 `SchemaV1.Read`에 넘기고, 결과 중 `/NM` UUID ≠ 주석 id, 물리적 페이지 ≠ Header의 페이지, 외형 처리기가 없는 종류는 제외하고 다시 읽는다. `Restored`/`DiscardedEmpty`인 주석만 배경 렌더용 PDF에서 제거한다. 나머지(다른 앱이 외형을 바꾼 것, 이동된 것, 지원하지 않는 종류·버전)는 외형을 배경에 남기고 개수를 `ReadOnlyCount`로 알린다.
- 저장할 때도 같은 읽기 규칙으로 원본에서 앱 주석을 제거하므로, 보기 전용으로 남은 외형과 다른 앱 주석은 그대로 보존된다.

### D05 이미지 및 변형 계약

- 표준 외형: PDFsharp `XImage`/`PdfImage`로 생성한 Image XObject와 PNG soft mask를 Form의 `/Resources /XObject /Image`에서 참조한다. 지역 이미지 사각형 → canonical → PDF 사용자 공간을 한 번만 적용한다. 화면은 원본 자원에서 최대 2048px 표시 이미지를 만들며 원본 바이트를 저장한다.
- 변형: 강체 행렬만 저장한다. 비례 배율은 획 점·굵기, 이미지 localBounds에 반영한다. 이미지 Shift 크기 조절은 이미지 지역 프레임에서 가로/세로를 독립 변경한다.
- 자원은 최대 256MiB, 디코딩 이미지 최대 64,000,000픽셀을 허용한다. 지원 형식이어도 실제 디코딩이 실패하면 편집 대상으로 복원하지 않고 원래 PDF 외형을 남긴다.
- 복제 주석/자원은 각각 새 UUID를 받는다. 불변 원본 바이너리 내부 공유는 허용한다. 원본/복제본 삭제 시 참조되지 않는 자원만 같은 Undo 단위에서 제거한다.
- 저장 검증은 모델과 메타데이터의 정확한 복원을 요구하되 원본에서 보존한 보기 전용 주석의 존재만으로 실패하지 않는다.

### D06 텍스트 외형·레이아웃 계약

- Text 프로젝트의 동일 FlowDocument 생성기로 편집/표시/외형을 배치한다. WPF 좌표값을 page point로 취급하며 최종 화면/PDF 변환을 각각 한 번 적용한다. 내부 여백은 항상 4pt, 테두리 1pt, 높이는 내용의 레이아웃 결과이며 저장하지 않는다.
- 실제 편집기는 활성 박스 하나에만 붙인다. 저장 모델은 UTF-16/grapheme 경계의 불변 text/runs로 읽으며 컨트롤 내부 XAML/RTF를 저장하지 않는다. 선택/IME/손잡이는 metadata에 기록하지 않는다.
- D01의 글꼴 외형 정책을 따라 shaping된 글자 윤곽을 0.02pt 허용오차로 평탄화한 경로로 저장한다. 글꼴 파일을 임베딩하지 않는다. 혼합 서식/밑줄/취소선/박스 opacity가 공통 DrawingGroup과 PDF 외형에 유지된다.
- 텍스트 Form에 `/PdfNotesText true`를 표시하고 외형 지문에 기본 경로/BBox와 모든 ExtGState ca/CA/BM을 추가한다. 표준 PDF 표시에는 이 앱 전용 키가 필요하지 않으며 schema v1 JSON은 변경하지 않는다.
- 없는 글꼴을 대체해 원래 레이아웃을 조용히 변경하지 않는다. PDF 리더는 해당 텍스트를 복원 대상에서 제외하고 원래 외형을 보기 전용 배경에 남기며 경고한다. 보기 전용 주석은 다시 저장해도 외형을 보존하지만 새 편집 Header의 주석으로 자동 재등록하지 않는다.
- 단일 텍스트 너비 변경은 font size를 유지해 줄바꿈/높이를 다시 계산한다. 다중 배율은 WidthPt/SizePt에 반영하며 transform은 강체다. UI 글자 크기 입력 1~400pt와 기존 v1의 배율 geometry 무결성 한계를 구분한다.
- `AnnotationGeometry`의 텍스트 bounds/hit 및 `AnnotationClipboard.Paste`에는 실제 높이 제공자 `TextLayout.Bounds`를 전달한다. Core가 WPF를 참조하거나 임의 높이를 추측하지 않는다.
- 저장 직전 `DocumentEdits.RemoveBlankTextBoxes`로 잠긴 앱 빈 박스도 정리한다. writer/verify도 공백 텍스트를 제외한다. 외부 읽기 전용 주석은 이 정리 대상으로 취급하지 않는다.
- 슬롯 5개/기본 스타일은 settings.json에만 기록하고 PDF 문서/Undo에 포함하지 않는다. 문서에 슬롯을 적용한 서식은 일반 편집 명령으로 저장/Undo 된다.
- WPF caret padding의 동작 근거: https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/RichTextBox.cs (CreateRenderScope).

### D07 직선·곡선 외형 계약

- JSON은 v1 `line` DTO 그대로다(필드 추가 없음, schemaVersion 변경 없음). 새 저장은 기존대로 v2 Header/Envelope.
- 기하 기준: `Core/Geometry/LineGeometry`. 제어 다각형 [Start, controls…, End], 조절점은 통과점이 아니다. 화면 painter·PDF 외형·hit·bounds가 같은 몸통/화살표 기하를 쓴다.
- 화살표: 길이 = 굵기×5, 반폭 = 굵기×2.25, 방향 = 끝점 접선. 끝 접선 길이가 0이면 다음 서로 다른 조절점, 그것도 없으면 반대 끝, 모두 겹치면 +X(NaN 없음). 몸통은 화살표 길이×0.6 지점에서 잘라 둥근 끝이 머리 밖으로 나오지 않게 한다. 머리가 선 전체를 덮으면 머리만 그린다.
- 선 스타일: 모든 스타일이 둥근 끝(`1 J 1 j`). 대시 배열(굵기 배수): 파선 [3 2.5], 점선 [0 2], 일점쇄선 [3 2.5 0 2.5]. PDF는 배수×굵기, WPF는 두께 상대값으로 같은 수를 쓴다.
- 외형 Form: `/GS0 gs /L Do` 한 줄. `/L`은 `/Group <</S /Transparency>>` Form으로 몸통과 머리를 불투명하게 그리고, 바깥 ExtGState `/CA /ca`로 그룹을 한 번 합성한다. 머리 아래 겹침이 진해지지 않는다(화면은 선마다 PushOpacity 1개). 2차 곡선은 정확한 차수 상승 3차 `c`로 기록한다.
- 지문: 기본 지문 + `/L` 스트림 내용·BBox·Group /S·Matrix/Resources 유무. Form에 `/PdfNotesLine true`. 다른 앱이 그룹 그림을 바꾸면 보기 전용으로 남는다.
- 선은 `LineAnnotation` 타입이므로 D08 지우개 대상(Ink/FreeHighlight)과 구분된다.

