# BlueApex

Windows 11 바탕화면 트레이 앱

- **홈 화면**: 위젯 + 꺼내둔 앱 아이콘
- **서랍**: android 형태의 서랍 정리 방식
  바탕화면의 둥근 버튼, `Ctrl+Shift+Space`, 트레이 아이콘으로 open
- **위젯**: 시계·메모가 들어 있고, 플러그인 DLL로 추가 가능

배경 화면의 기본 아이콘은 지우는 것이 아닌, 프로그램 종료나 삭제시 원복되는 형태 입니다.

## 설치

[Releases](https://github.com/PrizmCaptCore/blueApex/releases)에서 `BlueApex-Setup-<버전>.exe`를 받아 실행. Windows 11 (24H2 이후 권장).
코드 서명을 따로 구매하지 않았습니다. smart screen 차단이 떠도 어쩔 수 없는 부분 입니다. 직접 빌드시 뜨지 않습니다만, 귀찮으실 경우 그냥 사용하셔도 무방합니다.
단, github release 이외의 다른 경로로 얻은 SW라면 주의해주세요.

## 소스로 실행

```sh
dotnet run --project src/BlueApex
```

.NET 9 SDK. 배포 파일 만들기는 [docs/release.md](docs/release.md).

## 더 읽기

- [docs/usage.md](docs/usage.md) — 사용법과 동작 방식
- [docs/code-tour.md](docs/code-tour.md) — 코드 안내(구조, 흐름, 쓰인 C# 문법)
- [docs/design.md](docs/design.md) — 디자인 규칙(토큰·스타일·부품, 어디를 고치면 되는지)
- [docs/widget-sdk.md](docs/widget-sdk.md) — 위젯 플러그인 만들기 (`samples/` 참고)

## 라이선스

MIT (LICENSE 참고). 저작권자 prizmcaptcore. 재배포 구성 요소 고지는 THIRD-PARTY-NOTICES.md.
