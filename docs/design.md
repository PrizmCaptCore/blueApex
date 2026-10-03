# BlueApex 디자인 규칙

기조: **Fluent 다크(Windows 11)** 를 뼈대로, 서랍 격자와 바탕화면 버튼은 런처답게 조금 더 둥글고 넉넉하게.
모든 화면이 배경화면 위에 뜨므로 표면은 어둡고 반투명, 글자는 흰색, 강조색은 Windows 강조색을 따른다.

## 어디서 바꾸나

| 바꾸고 싶은 것 | 파일 |
| --- | --- |
| 색, 모서리, 간격, 글자 크기 **값** | `src/BlueApex/Ui/Theme.cs` — 토큰 하나를 바꾸면 그걸 쓰는 모든 곳이 바뀐다 |
| 버튼·입력창·체크·메뉴·구분선의 **생김새** | `src/BlueApex/Ui/Styles.xaml` — 암시적 스타일. 종류별 블록 하나가 그 컨트롤 전부 |
| 카드·타일·묶음 머리줄·드롭 알약·위젯 패널·대화상자의 **구조** | `src/BlueApex/Ui/Parts.cs` — 부품 하나를 고치면 그 부품을 쓰는 화면 전부 |
| 특정 화면의 배치만 | 그 화면 파일(`DrawerWindow.cs`, `BuiltInWidgets.cs`, `DesktopButton.cs`...) — 부품을 조립만 한다 |

규칙: 화면 코드에는 색이나 크기를 직접 쓰지 않는다. `Theme.*` 토큰이나 `Parts.*` 부품을 쓴다.
한 컨트롤만 다르게 하려면 `Style="{x:Null}"`로 암시적 스타일을 끄고 따로 준다.

## 토큰

- 표면: `Scrim`(서랍 뒤 어둡게) → `Card`/`CardAlt`(카드) → `Panel`(바탕화면 층, 불투명) → `Input`(입력창). 위로 갈수록 밝다.
- 선: `Border`(헤어라인), 상태: `Hover`, `Selection`(강조색 60%), `GroupBar`, `ButtonFill`.
- 글자: `Text`, `TextDim`, `TextFaint`. 크기 `FontSmall 12 / FontBody 13 / FontAction 15 / FontTitle 16 / FontLarge 20 / FontDisplay 52(시계)`. 글꼴 Segoe UI Variable → 맑은 고딕.
- 강조: `Accent`(Windows 강조색, 없으면 파랑), 파생 `Selection`/`MarqueeFill`/`MarqueeStroke`. 파괴적 동작은 `DangerColor`.
- 모서리: 카드 14, 타일 8, 컨트롤 6, 알약 12. 간격: 4 / 8 / 12 / 16. 타일 100×104, 아이콘 48, 카드 폭 440.

## 제약

- 바탕화면 층(위젯, 버튼)은 창 전체 투명도만 되므로 `Panel`처럼 불투명한 표면 위에 그린다. 픽셀 단위 투명은 서랍에서만.
- 위젯 플러그인은 자유지만, 내장 위젯과 어울리려면 `Parts.Panel`과 `Theme` 토큰을 쓰는 것을 권한다(SDK가 아닌 앱 내부 API이므로 플러그인은 값을 베껴 쓴다).
