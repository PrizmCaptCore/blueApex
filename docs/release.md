# 배포와 업데이트

인스톨러는 Inno Setup, 배포는 GitHub Releases, 업데이트 확인은 앱이 직접 한다. 서버는 없다.

## 한 번만 준비

- Inno Setup 6: `winget install JRSoftware.InnoSetup`
- GitHub 저장소. 주소는 `src/BlueApex/Updater.cs`의 `Repo` 상수와 `build/BlueApex.iss`의 `AppPublisherURL`에 적혀 있다. 저장소 이름이 다르면 둘 다 고친다.

## 릴리스 절차

1. `src/BlueApex/BlueApex.csproj`의 `<Version>`을 올린다 (예: 0.2.0).
2. 실행 중인 앱을 끈다: `BlueApex.exe --exit`.
3. `.\build\publish.ps1` → `build\out\app\`(self-contained win-x64)와 `build\out\BlueApex-Setup-0.2.0.exe`.
4. 설치 파일을 한 번 설치해 본다(아래 "인스톨러가 하는 일" 참고).
5. 커밋하고 태그를 붙인다: `git tag v0.2.0`, push.
6. GitHub Releases에서 태그 `v0.2.0`으로 릴리스를 만들고 **`BlueApex-Setup-0.2.0.exe`를 첨부**한다.
   앱은 태그에서 버전을, 첨부 파일 중 `BlueApex-Setup-*.exe`에서 설치 파일을 찾는다. 둘 중 하나가 없으면 업데이트로 인식하지 않는다.

## 인스톨러가 하는 일 (`build/BlueApex.iss`)

- 설치·업데이트·제거 전에 실행 중인 BlueApex에 `--exit`를 보내고 뮤텍스가 사라질 때까지(최대 15초) 기다린다. 그래야 숨긴 아이콘이 돌아오고 파일 잠금이 풀린다.
- `Program Files\BlueApex`에 설치, 시작 메뉴에 "BlueApex"와 "BlueApex 종료 (아이콘 복원)".
- 선택: Windows 시작 시 자동 실행(앱의 트레이 토글과 같은 레지스트리 값), 공용 바탕화면 권한(icacls, Users 그룹).
- WebView2 런타임이 없으면 부트스트래퍼를 받아 조용히 설치한다(Windows 11에는 이미 있다).
- 제거 시 설정 폴더 `%AppData%\BlueApex`를 지울지 묻는다(기본은 "아니요").
- 앱이 시작한 조용한 업데이트(`/SILENT /LAUNCH=1`)는 설치 뒤 앱을 다시 켠다.

## 앱 안의 업데이트 (`Updater.cs`)

- 시작 30초 뒤와 24시간마다 GitHub API로 최신 릴리스를 본다(트레이 "업데이트 자동 확인"으로 끌 수 있고, "업데이트 확인"으로 수동 확인).
- 새 버전이 있으면 트레이 알림을 띄우고 메뉴에 "업데이트 설치 (vX)"가 나타난다. 누르면 확인 뒤 설치 파일을 `%TEMP%`에 받아 `/SILENT /LAUNCH=1`로 실행하고 앱은 종료한다. UAC 창이 한 번 뜬다.
- 사용자가 승인하지 않으면 아무것도 받지 않는다.

## 코드 서명

하지 않는다. 처음 실행할 때 SmartScreen이 "알 수 없는 게시자" 경고를 보이며, "추가 정보 → 실행"으로 넘어간다. README에 그렇게 적어 둔다.
