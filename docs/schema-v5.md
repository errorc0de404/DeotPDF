# 편집 메타데이터 스키마 v5 — D11 도형

코드: `Core/Models/Annotations.cs`(`ShapeAnnotation`, `ShapeStyle`), `Core/Geometry/ShapeGeometry.cs`, `Core/Serialization/SchemaV5.cs`, `Pdf/Writing/AnnotationAppearance.cs`(`CreateShape`). v4의 모든 필드·의미(페이지 source/content, 텍스트 layoutScale, 원본 보관, 배경 지문)를 그대로 유지한다. 새 일반 저장과 주석 클립보드는 Header/Envelope 모두 schemaVersion=5다.

## 새 주석 종류 `shape`

```json
{
  "type": "shape", "id": "…", "locked": false, "transform": [a, b, c, d, e, f],
  "shapeKind": "rectangle | ellipse | triangle | polygon | polyline",
  "box": [x, y, width, height],
  "points": [x0, y0, x1, y1, …],
  "fillColor": "#RRGGBB", "fillOpacity": 0.7,
  "borderEnabled": true, "borderColor": "#RRGGBB", "borderWidthPt": 2, "borderOpacity": 1,
  "borderDash": "solid | dash | dot | dashDot"
}
```

- `rectangle`·`ellipse`·`triangle`은 지역 좌표 `box`(양의 너비·높이)를 채우고 `points`는 빈 배열이다. 삼각형 꼭짓점은 상자 위 가운데, 오른쪽 아래, 왼쪽 아래다. 타원은 상자에 내접한다.
- `polygon`(닫힘, 꼭짓점 3개 이상)과 `polyline`(열림, 2개 이상, 다른 앱 PolyLine 변환)은 `points`를 쓰고 `box`는 빈 배열이다(모델은 `Rect2` 기본값).
- transform은 다른 종류처럼 강체(회전+이동)다. 배율은 저장 전에 box/points/`borderWidthPt`에 한 번 반영한다. 단일 도형의 자유 비율 크기 조절은 box/points만 늘리고 테두리 굵기는 그대로 둔다.
- 채우기 투명도 0은 채우기 없음이다. 꺾은선은 채우지 않는다. 채우기도 테두리도 보이지 않는 도형(테두리 끔 + 채우기 0, 또는 테두리 끈 꺾은선)은 검증에서 거절한다.
- 대시 패턴은 선(D07)과 같은 `LineGeometry.DashPattern` × 테두리 굵기, 둥근 이음·끝이다.

## PDF 외형

- 앱 주석 공통 규칙(`/Stamp`, `/PdfNotesData` Envelope, `/PdfNotesAP` 지문)을 따른다.
- 외형 Form은 `/PdfNotesShape true`이고 `/GF`(채우기 `ca`)·`/GB`(테두리 `CA`) ExtGState를 따로 둔다. 채우기는 `f*`(even-odd), 테두리는 `S`로 각각 한 번 합성한다. 화면 painter도 같은 순서·투명도·윤곽을 쓴다. 타원은 4개의 3차 Bézier다.
- 지문은 텍스트 외형처럼 모든 ExtGState의 `ca`/`CA`/`BM`을 포함한다. 다른 앱이 투명도를 바꾸면 그 도형은 보기 전용으로 남는다.

## 호환성

- `SchemaV5.Read`는 v1~v4를 각 기존 리더로 넘긴다(기존 파일은 그대로 열린다).
- v1~v4 리더는 v5 Header를 Unsupported로 보고 모든 외형을 보기 전용으로 보존한다. v4 이하 Header 아래의 `shape` Envelope은 거절(보기 전용)이다.
- v1~v4 `Serialize`는 도형이 있는 모델을 거절한다(`Shapes need schema v5.`).
- `EditablePdfReader`는 v4와 v5 모두 원본 보관(`/PdfNotesOriginal`)·배치를 사용한다.

## 다른 앱 주석 변환과의 관계 (명세 14.6)

다른 앱의 Square·Circle은 `rectangle`·`ellipse`(보이는 페이지 기준 축 정렬, /Rect에서 /RD와 테두리 절반 안쪽), Polygon·PolyLine은 `polygon`·`polyline`(transform 단위)로 변환한다. 변환 결과는 일반 v5 도형으로 저장되며 원래 주석은 저장본에서 빠진다.

## 페이지 불러오기 (D13g, 2026-10-14) — 형식 변경 없음

다른 PDF에서 불러온 페이지(명세 11.9)는 새 필드 없이 v5로 저장한다. 저장기는 불러온 원본 페이지를 보관 원본(`/PdfNotesOriginal`) 뒤에 이어 붙이고, 그 페이지들의 `source.index`는 기존 원본 페이지 수부터 이어진다. 그래서 저장 파일은 원본 하나짜리 보통 v5 문서이고 이전 버전 앱도 같은 규칙으로 읽는다. 불러온 앱 주석은 새 ID를 받으므로 Envelope 의미도 그대로다.
