# 저장 형식: PDF 안의 편집 데이터

덧PDF는 별도 프로젝트 파일 없이 PDF 하나에 모든 걸 저장합니다. 필기 하나하나는 두 가지로 들어갑니다.

- **표준 외형**: 일반 PDF 주석의 appearance stream. 다른 뷰어는 이걸 보고 그립니다.
- **편집 데이터**: 덧PDF가 다시 열어서 고칠 수 있게 남겨 두는 JSON. 다른 뷰어는 무시합니다.

이 문서는 그 편집 데이터의 형식과 읽기·쓰기 규칙을 설명합니다. 현재 버전은 **schemaVersion 7**이고, 이전 버전과 달라진 점은 [마지막 절](#버전-기록)에 모았습니다.

코드와 이 문서가 다르면 코드가 맞습니다. 주요 코드는 `src/PdfNotes.Core/Serialization/SchemaV*.cs`, `SchemaV1Dtos.cs`, `src/PdfNotes.Pdf/Writing/{EditablePdfWriter,AnnotationAppearance,SafeFileSaver}.cs`, `src/PdfNotes.Pdf/Reading/EditablePdfReader.cs`입니다. 형식 식별자가 `PdfNotes`인 이유는 README의 '소스에서 빌드하기'를 보세요.

목차: [1. 저장 단위](#1-저장-단위) · [2. 좌표와 단위](#2-좌표와-단위) · [3. PDF 키](#3-pdf-안의-위치) · [4. Header](#4-header) · [5. 주석 종류](#5-주석-종류) · [6. 외형](#6-외형-그리기-규칙) · [7. 원본 보관과 페이지 배치](#7-원본-보관과-페이지-배치) · [8. 읽기](#8-읽기-규칙) · [9. 쓰기와 검증](#9-쓰기와-검증) · [10. Flatten](#10-flatten) · [11. 다른 앱 주석](#11-다른-앱-주석-바꾸기) · [12. 버전 정책](#12-버전-정책) · [버전 기록](#버전-기록)

## 1. 저장 단위

| 단위 | 내용 | 위치 |
|---|---|---|
| Header | 앱 ID, 스키마 버전, 단위 선언, 문서 ID, 페이지 목록(크기·회전·배경·원본 위치·주석 ID 순서), 이미지 자원 설명, 태그 표 | 카탈로그 `/PdfNotes` 스트림 |
| Envelope | 앱 ID, 스키마 버전, 문서 ID, 페이지 ID, 주석 하나 | 그 주석 객체의 `/PdfNotesData` 스트림 |
| 자원 | 이미지 원본 바이트(PNG/JPEG/BMP) | 카탈로그 `/PdfNotesResources`, 설명의 SHA-256·길이로 검증 |
| 원본 보관 | 처음 연 원본 PDF 한 벌 | 카탈로그 `/PdfNotesOriginal` |

- JSON은 UTF-8, camelCase, 열거형도 camelCase 문자열입니다. 스트림은 FlateDecode로 압축합니다.
- 페이지의 `annotationIds` 순서가 쌓인 순서입니다(0이 맨 아래). zOrder 숫자는 따로 없습니다.
- 확대율, 스크롤, 선택, 검색 강조 같은 화면 상태는 저장하지 않습니다.
- 직렬화 형태는 DTO 클래스(`SchemaV1Dtos.cs` 등)만 정합니다. 모델 record를 직접 직렬화하지 않아서, 모델을 고쳐도 파일 형식이 몰래 바뀌지 않습니다.

## 2. 좌표와 단위

- `coordinateSpace = "cropbox-top-left-unrotated-pt"`: PDF point 단위, 보이는 CropBox(∩MediaBox)의 왼쪽 위가 원점, x는 오른쪽, y는 아래로 커집니다. `/Rotate` 적용 전 좌표이고 `/UserUnit`을 곱한 실제 point입니다. 이 문서에서는 canonical 좌표라고 부릅니다.
- PDF 사용자 공간 → canonical: `x' = (x - CropLeft)·U`, `y' = (CropTop - y)·U` (`PdfPageBox.PdfUserToCanonical`).
- 페이지 회전은 `PageFrame.CanonicalToDisplay`(시계 방향 0/90/180/270)로, 화면은 `ViewTransform`(회전 → `zoom·96/72` 배율 → 페이지 원점 이동 → DPI 배율)으로 바꿉니다.
- 주석의 `transform`은 `[a b c d e f]`(PDF/WPF 행벡터 규약)이고 지역 좌표 → canonical입니다. **transform은 항상 강체(회전+이동)입니다.** 크기를 바꾸면 확정할 때 기하·선 굵기·글자 크기에 반영하고 transform에는 남기지 않습니다. 배율이 두 번 적용되는 걸 막기 위해서입니다. PDF 글자에 붙는 표시(`pdfTextHighlight`, `textMarkup`)는 항상 항등입니다.
- `textIndexUnit = "utf16-code-unit/grapheme-boundary"`: 텍스트 범위는 UTF-16 offset/length이고 경계는 확장 grapheme cluster 경계여야 합니다. 서로게이트 쌍이나 결합 문자를 가르는 범위는 거부합니다.
- 색은 `#RRGGBB`(sRGB), 불투명도는 0~1입니다. 화면의 투명도 T%는 `1 - T/100`입니다.
- double은 System.Text.Json의 왕복 표현으로 기록해서 같은 모델은 비트 단위로 같은 값으로 돌아옵니다.

## 3. PDF 안의 위치

| 위치 | 내용 |
|---|---|
| 카탈로그 `/PdfNotes` | Header JSON 스트림 |
| 카탈로그 `/PdfNotesResources` | `/R<자원 UUID, N 형식>` → 이미지 원본 바이트 스트림 |
| 카탈로그 `/PdfNotesOriginal`, `/PdfNotesOriginalHash` | 원본 PDF 보관본과 그 SHA-256 ([7절](#7-원본-보관과-페이지-배치)) |
| 페이지 `/PdfNotesBackgroundHash` | 배경 지문 ([7절](#7-원본-보관과-페이지-배치)) |
| 페이지 `/Annots`의 앱 주석 | `/NM (PdfNotes:<주석 UUID>)`, `/F 4`(인쇄), `/Rect` = 그린 범위(PDF 사용자 공간, 여유 1pt), `/AP /N` = 외형 Form XObject, `/Subtype`은 [6절](#6-외형-그리기-규칙) |
| 앱 주석 `/PdfNotesData` | Envelope JSON 스트림 |
| 앱 주석 `/PdfNotesAP` | 외형 지문 ([6절](#6-외형-그리기-규칙)) |
| 앱 주석 `/PdfNotesTextLayer`, `/PdfNotesTextHash` | 텍스트 상자의 검색용 본문 연결과 지문 ([6절](#텍스트-상자)) |
| 외형 Form 표시 | `/PdfNotesText`, `/PdfNotesLine`, `/PdfNotesShape` (`true`) |

Flatten은 `/PdfNotes`로 시작하는 키를 모두 지웁니다. 그래서 새 키도 반드시 `/PdfNotes`로 시작해야 합니다.

## 4. Header

```json
{
  "app": "PdfNotes",
  "schemaVersion": 7,
  "coordinateSpace": "cropbox-top-left-unrotated-pt",
  "textIndexUnit": "utf16-code-unit/grapheme-boundary",
  "documentId": "…",
  "pages": [
    {
      "id": "…", "widthPt": 1100, "heightPt": 540, "rotation": 0,
      "background": { "kind": "blank", "spacingMm": 5, "lineColor": "#D9D9D9" },
      "source": { "kind": "pdf", "index": 0 },
      "content": { "widthPt": 720, "heightPt": 540, "transform": [1, 0, 0, 1, 0, 0] },
      "annotationIds": ["…", "…"]
    }
  ],
  "resources": [ { "id": "…", "mediaType": "image/png", "sha256": "…", "length": 12345 } ],
  "tags": [
    { "annotationId": "…", "author": "ai", "batchId": "run-01", "sourceKey": "lecture/p3/text", "groupKey": "lecture/p3" }
  ]
}
```

(값은 예시입니다. 정확한 필드와 허용 값은 DTO가 기준입니다.)

- `widthPt`, `heightPt`, `rotation`은 지금 종이의 경계입니다.
- `source`는 `{ "kind": "pdf", "index": n }` 또는 `{ "kind": "generated", "index": null }`입니다. PDF 색인은 [원본 보관본](#7-원본-보관과-페이지-배치)의 페이지 번호이고 지금 문서 순서와 다를 수 있습니다. 새로 만든 페이지는 모눈·줄노트를 다시 그릴 수 있게 종류를 남깁니다.
- `content`(선택)는 처음 원본 CropBox의 크기와, 원본 canonical → 지금 canonical 누적 배치입니다. 양의 균일 배율과 이동만 허용합니다. 없으면 원본과 지금 좌표가 같습니다.
- `tags`(v7)는 주석 레코드가 아니라 **문서 단위 표(주석 ID → 태그)**입니다. `deot` CLI가 만든 주석을 알아보는 데 씁니다.
  - `author`는 필수, 1~64자(CLI는 `"ai"`). `batchId`, `sourceKey`, `groupKey`는 선택이고 있으면 1~512자. 제어 문자는 안 됩니다.
  - `sourceKey`가 겹치지 않는지는 형식이 아니라 CLI가 검사합니다. 파일에 중복이 있으면 CLI는 아무거나 고르지 않고 오류를 냅니다.
  - 앱 화면에는 태그가 보이지 않습니다.

## 5. 주석 종류

| type | 모델 | 주요 필드 |
|---|---|---|
| `textBox` | `TextBoxAnnotation` | text, runs[](start, length, fontFamily, sizePt, color, bold, italic, underline, strikethrough), alignment, widthPt, boxStyle, highlights[], layoutScale |
| `ink` | `InkStroke` | points(x, y, … 평탄 배열), widthPt, color, opacity, outline? |
| `freeHighlight` | `FreeHighlightStroke` | `ink`와 같음 |
| `line` | `LineAnnotation` | lineKind(straight/quadratic/cubic), start, end, controls(수 0·2·4개), widthPt, dash, startArrow, endArrow, color, opacity |
| `pdfTextHighlight` | `PdfTextHighlight` | quads(8개씩: UL, UR, LL, LR), sourceTextHint?, heightMode, manualHeightPt?, color, opacity |
| `textMarkup` | `PdfTextMarkup` | quads, sourceTextHint?, markupKind(underline/strikeOut), widthPt, color, opacity |
| `image` | `ImageAnnotation` | resourceId, localBounds[x, y, w, h] |
| `shape` | `ShapeAnnotation` | shapeKind, box, points, fillColor, fillOpacity, borderEnabled, borderColor, borderWidthPt, borderOpacity, borderDash |
| `note` | `StickyNote` | text, color |

모든 주석에 공통으로 `id`, `locked`, `transform`이 있습니다. 무결성 한계(`ModelLimits`)는 화면 입력 범위(`InputLimits`)보다 넓습니다. 크기를 키우면 화면 최대값보다 굵은 선이나 큰 글자가 생길 수 있어서입니다.

### 텍스트 상자 (`textBox`)

- runs는 전체 텍스트를 빈틈없이 순서대로 덮습니다. highlights는 정렬되어 있고 겹치지 않습니다.
- 높이는 레이아웃 결과라서 저장하지 않습니다.
- `layoutScale`(기본 1)은 고정 4pt 여백, 1pt 테두리, 레이아웃 최소 길이의 배율입니다. 너비·글자 크기·수동 형광 높이는 이미 지금 point로 저장되어 있고, 그릴 때는 지금 크기를 이 값으로 나눈 원래 단위에서 줄바꿈한 뒤 전체를 배율로 그립니다. 편집기도 같은 방식을 씁니다.
- 공백만 있는 상자는 저장하지 않고, 읽을 때도 버립니다.

### 펜·자유 형광 (`ink`, `freeHighlight`)와 지우개 조각

영역 지우개로 획 일부만 지우면 남은 모양을 중심선과 굵기로 나타낼 수 없습니다. 그래서 남은 칠 영역을 그대로 저장합니다.

- `outline`: `[[x0, y0, x1, y1, …], …]`, 지역 좌표 고리 목록, even-odd 채움, 고리마다 3점 이상.
- `outline`이 있으면 `points`는 빈 배열이고 `widthPt`는 기록일 뿐 모양에 영향이 없습니다.
- 조각 하나는 바깥 고리 하나와 그 안의 구멍들로 된 연결된 영역입니다. 한 획이 여러 조각으로 갈라지면 각각 따로 주석이 됩니다. 가장 큰 조각이 원래 ID를 갖고 나머지는 새 ID로, 원래 자리에 이어서 쌓입니다.
- 칠 영역은 중심선을 굵기/2만큼 둥근 끝·둥근 이음으로 키운 다각형입니다(Clipper2 `InflatePaths`, 점 하나는 원). 정밀도는 0.001pt 격자, 원호 평탄화 0.01pt, 0.01pt² 미만 부스러기는 버립니다(`StrokeRegion`).

### 선 (`line`)

- 제어 다각형은 `[start, controls…, end]`이고 조절점은 선이 지나가는 점이 아닙니다(`Core/Geometry/LineGeometry`).
- 화살표: 길이 = 굵기×5, 반폭 = 굵기×2.25, 방향 = 끝점 접선. 끝 접선 길이가 0이면 다음 조절점, 그것도 없으면 반대쪽 끝, 모두 겹치면 +x 방향입니다. 몸통은 화살표 길이×0.6 지점에서 잘라 둥근 끝이 머리 밖으로 나오지 않게 합니다.
- 대시 배열(굵기 배수): 파선 `[3 2.5]`, 점선 `[0 2]`, 일점쇄선 `[3 2.5 0 2.5]`. 모든 스타일이 둥근 끝입니다.

### 글자 표시 (`pdfTextHighlight`, `textMarkup`)

- PDF 글자 사변형(canonical, 줄마다 하나)에 붙고 transform은 항등입니다. 혼자 옮기거나 돌리거나 키울 수 없고, 페이지 변환 때만 사변형과 굵기가 같이 바뀝니다.
- `textMarkup`의 띠는 사변형 위쪽 방향으로 `widthPt`(0.5~4) 두께입니다. 밑줄은 띠 아래쪽이 사변형 아래 변에, 취소선은 띠 가운데가 사변형 높이 가운데에 옵니다(`TextSelection.MarkupBand`).

### 이미지 (`image`)

- 자원은 최대 256MiB, 디코딩한 이미지는 최대 64,000,000픽셀입니다. 형식이 맞아도 실제 디코딩에 실패하면 편집 대상으로 복원하지 않고 외형만 남깁니다.
- 복제한 주석과 자원은 각각 새 UUID를 받습니다. 같은 원본 바이트를 내부에서 공유하는 건 괜찮습니다. 아무도 안 쓰는 자원은 저장하지 않습니다.

### 도형 (`shape`)

```json
{
  "type": "shape", "id": "…", "locked": false, "transform": [1, 0, 0, 1, 0, 0],
  "shapeKind": "rectangle | ellipse | triangle | polygon | polyline",
  "box": [x, y, width, height],
  "points": [x0, y0, x1, y1],
  "fillColor": "#9EC5F0", "fillOpacity": 0.7,
  "borderEnabled": true, "borderColor": "#1E4F91", "borderWidthPt": 2, "borderOpacity": 1,
  "borderDash": "solid | dash | dot | dashDot"
}
```

- `rectangle`, `ellipse`, `triangle`은 `box`를 쓰고 `points`는 비웁니다. 삼각형 꼭짓점은 상자의 위 가운데, 오른쪽 아래, 왼쪽 아래이고 타원은 상자에 내접합니다.
- `polygon`(닫힘, 3점 이상)과 `polyline`(열림, 2점 이상, 다른 앱 PolyLine을 바꾼 것)은 `points`를 쓰고 `box`는 비웁니다.
- 크기를 바꾸면 box·points·`borderWidthPt`에 한 번 반영합니다. 도형 하나를 자유 비율로 늘릴 때는 box·points만 바뀌고 테두리 굵기는 그대로입니다.
- 채우기 투명도 0은 채우기 없음입니다. polyline은 채우지 않습니다. 채우기도 테두리도 안 보이는 도형은 검증에서 거절합니다.

### 메모 (`note`)

```json
{ "type": "note", "id": "…", "locked": false, "transform": [1, 0, 0, 1, x, y], "text": "메모 내용", "color": "#FFD54F" }
```

- 아이콘은 지역 원점의 20pt 정사각형이고 크기가 바뀌지 않습니다. transform은 이동만 허용합니다.
- `text`는 일반 텍스트(줄바꿈 `\n`)입니다. 비어 있으면 저장하지 않고 읽을 때도 버립니다.

## 6. 외형 그리기 규칙

### 공통

- `/Subtype`: 밑줄 `/Underline`, 취소선 `/StrikeOut`, 메모 `/Text`, 나머지는 모두 `/Stamp`. 리더는 이 네 가지를 앱 주석 후보로 봅니다(`AnnotationAppearance.IsAppSubtype`).
- 외형 Form은 `cm` 하나(지역 → canonical → PDF 사용자 공간, CropBox·UserUnit 반영)로 canonical 좌표를 그대로 그립니다. NoRotate/NoZoom 플래그가 없어서 페이지 회전과 확대를 따라갑니다.
- 숫자는 소수 6자리까지 직접 서식화합니다. PDFsharp `PdfReal`(7자리)을 쓰는 BBox·ExtGState 값은 반드시 `AnnotationAppearance.Real`로 먼저 6자리로 반올림합니다. 7자리→6자리로 두 번 반올림하면 마지막 자리가 달라져서 자기가 쓴 외형을 '바뀐 외형'으로 잘못 판단하게 됩니다.
- **외형 지문** `/PdfNotesAP`: SHA-256(외형 스트림 내용 + BBox + ExtGState의 CA/ca/BM + Matrix 유무). 이미지는 래스터·soft mask 스트림과 Width/Height/BitsPerComponent/ColorSpace/Filter/Decode/DecodeParms/Mask/Interpolate까지, 선은 내부 `/L` 그룹의 내용·BBox·Group /S까지 넣습니다. 다른 프로그램이 외형을 고쳤으면 지문이 달라져서 그 주석은 보기 전용으로 남습니다.
- 지문은 압축 상태나 간접 객체 번호에 영향받지 않습니다. 사전 키를 정렬하고 수치를 정규화하고 압축을 푼 스트림으로 계산하며, 재귀 깊이·객체 수·스트림 크기에 한도를 둡니다.

### 획

한 경로를 `1 J 1 j`(둥근 끝·이음)로 한 번 칠합니다. 점 하나는 굵기 지름의 원입니다. 불투명도는 ExtGState `/CA /ca`(Normal)입니다. 한 번에 칠하니까 형광펜이 자기 자신과 겹친 곳은 진해지지 않고, 서로 다른 획끼리는 진해집니다. 화면도 같은 규칙(획마다 불투명도 레이어 하나)으로 그립니다. 지우개 조각은 `/GS0` 아래 고리들을 `f*` 한 번으로 칠합니다. 지운 자리를 흰색으로 덮지 않기 때문에 배경(모눈 포함)이 그대로 보입니다.

### 선

외형 Form은 `/GS0 gs /L Do` 한 줄입니다. `/L`은 `/Group <</S /Transparency>>` Form으로 몸통과 머리를 불투명하게 그리고, 바깥 `/CA /ca`로 그룹을 한 번 합성합니다. 머리 아래 겹친 부분이 진해지지 않습니다. 2차 곡선은 차수를 올린 3차 `c`로 기록합니다.

### 도형

`/GF`(채우기 `ca`)와 `/GB`(테두리 `CA`) ExtGState를 따로 두고, 채우기는 `f*`, 테두리는 `S`로 한 번씩 합성합니다. 타원은 3차 베지어 4개입니다.

### 텍스트 상자

- 화면 편집기, 화면 표시, PDF 외형이 같은 FlowDocument 배치를 씁니다. 여백은 항상 4pt, 테두리 1pt입니다.
- 글꼴 파일을 넣지 않고, shaping된 글자 윤곽을 0.02pt 허용오차로 평탄화한 경로로 그립니다.
- **검색용 본문**: 다른 뷰어에서 글자를 검색·선택·복사할 수 있도록, 같은 위치에 보이지 않는 표준 텍스트(Tr=3)를 함께 넣습니다.
  - 같은 줄 글자를 Tj 하나로 묶고 실제 폭과 위치를 Type3 글꼴에 적습니다. CharProcs는 폭과 BBox만 선언하고 아무것도 그리지 않습니다. 코드 공간 1바이트, 글꼴당 최대 255자, ToUnicode bfchar 블록 최대 100개, ToUnicode는 UTF-16BE입니다.
  - 상자마다 Form XObject 하나와 그걸 부르는 페이지 Contents 스트림 하나를 가집니다. 주석의 `/PdfNotesTextLayer`가 그 Contents를 가리키고, `/PdfNotesTextHash`가 호출 스트림·Form·BBox·글꼴·폭·CharProcs·ToUnicode의 지문입니다. 스트림에는 `/PdfNotesTextId`, `/PdfNotesTextName`, `/PdfNotesTextForm`이 붙습니다.
  - 복원할 때 이 연결과 지문을 모두 확인한 다음에만 스트림과 XObject 등록을 함께 지웁니다. 원본 PDF의 스트림은 지우지 않습니다.
  - 회전한 여러 줄을 통째로 복사할 때 순서는 뷰어의 읽기 순서 분석에 따라 다를 수 있습니다.
- 없는 글꼴을 다른 글꼴로 바꿔서 레이아웃을 몰래 바꾸지 않습니다. 저장된 글꼴이 없으면 그 상자는 복원하지 않고 외형과 본문을 보기 전용으로 남기고 알립니다.

### 이미지

PDFsharp `XImage`로 만든 Image XObject와 PNG soft mask를 Form 리소스로 참조합니다. 원본 바이트는 자원으로 따로 저장하고, 화면에서는 원본에서 최대 2048px 표시용 이미지를 만듭니다.

### 밑줄·취소선·메모

다른 뷰어를 위해 표준 값도 씁니다(`EditablePdfWriter.StandardFields`).

- 밑줄·취소선: `/QuadPoints`(PDF 사용자 공간, UL·UR·LL·LR), `/C`, `/CA`, `/Contents`(글자).
- 메모: `/Contents`(UTF-16), `/C`, `/Name /Comment`, `/Open false`, `/Popup`(닫힌 팝업, `/Parent`로 메모를 가리킴). 다시 읽을 때 앱 메모를 배경에서 빼면서 그 팝업도 같이 빼므로, 여러 번 저장해도 팝업은 하나입니다.

## 7. 원본 보관과 페이지 배치

페이지 축소, 크기 변경, 다른 PDF에서 가져오기를 해도 원본을 비트맵으로 바꾸지 않고, 나중에 되돌릴 수 있게 하는 장치입니다.

- **원본 보관** `/PdfNotesOriginal`: 앱이 복원한 주석·본문과 이전 앱 메타데이터를 뺀 최초 원본 PDF 한 벌을 압축 스트림으로 보관합니다. `/PdfNotesOriginalHash`는 디코딩한 바이트의 SHA-256, 상한은 512MiB입니다. 저장할 때마다 이 보관본에서 다시 쓰므로 변환이 겹겹이 쌓이지 않습니다.
- 다른 PDF에서 가져온 페이지는 보관본 뒤에 이어 붙이고, 그 페이지들의 `source.index`는 기존 원본 페이지 수부터 이어집니다. 그래서 저장 파일은 언제나 원본 하나짜리 문서입니다.
- 원본 페이지 내용 앞뒤에 `q/Q`로 균일 CTM과 **처음 CropBox 영역의 고정 클립**을 붙입니다. MediaBox/CropBox는 지금 종이로 맞추고 UserUnit은 CTM에 흡수하고 Rotate는 그대로 둡니다. 종이를 줄였다가 저장하고 다시 키우면 내용이 돌아옵니다.
- 다른 앱 주석의 표준 AP는 원래 CropBox로 자르는 Form으로 감싸서 Rect·좌표·굵기와 함께 옮깁니다. 원래 경계 밖에 있던 주석은 지금 화면에서는 빠지지만 보관본에는 남습니다. 경계에 걸친 다른 앱 주석에 `/AP /N`이 없으면 안전하게 자를 수 없어서 그 변환은 거절합니다(적용 전에 대상 전체를 확인하므로 일부 페이지만 바뀌는 일은 없습니다).
- **배경 지문** `/PdfNotesBackgroundHash`: 앱 외형과 검색 본문을 뺀 뒤의 contents·resources·다른 앱 주석·페이지 기하의 지문입니다. 다른 프로그램이 페이지를 고쳐서 지문이 달라지면 지금 PDF를 새 원본으로 삼아 그 변경을 살립니다(이때 이전 보관본의 바깥 데이터 복원은 보장하지 않습니다).
- 페이지 변환(`PageTransforms`)은 원본 배치를 누적하고, 모든 주석(잠긴 것 포함)의 기하·길이·`outline`·글자 사변형을 한 명령으로 함께 바꿉니다. 새 주석에는 과거 배율을 적용하지 않습니다.
- 보관 때문에 처음 저장한 파일과 메모리가 커질 수 있고, 지운 페이지도 보관본에는 남을 수 있습니다.

## 8. 읽기 규칙

후보는 `/NM` 접두사나 `/PdfNotesData`가 있는 주석뿐입니다. 앱 서브타입, `/NM`의 UUID, 외형 존재, 외형 지문을 통과한 것만 스키마 리더에 넘기고, 결과 중 `/NM` UUID와 주석 ID가 다르거나 실제 페이지가 Header와 다른 것은 뺍니다.

| 상황 | 결과 |
|---|---|
| Header 없음 | 편집 데이터 없음. 앱 주석 외형은 모두 보기 전용 |
| `app` 불일치, JSON 손상, 모르는 필드, 단위 선언 불일치, ID 중복·빈 값, 잘못된 태그 표 | Header 전체 `Invalid`, 모두 보기 전용 |
| 모르는 `schemaVersion` | `Unsupported`, 모두 보기 전용 |
| Header와 Envelope 버전이 다름 | 거절 |
| 실제 페이지 수·크기(±0.001pt)·회전이 Header와 다름, 보관본 손상, 원본 색인·크기 불일치 | `Invalid`. 다른 앱이 페이지를 바꾼 것으로 보고 추측해서 복원하지 않음 |
| Envelope 손상, 모르는 type·필드, 다른 문서 ID, Header에 없는 ID, 다른 페이지, 중복, 값 검증 실패, 그 버전에 없는 종류·필드 | 그 주석만 `Rejected`, 외형은 배경에 남음 |
| 이미지 자원 누락, 길이·SHA-256·형식 서명 불일치 | 그 자원을 쓰는 이미지만 `Rejected`, 경고 |
| 저장된 글꼴이 없음 | 그 텍스트 상자는 보기 전용, 경고 |
| 공백만 있는 텍스트 상자·메모 | `DiscardedEmpty`, 복원도 표시도 안 함 |
| Header에는 있는데 Envelope가 없는 ID | 경고(복원 못 한 개수) |

- `Restored`와 `DiscardedEmpty`인 주석만 배경 렌더용 PDF에서 지웁니다. 나머지는 외형을 배경에 남기고 `ReadOnlyCount`로 알립니다.
- 태그는 복원된 주석 것만 붙이고, 거절되거나 버려진 주석의 태그는 버립니다.
- 보기 전용으로 남은 외형은 다시 저장해도 보존되지만, 새 Header의 편집 주석으로 자동 등록되지는 않습니다.

## 9. 쓰기와 검증

- 원본 PDF(열 때 읽은 바이트)를 수정 모드로 열고, 복원했던 앱 주석을 뺀 뒤, 모델 순서로 페이지를 놓고, 회전을 `/Rotate`로 적고, 앱 주석과 Header를 새로 씁니다. 매번 전체를 다시 쓰기 때문에 여러 번 저장해도 주석이나 파일 크기가 불어나지 않습니다.
- 유효하지 않은 모델과 공백 텍스트 상자는 쓰지 않습니다. 저장 직전에 잠긴 빈 상자도 정리합니다.
- **새 파일은 필요한 가장 낮은 버전이 아니라 지금 버전으로 씁니다.** 예외는 태그입니다. 태그가 하나라도 있을 때만 v7로 쓰고, 없으면 v6로 씁니다. 그래서 CLI를 쓰지 않은 파일은 이전 앱에서도 편집할 수 있습니다. 지운 주석의 태그는 쓰지 않습니다.
- 앱의 주석 클립보드(`AnnotationTransfer`)는 v6 형식입니다. 복사본은 새 ID라 태그가 없기 때문입니다.
- 검증: 같은 폴더의 임시 파일(`~<이름>.<guid>.tmp`)을 다시 읽어서 PDFium으로 열리는지, Header가 유효한지, 모든 앱 주석이 복원되는지, 다시 직렬화한 결과가 저장한 스냅샷과 바이트 단위로 같은지 확인한 뒤에만 `File.Replace`로 바꿉니다. 원본에서 보존한 보기 전용 주석은 검증 실패 사유가 아닙니다.

### 앱 편집과 태그

| 앱에서 한 일 | 태그 |
|---|---|
| 이동·회전·크기·서식 변경, 페이지 변환·회전·이동 | ID가 그대로라 유지 |
| 삭제 | 저장에서 빠짐(실행 취소하면 돌아옴) |
| 복사·붙여넣기, 페이지 복제, 지우개 조각 | 새 ID라 태그 없음(사람 필기로 취급) |

태그를 바꾸는 편집은 `SetTagOperation` 하나뿐이고 CLI만 씁니다.

## 10. Flatten

필기 합친 PDF는 지금 페이지 내용에 앱 외형과 표준 외형이 있는 다른 앱 주석(투명도 유지)을 합치고 `/PdfNotes*` 데이터(Header, 자원, 원본 보관, 지문, Envelope 연결)를 모두 지웁니다. 보관 원본을 출력 본문으로 쓰지 않습니다.

- 텍스트 상자의 검색용 본문은 그대로 남아서 계속 검색·선택됩니다.
- 지우개 조각 외형은 추가 합성 없이 그대로 합칩니다.
- 메모는 합치지 않고 표준 메모로 남깁니다. `/PdfNotes*` 키만 지우고 `/NM`을 새 값으로 바꿉니다.
- 양식 필드, NoZoom/NoRotate, `/OC`, 외형 없는 다른 앱 주석은 합치지 않고 남깁니다.

## 11. 다른 앱 주석 바꾸기

'다른 프로그램 주석' 명령으로 다른 앱이 만든 주석을 덧PDF 주석으로 바꿀 수 있습니다. 바꾼 결과는 보통 주석과 똑같이 저장되고 원래 주석은 저장본에서 빠집니다. 저장하지 않으면 파일은 그대로입니다.

| 다른 앱 주석 | 바뀌는 종류 |
|---|---|
| Ink | `ink` |
| Highlight | `pdfTextHighlight` |
| Line | `line` |
| FreeText | `textBox` (일반 텍스트, 기본 글꼴. 위치가 조금 다를 수 있음) |
| Square, Circle | `shape` rectangle/ellipse (보이는 페이지 기준 축 정렬, `/Rect`에서 `/RD`와 테두리 절반만큼 안쪽) |
| Polygon, PolyLine | `shape` polygon/polyline |
| Underline, StrikeOut | `textMarkup` (굵기는 `/BS /W` 또는 1pt, 0.5~4로 제한) |
| 글자가 있는 Text 메모 | `note` (아이콘 왼쪽 위 = `/Rect` 왼쪽 위) |

Flatten한 덧PDF 메모도 이 방법으로 다시 편집할 수 있습니다.

## 12. 버전 정책

1. **필드를 더하거나 빼거나 의미를 바꾸면 `schemaVersion`을 올립니다.** 리더는 모르는 필드를 거부합니다(`UnmappedMemberHandling.Disallow`). 옛 앱이 새 데이터를 일부만 읽고 다시 저장해서 정보를 조용히 잃는 걸 막기 위해서입니다.
2. 새 리더는 이전 버전을 각 버전의 리더로 넘겨 같은 모델로 읽습니다(`SchemaV7.Read` → `SchemaV6.Read` → …). 쓸 때는 지금 버전으로 씁니다(태그 예외는 9절).
3. 이전 버전의 `Serialize`는 그 버전에 없는 내용(조각, 도형, 새 종류 등)이 있는 모델을 예외로 거절합니다.
4. 모르는 버전은 추측하지 않고 외형을 보기 전용으로 남깁니다. 그래서 옛 앱으로 새 파일을 열어도 필기가 사라지거나 엉뚱하게 바뀌지 않고, 보이기만 합니다.
5. 새 주석 종류는 외형 생성, 화면 그리기, 선택 판정을 함께 등록합니다. 외형 처리기가 없는 종류는 저장하지도 복원하지도 않습니다.

## 버전 기록

| 버전 | 추가된 것 | 옛 앱에서 열면 |
|---|---|---|
| 1 | Header/Envelope, 좌표 규약, `textBox`, `ink`, `freeHighlight`, `line`, `pdfTextHighlight`, `image`, 이미지 자원 | (최초) |
| 2 | 텍스트 상자 검색용 본문(Type3, Tr=3). JSON 필드 변화 없음. 옛 앱이 외형만 지우고 검색 글자를 남기는 걸 막으려고 버전을 올림 | v1 앱: 보기 전용 |
| 3 | `ink`/`freeHighlight`의 `outline`(영역 지우개 조각) | v1~2 앱: 보기 전용 |
| 4 | 페이지 `source`·`content`, 텍스트 `layoutScale`, 원본 보관(`/PdfNotesOriginal`), 배경 지문 | v1~3 앱: 보기 전용 |
| 5 | `shape` | v1~4 앱: 보기 전용 |
| 6 | `textMarkup`(밑줄·취소선), `note`(메모), 앱 주석 서브타입 `/Underline`·`/StrikeOut`·`/Text` | v1~5 앱: 보기 전용 |
| 7 | Header `tags`(CLI 태그 표). 태그가 있을 때만 씀 | v1~6 앱: 보기 전용 |

지금 앱은 v1~v7을 모두 읽고, 다음에 저장할 때 v6(태그 없음) 또는 v7로 바꿔 씁니다.
