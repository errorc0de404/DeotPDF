# `deot` 명령줄 도구 설명서

[English](cli.md)

`deot`는 덧PDF의 명령줄 버전입니다. PDF를 읽어서 페이지마다 무엇이 어디에 있는지 알려 주고, 형광 표시, 텍스트 상자, 선, 도형, 펜 획, 이미지, 메모를 써 넣습니다. 페이지 순서나 크기도 바꿀 수 있습니다. 앱과 같은 엔진으로 쓰기 때문에 결과물은 덧PDF에서 손으로 그린 것과 똑같이 보이고, 앱에서 이어서 고칠 수 있습니다.

AI 에이전트나 스크립트가 쓰라고 만든 도구라서 동작 방식이 조금 독특합니다.

- 모든 명령은 표준 출력에 **JSON 하나**만 찍습니다. 로그와 오류 추적은 표준 오류로 나갑니다.
- 오류에는 **정해진 코드**(`AMBIGUOUS_MATCH`, `PAGE_OVERFLOW` 등)가 붙습니다. 메시지 문장 말고 코드로 판단하면 됩니다.
- `deot`는 **대신 고르지 않습니다**. 찾는 문구가 두 군데 있으면 아무거나 고르지 않고 두 후보를 담은 오류를 돌려줍니다. 어디에 무엇을 둘지는 부르는 쪽이 정합니다.
- **입력 파일은 절대 바뀌지 않습니다**. 쓰는 명령에는 `-o <출력>`이나 `--dry-run`이 꼭 필요합니다.

목차: [준비](#준비) · [출력과 종료 코드](#출력과-종료-코드) · [좌표](#좌표와-번호) · [명령 목록](#명령-목록) · [쓰기 옵션](#쓰기-옵션) · [서식 있는 글](#서식-있는-글과-스타일) · [태그와 키](#태그-sourcekey-groupkey) · [apply](#apply-여러-작업을-한-번에) · [check](#check-규칙) · [추천 흐름](#에이전트에게-추천하는-흐름) · [오류 코드](#오류-코드)

## 준비

`deot.exe`는 `DeotPDF.exe`와 같은 zip에 들어 있습니다([README](../README.md#다운로드와-실행) 참고). 그 폴더를 `PATH`에 넣거나 전체 경로로 실행하세요.

소스에서 빌드했다면:

```powershell
dotnet build PdfNotes.slnx -c Release
tools\PdfNotes.Cli\bin\Release\net10.0-windows\win-x64\deot.exe schema
```

`deot schema`는 명령 목록, 종료 코드, 오류 코드를 보여 줍니다. `deot schema <명령>`(예: `deot schema add text`)은 그 명령의 옵션과 출력 JSON Schema를 보여 줍니다. 이 문서와 `deot schema`가 다르면 `deot schema`가 맞습니다.

## 출력과 종료 코드

성공하면:

```json
{"ok": true, "command": "info", "sourceHash": "sha256:7cc1…", "pageCount": 120, ...}
```

실패하면:

```json
{"ok": false, "error": {"code": "AMBIGUOUS_MATCH", "message": "...", "details": {"matches": [...]}}}
```

| 종료 코드 | 뜻 |
|---|---|
| 0 | 성공 |
| 1 | 입력이 잘못됨 (옵션 오류, 파일 없음, 없는 페이지 등) |
| 2 | 입력은 맞는데 요청대로 할 수 없음 (일치 없음, 빈 자리 없음, 페이지 밖으로 넘침 등) |
| 3 | 파일이 잠겨 있거나, 덧PDF에서 열려 있거나, 읽은 뒤에 바뀜 |
| 4 | 내부 오류 |

메시지는 항상 영어입니다. 성공한 결과에도 `warnings`가 붙을 수 있습니다. 예를 들어 글꼴이 설치되어 있지 않으면 `FONT_FALLBACK`이 붙습니다.

## 좌표와 번호

- 단위는 PDF 포인트(1/72인치)입니다.
- 원점은 **회전하기 전 페이지의 왼쪽 위**입니다. x는 오른쪽, y는 아래쪽으로 커집니다. 페이지를 돌려도 이 좌표는 바뀌지 않습니다.
- 사각형은 `[x, y, w, h]`(명령줄에서는 `x,y,w,h`), 사변형은 `[[왼쪽 위], [오른쪽 위], [왼쪽 아래], [오른쪽 아래]]`, 행렬은 `[a, b, c, d, e, f]`이고 `x' = a·x + c·y + e`, `y' = b·x + d·y + f`입니다.
- `render`와 `--preview` 결과에 `pageToPixel`, `pixelToPage` 행렬이 들어 있어서, PNG에서 본 위치를 페이지 좌표로 바꿀 수 있습니다.
- 페이지 번호는 1부터 셉니다. `info`에 나오는 페이지 ID를 써도 됩니다. 덧PDF로 저장한 적 없는 PDF는 파일 해시로 페이지 ID를 만들기 때문에, 같은 파일이면 항상 같은 ID가 나옵니다.
- 글자·단어 번호(`text`, `find`, `layout`)는 0부터 셉니다. 범위는 끝을 포함하지 않아서 `--chars 10:20`은 10번부터 19번 글자까지입니다. 원본 PDF의 글자 층 기준이라 주석을 달거나 페이지를 줄여도 번호가 밀리지 않습니다.

## 명령 목록

| 분류 | 명령 | 하는 일 |
|---|---|---|
| 읽기 | `info <pdf>` | 페이지 수·크기·회전, 글자 층 유무, 주석 수, `sourceHash` |
| | `render <pdf> -p <페이지> -o <png>` | 페이지나 `--region` 영역을 PNG로. `--pages 1-8 --grid`는 여러 쪽을 한 장에 모아 줍니다. `--annotations all\|none\|tagged\|...`로 그릴 주석을 고르고, `--overlay`는 PNG에만 상자를 그립니다 |
| | `text <pdf> -p <페이지>` | 글자·단어·줄과 그 위치(사변형) |
| | `annot list <pdf>` | 주석 종류·범위·스타일·글·태그. `-p`, `--kind`, `--tag`, `--batch-id`, `--source-key`, `--group-key`로 거를 수 있습니다 |
| 찾기 | `find <pdf> --text <문구>` | 문구 위치. 대소문자, 띄어쓰기, 줄바꿈, 하이픈은 무시합니다. `--within x,y,w,h`로 범위를 좁히고 `--fuzzy`로 비슷한 것도 찾습니다 |
| | `layout <pdf> -p <페이지>` | 줄을 문단과 목록 항목으로 묶어서 글자 범위, 글머리 기호·번호와 함께 돌려줍니다 |
| | `ink <pdf> -p <페이지>` | 칸마다 잉크가 얼마나 있는지. `--blocks`는 그림·도표 같은 덩어리를 돌려줍니다 |
| | `space <pdf> -p <페이지> --size w,h` | 잉크와 `--avoid` 영역을 피해 `w×h`가 들어가는 빈 자리. `--largest n`은 가장 큰 빈 자리를 찾습니다 |
| 재기 | `measure --text <글> --width <pt>` | 높이, 줄, 실제로 쓰인 글꼴, 없는 글자. `add text`와 똑같이 계산합니다 |
| | `fonts` | 쓸 수 있는 글꼴 목록 |
| 페이지 | `page duplicate`, `insert-blank`, `delete`, `move`, `rotate` | 복제, 빈 페이지 넣기, 삭제, 이동, 회전 |
| | `page shrink --scale 0.7` | 페이지 크기는 그대로 두고 내용과 주석을 함께 줄입니다 |
| | `page resize --size w,h --mode fit\|clip` | 페이지 크기 변경. `clip`은 내용 크기를 그대로 둡니다(필기할 여백 만들 때 좋습니다) |
| | `page import --from 다른.pdf` | 다른 PDF의 페이지 넣기 |
| | `page export --pages 1-3 -o 일부.pdf` | 일부 페이지만 따로 PDF로 |
| 만들기 | `add text` | 서식 있는 텍스트 상자. 높이는 글에 맞춰집니다 |
| | `add mark` | PDF 글자에 형광·밑줄·취소선. `--chars`, `--words`, `--quads`, `--find` 중 하나로 위치를 줍니다 |
| | `add highlight-stroke` | 자유 형광 획, 또는 꽉 찬 형광 사각형(스캔 페이지에 유용) |
| | `add line` | 직선이나 베지어 곡선. 화살표, 점선 가능 |
| | `add shape` | 사각형, 타원, 삼각형, 다각형 |
| | `add ink` | 주어진 점을 지나는 펜 획 |
| | `add image` | PNG, JPEG, BMP |
| | `add note` | 스티키 메모 |
| 고치기 | `annot update` | 글, 스타일, 크기, 위치, 회전 바꾸기 |
| | `annot delete` | ID, 배치, sourceKey, groupKey로 지우기 |
| | `annot order` | 맨 앞으로, 맨 뒤로 등 |
| | `annot lock` | 잠금, 잠금 해제 |
| | `annot copy` | 다른 페이지나 위치로 복사 |
| 검사 | `check --rules <json>` | 보호 영역 겹침, 페이지 밖, 글 넘침 같은 기하 검사 |
| 일괄 | `apply <pdf> <plan.json>` | 여러 작업을 한 번에. 전부 되거나 전부 안 되거나 |

## 쓰기 옵션

쓰는 명령은 모두 아래 옵션을 받습니다.

| 옵션 | |
|---|---|
| `-o <pdf>` | 저장할 파일. 입력 파일과 같으면 안 됩니다. 이미 있으면 `--overwrite`를 붙이세요 |
| `--dry-run` | 파일만 안 쓰고 나머지는 다 합니다. JSON 결과는 똑같습니다 |
| `--preview <png 또는 폴더>` | 바뀐 페이지를 PNG로도 그려 줍니다 |
| `--expect-hash <sha256:…>` | 입력이 앞서 읽은 파일과 다르면 거절(`HASH_MISMATCH`) |
| `--batch-id <id>` | 이번 실행에서 쓴 것에 이름표를 붙입니다 |
| `--flatten` | 필기를 페이지에 합친 PDF로 저장(이후에는 고칠 수 없음) |

주석을 만드는 명령은 `--source-key`, `--group-key`, `--lock`도 받습니다(아래 참고).

덧PDF 창에서 그 파일을 열어 두었으면 `FILE_OPEN_IN_APP`으로 거절합니다. 창을 닫거나 다른 파일로 저장하세요.

## 서식 있는 글과 스타일

`add text`와 `measure`의 `--text`에는 간단한 표시를 쓸 수 있습니다.

| 표시 | 결과 |
|---|---|
| `**굵게**` | 굵게 |
| `*기울임*` | 기울임 |
| `__밑줄__` | 밑줄 |
| `~~취소선~~` | 취소선 |
| `==형광==` | 형광 |

표시 글자를 그대로 쓰고 싶으면 앞에 백슬래시를 붙입니다. 더 세밀하게 하려면 JSON 목록이나 `@파일.json`을 넘기면 됩니다.

```json
[{"text": "참고: ", "bold": true, "color": "#C00000"}, {"text": "그림 2 확인", "highlight": "#FFF59D"}]
```

조각마다 `text`, `bold`, `italic`, `underline`, `strikeout`, `color`, `size`, `font`, `highlight`, `highlightOpacity`를 줄 수 있습니다.

`--style`은 상자 전체 설정입니다. `font`, `size`, `color`, `bold`, `italic`, `alignment`(`left`, `center`, `right`), `background`, `backgroundOpacity`, `border`, `borderColor`, `borderOpacity`, `highlightColor`, `highlightOpacity`를 쓸 수 있고, 안 준 값은 앱 기본값을 따릅니다.

텍스트 상자는 글에 맞춰 아래로 늘어납니다. `h`를 `0`으로 주고 `deot`에 맡기면 되고, `--no-auto-height`를 붙이면 `h`를 한계로 보고 넘치면 `TEXT_OVERFLOW`를 냅니다. 상자가 페이지 밖으로 나가면 `PAGE_OVERFLOW`로 실패합니다. 그래도 쓰려면 `--overflow allow`를 붙이세요.

## 태그, sourceKey, groupKey

`deot`가 만든 주석에는 숨은 태그가 붙습니다. 작성자 `ai`, 배치 ID, 그리고 선택으로 sourceKey와 groupKey입니다. 앱 화면에는 안 보이고, 사람이 앱에서 옮기거나 고쳐도 그대로 남습니다.

- **sourceKey**(`--source-key lecture/p3/para-1`)는 문서 안에서 주석 하나를 가리키는 이름입니다. 이미 있는 키로 다시 쓰면 새로 하나 더 생기는 게 아니라 **그 주석을 교체**합니다. ID와 쌓인 순서도 그대로 유지됩니다. 그래서 같은 스크립트를 여러 번 돌려도 중복이 생기지 않습니다.
- **groupKey**(`--group-key lecture/p3/para-1`)는 함께 다니는 주석을 묶습니다. 예를 들어 설명 상자, 형광 표시, 화살표를 한 묶음으로 두면 `annot list --group-key`로 함께 보고 `annot delete --group-key`로 함께 지울 수 있습니다.
- 태그가 없는 주석(사람이 쓴 필기, 다른 프로그램에서 단 주석)은 `deot` 입장에서 **읽기 전용**입니다. 고치거나 지우려 하면 `FORBIDDEN`이 나옵니다. 꼭 필요하면 `--allow-untagged`를 붙이세요.

같은 묶음이라도 주석마다 키는 따로 주세요. 글은 `p3/para-1`, 형광은 `p3/mark-1`처럼요. 다른 종류의 주석에 같은 키를 쓰면 `SOURCE_KEY_KIND_MISMATCH`가 납니다.

## `apply`: 여러 작업을 한 번에

`apply`는 계획 파일을 읽어 모든 작업을 메모리에서 차례로 실행하고 마지막에 한 번만 저장합니다. 하나라도 실패하면 아무것도 저장하지 않고, 몇 번째 작업이 실패했는지(`details.opIndex`) 알려 줍니다.

```json
{
  "batchId": "lecture-A-v3",
  "defaults": {
    "add.text": { "style": { "size": 11, "color": "#D32F2F" } }
  },
  "ops": [
    { "op": "page.resize", "pages": "3-5", "size": [1300, 540], "mode": "clip", "anchor": "left" },
    { "op": "add.mark", "page": 3, "find": "막전위", "sourceKey": "p3/mark-1" },
    { "op": "add.text", "page": 3, "rect": [975, 60, 305, 0],
      "text": "**안정막전위**는 약 -70 mV", "sourceKey": "p3/para-1", "groupKey": "p3/para-1" }
  ],
  "guards": [
    { "rule": "outsidePage", "select": { "tagged": true } }
  ]
}
```

```powershell
deot apply 강의.pdf plan.json --dry-run --preview-dir 미리보기   # 먼저 확인하고
deot apply 강의.pdf plan.json -o 강의_필기.pdf                    # 저장
```

계획 파일 규칙:

- `op`는 명령 이름의 공백을 점으로 바꾼 것입니다. `add.text`, `page.resize`, `annot.delete`처럼요.
- 필드 이름은 옵션 이름을 camelCase로 쓴 것입니다(`--source-key` → `sourceKey`). 모르는 필드는 오류라서 오타가 조용히 넘어가지 않습니다.
- 값은 사각형 `[x, y, w, h]`, 글자 범위 `{"start": 10, "end": 20}`, JSON 값(`points`, `style`, 글 조각 목록)은 JSON 그대로, 플래그는 `true`로 씁니다.
- `page` 번호는 앞 작업에서 페이지를 넣거나 지웠더라도 항상 **입력 파일 기준**입니다. 작업이 새로 만든 페이지는 `"as": "p3a"`로 이름을 붙이고 뒤에서 `"page": "$p3a"`로 가리킵니다.
- 좌표는 **그 작업이 실행될 때** 페이지 기준입니다. 앞에서 페이지를 줄였다면 그 작업 결과의 `matrix`로 좌표를 바꾸세요(페이지 작업만 넣고 `--dry-run`을 먼저 돌리면 나옵니다). `chars`, `words`, `find`로 준 표시는 알아서 따라갑니다.
- `defaults`는 작업 종류별 기본 필드입니다. 작업 안에 직접 쓴 값이 우선합니다.
- `guards`는 결과에 돌리는 `check` 규칙입니다. error 수준 문제가 나오면 `CHECK_FAILED`로 저장하지 않고 문제 목록을 돌려줍니다.
- 결과의 `summary`에 만든·교체한·고친·지운 주석 수와 만든·지운·변환한 페이지 수가 들어 있습니다.

## `check` 규칙

```powershell
deot check 결과.pdf --rules '[{"rule": "overlapRegions", "regions": [[0,0,960,540]], "select": {"kinds": ["text"], "tagged": true}}]'
```

| 규칙 | 찾는 것 | 매개변수 |
|---|---|---|
| `overlapRegions` | 고른 주석이 지정한 영역과 겹침 | `regions`, `tolerance` |
| `overlapAmong` | 고른 주석끼리 겹침 | `tolerance` |
| `outsidePage` | 페이지 밖으로 삐져나감 | `tolerance` |
| `textOverflow` | 텍스트 상자가 `maxHeight`보다 큼 | `maxHeight` |
| `minFontSize` | 글자가 `size`pt보다 작음 | `size` |
| `missingGlyph` | 글꼴에 없는 글자 | |

규칙마다 `level`(기본 `error`, 또는 `warn`)과 `select`를 줄 수 있습니다. `select`에는 `kinds`, `ids`, `sourceKeys`, `groupKeys`, `batchIds`, `tagged`, `pages`를 쓰고, 일부러 겹치게 둔 작은 번호 표시처럼 빼고 싶은 것은 `exclude`(`pages` 빼고 같은 필드)로 뺍니다. `check`는 문제를 찾아도 종료 코드 0입니다. `passed` 값을 보세요.

검사는 주석의 경계 사각형으로 합니다. 그래서 길게 대각선으로 그은 화살표는 실제로는 그림을 가로지르는데도 통과하는 경우가 많습니다. 미리보기 그림으로 꼭 한 번 보세요.

## 에이전트에게 추천하는 흐름

강의 슬라이드에 필기를 다는 에이전트라면 대략 이렇게 씁니다.

1. **훑어보기.** `info`로 시작해서 `render --pages 1-8 --grid --dpi 40`으로 전체를 봅니다. 페이지마다 `layout`(문단과 글자 범위), `find`(특정 문구), `ink --blocks`(그림 위치), `space`(빈 자리)를 봅니다.
2. **정하기.** 무엇을 어디에 둘지는 부르는 쪽 몫입니다. `deot`는 후보 중에서 고르지 않습니다.
3. **재기.** 글을 놓기 전에 `measure --width ...`로 크기를 잽니다. `add text`가 만들 크기와 정확히 같습니다.
4. **자리부터 만들기.** 페이지 작업만 넣은 계획을 `--dry-run`으로 돌려 `pageTransforms[].matrix`를 받고, 그걸로 좌표를 바꿉니다. `chars`, `words`, `find`로 준 표시는 알아서 맞춰집니다.
5. **전체 계획 미리보기.** `--preview-dir`로 PNG를 보고, `guards`를 걸고, 계획을 고칩니다.
6. **항상 원본에 전체 계획을 적용하기.** 직전 결과물 위에 덧붙이지 마세요. 같은 원본에 같은 계획이면 같은 결과가 나오고, sourceKey 덕분에 다시 돌려도 중복 없이 교체됩니다.
7. 마무리는 사람이 덧PDF에서 직접 합니다.

해 보면서 알게 된 것들:

- `layout`이 주는 블록 범위에는 앞의 글머리 기호나 번호가 포함됩니다. 형광 표시는 `start + 1`부터 주는 게 보통 더 깔끔합니다.
- 슬라이드에 여백이 없으면 `page.resize`를 `"mode": "clip", "anchor": "left"`로 쓰면 오른쪽에 빈 공간이 생깁니다. 행렬이 항등이라 기존 좌표를 그대로 쓸 수 있습니다.
- 어떤 대상 옆에 작은 번호 표시를 둘 자리는 `space -p 3 --size 15,15 --within <대상 주변> --gap 1 --cell 1`로 찾으면 잘 나옵니다.
- 긴 단어도 글자 단위로 줄바꿈되기 때문에 옆으로 넘치는 일은 없습니다. 신경 쓸 것은 높이입니다(`measure --height`나 `textOverflow` 규칙).

## 오류 코드

정확한 목록은 `deot schema`의 `errorCodes`입니다.

| 코드 | 종료 | 뜻 |
|---|---|---|
| `INVALID_ARGUMENT` | 1 | 옵션이나 필드가 없거나, 모르는 것이거나, 범위를 벗어남 |
| `UNKNOWN_COMMAND` | 1 | 없는 명령 |
| `FILE_NOT_FOUND` | 1 | 입력, 이미지, 글, 스타일, 규칙, 계획 파일이 없음 |
| `PDF_OPEN_FAILED` | 1 | PDF를 열 수 없음 |
| `PAGE_NOT_FOUND` | 1 | 없는 페이지 번호, 페이지 ID, `$이름` |
| `ANNOTATION_NOT_FOUND` | 1 | 없는 주석 ID나 sourceKey |
| `RANGE_OUT_OF_TEXT` | 1 | `--chars`, `--words`가 글자 수를 넘음 |
| `NOT_TRANSFORMABLE` | 1 | 글자 표시는 그런 식으로 옮기거나 돌리거나 복사할 수 없음 |
| `INVALID_IMAGE` | 1 | PNG, JPEG, BMP가 아님 |
| `IMAGE_TOO_LARGE` | 1 | 그림이 너무 큼. `--dpi`를 낮추거나 `--region`을 쓰세요 |
| `SAME_AS_INPUT` | 1 | 출력(미리보기) 경로가 입력 파일임 |
| `OUTPUT_EXISTS` | 1 | 출력 파일이 이미 있는데 `--overwrite`가 없음 |
| `NOT_FOUND` | 2 | `--find`와 정확히 맞는 것이 없음 |
| `AMBIGUOUS_MATCH` | 2 | `--find` 후보가 둘 이상(`details.matches`) |
| `EMPTY_RANGE` | 2 | 그 범위에 보이는 글자가 없음 |
| `NO_SPACE` | 2 | `space`가 빈 자리를 못 찾음 |
| `REGION_OUTSIDE_PAGE` | 2 | `--region`이 페이지와 겹치지 않음 |
| `PAGE_OVERFLOW` | 2 | 텍스트 상자가 페이지 밖으로 나감 |
| `TEXT_OVERFLOW` | 2 | `--no-auto-height`인데 글이 상자보다 김 |
| `LAST_PAGE` | 2 | 페이지를 전부 지울 수는 없음 |
| `SOURCE_KEY_KIND_MISMATCH` | 2 | 그 키는 다른 종류의 주석 것 |
| `DUPLICATE_SOURCE_KEY` | 2 | 같은 키가 여러 개이거나, 한 `apply`에서 두 번 씀 |
| `FORBIDDEN` | 2 | `deot`가 만든 주석이 아님(`--allow-untagged`로 허용 가능) |
| `LOCKED_ANNOTATION` | 2 | 잠긴 주석. `annot lock --off`로 풀거나 지울 때 `--include-locked` |
| `CHECK_FAILED` | 2 | `apply`의 guards에서 error 수준 문제가 나옴(`details.problems`), 저장 안 함 |
| `HASH_MISMATCH` | 3 | `--expect-hash` 이후 입력 파일이 바뀜 |
| `FILE_OPEN_IN_APP` | 3 | 덧PDF 창이 그 파일을 열고 있음 |
| `FILE_LOCKED` | 3 | 다른 프로그램이 입력 파일을 잡고 있음 |
| `SAVE_FAILED` | 4 | 출력 파일 쓰기 실패(잠김 때문이면 3) |
| `INTERNAL` | 4 | 예상 못 한 오류. `details.type`과 표준 오류의 추적 정보를 보세요 |

성공 결과에 붙을 수 있는 경고: `FONT_NOT_INSTALLED`, `FONT_FALLBACK`, `MISSING_GLYPH`, 그리고 `--overflow allow`를 줬을 때의 `PAGE_OVERFLOW`, `TEXT_OVERFLOW`.
