# 편집 메타데이터 스키마 v7 — CLI 주석 태그 (C3)

코드: `Core/Models/AnnotationTags.cs`(`AnnotationTag`), `Core/Models/DocumentModel.cs`(`Tags`·`TagOf`·`LiveTags`), `Core/Commands/EditOperations.cs`(`SetTagOperation`), `Core/Models/ModelValidator.cs`, `Core/Serialization/SchemaV7.cs`, `Pdf/Reading/EditablePdfReader.cs`·`Pdf/Writing/EditablePdfWriter.cs`(v7로 읽고 쓰기). 계획: `../cli_plan.md` 6절. v6의 모든 필드·의미와 Envelope 내용을 그대로 유지한다.

## 태그 표

```json
{
  "app": "PdfNotes", "schemaVersion": 7, "…": "v6 Header 필드 그대로",
  "tags": [
    { "annotationId": "…", "author": "ai", "batchId": "run-2026-10-24-01",
      "sourceKey": "lecture-A/seg-012/text", "groupKey": "lecture-A/seg-012" }
  ]
}
```

- 태그는 주석 레코드가 아니라 **문서의 표(주석 ID → 태그)**다. 주석 종류·Envelope·외형은 v6와 같고, Envelope의 `schemaVersion`만 7이다.
- `author`: 1~64자(필수, CLI는 `"ai"`). `batchId`·`sourceKey`·`groupKey`: 선택, 있으면 1~512자. 제어 문자 금지(`AnnotationTag.Validate`).
- `sourceKey`의 유일성(같은 author 안에서)은 저장 형식이 아니라 CLI가 검사한다(`DUPLICATE_SOURCE_KEY`, C5). 파일에서 중복이 발견되면 CLI가 임의로 고르지 않고 오류로 알린다.
- 앱 화면에는 태그를 보이지 않는다.

## 쓰기

- **태그가 하나라도 있는 문서만 v7로 쓴다.** 태그가 없으면 v6 그대로라서, CLI가 태그를 붙이지 않은 파일은 이전 앱 버전에서도 편집할 수 있다.
- 존재하지 않는 주석의 태그(지운 주석의 남은 항목)는 쓰지 않는다. 태그 표 순서는 페이지·쌓임 순서.
- `EditablePdfWriter.Verify`도 v7로 다시 읽어 Header 바이트를 비교하므로 태그 표까지 검증된다.

## 읽기

- `SchemaV7.Read`는 v7 Header에서 `tags`를 떼고 v6로 읽은 뒤, 복원된 주석의 태그만 붙인다(거절·빈 주석의 태그는 버림). v1~v6은 각 기존 리더로 넘긴다.
- `tags` 항목에 모르는 필드, 잘못된 값, 같은 주석 ID 중복이 있으면 Header 전체를 Invalid로 보고 외형을 보기 전용으로 둔다(추측하지 않음).
- v1~v6 리더(이전 앱)는 v7 Header를 Unsupported로 보고 모든 외형을 보기 전용으로 보존한다.
- `EditablePdfReader`는 v4~v7 모두 원본 보관·배치를 사용한다.

## 앱 편집과 태그

| 앱 동작 | 태그 |
|---|---|
| 이동·회전·크기·서식 변경, 페이지 변환·회전·이동 | 주석 ID가 그대로라 유지 |
| 삭제 | 저장에서 빠짐(표의 항목은 남아 있어 Undo하면 태그도 돌아옴) |
| 복사·붙여넣기(같은 창·다른 창), 페이지 복제, 지우개 조각 | 새 ID라 태그 없음(사람 필기로 취급) |
| 주석 클립보드 형식(`AnnotationTransfer`) | v6 그대로(복사본은 태그가 없으므로) |

태그를 바꾸는 편집은 `SetTagOperation` 하나뿐이고 CLI만 쓴다. Undo는 주석 삽입과 같은 단위로 되돌린다.
