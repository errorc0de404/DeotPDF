# 편집 메타데이터 스키마 v6 — D13i 밑줄·취소선, 스티키 메모

코드: `Core/Models/Annotations.cs`(`PdfTextMarkup`, `TextMarkupKind`, `StickyNote`), `Core/Geometry/TextSelection.cs`(`MarkupBand`), `Core/Serialization/SchemaV6.cs`·`SchemaV1Dtos.cs`(`TextMarkupDto`, `NoteDto`), `Pdf/Writing/AnnotationAppearance.cs`(`Subtype`, `CreateFill`, `CreateNote`), `Pdf/Writing/EditablePdfWriter.cs`(`StandardFields`). v5의 모든 필드·의미를 그대로 유지한다. 새 일반 저장과 주석 클립보드는 Header/Envelope 모두 schemaVersion=6이다.

## 새 주석 종류 `textMarkup` (명세 24.8)

```json
{
  "type": "textMarkup", "id": "…", "locked": false, "transform": [1, 0, 0, 1, 0, 0],
  "quads": [ulx, uly, urx, ury, llx, lly, lrx, lry, …],
  "sourceTextHint": "English",
  "markupKind": "underline | strikeOut",
  "widthPt": 1.5, "color": "#RRGGBB", "opacity": 1
}
```

- `pdfTextHighlight`와 같이 PDF 글자 사변형(canonical point, 줄마다 하나)에 붙고 transform은 항등이다. 혼자 이동·회전·크기 변경하지 않는다(페이지 변환은 사변형과 굵기에 함께 반영).
- 칠하는 띠: 사변형의 위쪽 방향으로 `widthPt` 두께. 밑줄은 띠의 아래쪽이 사변형 아래 변, 취소선은 띠의 가운데가 사변형 높이 가운데다(`TextSelection.MarkupBand`).
- `widthPt` 0.5~4, `opacity` 0~1, 사변형 개수 한도는 하이라이트와 같다.

## 새 주석 종류 `note` (명세 24.9)

```json
{ "type": "note", "id": "…", "locked": false, "transform": [1, 0, 0, 1, x, y], "text": "메모 내용", "color": "#RRGGBB" }
```

- 아이콘은 지역 원점의 20pt 정사각형(`StickyNote.IconPt`), transform은 **이동만**(검증에서 이동 외 성분은 거절). 아이콘 크기는 바뀌지 않는다.
- `text`는 일반 텍스트(줄바꿈 `\n`), 길이 한도는 텍스트 상자와 같다. 빈(공백뿐인) 메모는 저장하지 않고, 읽을 때도 빈 텍스트 상자처럼 버린다.

## PDF 저장

- 앱 주석 공통 규칙(`/NM` 접두사, `/PdfNotesData` Envelope, `/PdfNotesAP` 외형 지문)은 같고 `/Subtype`만 종류에 따라 다르다: 밑줄 `/Underline`, 취소선 `/StrikeOut`, 메모 `/Text`, 그 밖은 지금처럼 `/Stamp`. 리더는 이 네 가지를 앱 주석으로 인정한다(`AnnotationAppearance.IsAppSubtype`).
- 다른 뷰어용 표준 값: 밑줄·취소선은 `/QuadPoints`(PDF 사용자 공간, 사변형마다 UL·UR·LL·LR), `/C`, `/CA`, `/Contents`(글자). 메모는 `/Contents`(UTF-16), `/C`, `/Name /Comment`, `/Open false`, `/Popup`(닫힌 팝업, `/Parent`로 메모를 가리킴). 외형(`/AP /N`)은 앱이 그린 것과 같다.
- 메모의 팝업은 앱 표시가 없는 일반 팝업이다. 다시 읽을 때 앱 메모를 배경에서 뺄 때 그 팝업도 함께 빼므로 저장을 반복해도 하나다.
- Flatten: 밑줄·취소선은 다른 앱 주석처럼 페이지 내용에 합친다. 메모는 합치지 않고 표준 메모로 남기며 `/PdfNotes*` 키만 지우고 `/NM`을 새 값으로 바꾼다(내용이 사라지지 않게).

## 호환성

- `SchemaV6.Read`는 v1~v5를 각 기존 리더로 넘긴다(`SchemaV5.Read` → …).
- v1~v5 리더는 v6 Header를 Unsupported로 보고 모든 외형을 보기 전용으로 보존한다. v5 이하 Header 아래의 `textMarkup`·`note` Envelope은 거절(보기 전용)이다.
- v1~v5 `Serialize`는 새 종류가 있는 모델을 거절한다(`Text markups and notes need schema v6.`). 구현: V1/V3/V4의 `allowV6` 인자.
- `EditablePdfReader`는 v4·v5·v6 모두 원본 보관·배치를 사용한다.

## 다른 앱 주석 변환과의 관계 (명세 14.6)

설정 `다른 앱 주석 편집 허용`이 켜져 있으면 다른 앱의 `/Underline`·`/StrikeOut`은 `textMarkup`(굵기는 `/BS /W` 또는 1pt, 0.5~4로 제한)으로, 글자가 있는 `/Text` 메모는 `note`(아이콘 왼쪽 위 = `/Rect` 왼쪽 위)로 바꾼다. Flatten한 앱 메모도 이렇게 다시 편집할 수 있다.
