# BlueApex

Windows 11 바탕화면을 안드로이드 홈 화면처럼 쓰게 해 주는 트레이 앱.

- **홈 화면**: 바탕화면에는 꺼내 둔 아이콘과 위젯만 남는다.
- **서랍**: 나머지 아이콘은 전부 서랍에 들어가고, 구역(카테고리)별로 보고 검색해서 실행한다.
  바탕화면의 둥근 버튼, `Ctrl+Shift+Space`, 트레이 아이콘으로 연다.
- **위젯**: 시계·메모가 들어 있고, 플러그인 DLL로 더 만들 수 있다.

아이콘을 옮기거나 지우지 않는다. 숨김 속성만 쓰고, 끄면 전부 원래대로 돌아온다.

## 실행

```sh
dotnet run --project src/BlueApex
```

.NET 9 SDK, Windows 11 (24H2 이후 권장).

## 더 읽기

- [docs/usage.md](docs/usage.md) — 사용법과 동작 방식
- [docs/code-tour.md](docs/code-tour.md) — 코드 안내(구조, 흐름, 쓰인 C# 문법)
- [docs/design.md](docs/design.md) — 디자인 규칙(토큰·스타일·부품, 어디를 고치면 되는지)
- [docs/widget-sdk.md](docs/widget-sdk.md) — 위젯 플러그인 만들기 (`samples/` 참고)

## 라이선스

MIT (LICENSE 참고). 저작권자 prizmcaptcore. 재배포 구성 요소 고지는 THIRD-PARTY-NOTICES.md.
