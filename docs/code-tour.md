# BlueApex 코드 안내

C#을 깊이 몰라도 이 코드를 따라 읽을 수 있도록 쓴 문서다. 전체 약 4,800줄, 그중 지금 실제로 쓰이는 건 절반 정도다.

## 1. 지도

```
src/BlueApex/
  App.xaml.cs            시작·종료, 트레이 메뉴. 모든 부품을 여기서 조립한다.
  Log.cs                 log.txt에 한 줄 쓰기.
  Autostart.cs           로그인 시 자동 실행(작업 스케줄러 로그온 작업, COM late binding). 예전 Run 키는 옮긴다.
  StartupMovie.cs        로그인 직후 Steam 부팅 영상을 전체 화면으로 재생(WebView2). 파일은 Steam 설치본의 것.
  Updater.cs             GitHub Releases에서 새 버전 확인, 설치 파일 받아서 조용히 실행 (docs/release.md).
  Secret.cs              작은 비밀값(디스코드 Secret·토큰, Spotify 토큰)을 Windows DPAPI로 암호화/복호화.

build/
  publish.ps1            self-contained 배포 빌드 + 인스톨러 생성. 버전은 csproj의 <Version>.
  BlueApex.iss           Inno Setup 스크립트: 실행 중인 앱 --exit, WebView2 런타임, icacls, 제거 시 설정 삭제 여부.
  make-icon.ps1          아이콘(.ico) 생성. 결과는 src/BlueApex/Assets/.

  Drawer/                "홈 화면 + 서랍" 기능의 중심
    DrawerManager.cs     ★ 상태의 주인. 어떤 아이콘이 꺼내져 있고 숨겨져 있는지, 구역·규칙, 저장, 1초 폴링.
                         "규칙대로 지금 정리"는 PlanRules(무엇이 어디로 갈지 계산만) → ApplyRules(실행) 두 단계.
                         포털로 가는 파일은 SHFileOperation(FO_MOVE, 되돌리기 가능, 이름 겹치면 번호)으로 실제로 옮긴다.
                         앱이 숨긴 파일은 옮기기 전에 숨김을 풀어야 한다(안 그러면 새 폴더에서도 숨은 채로 남는다).
    RulesDialog.cs       "규칙대로 지금 정리" 확인 창: 옮길 목록을 보여 주고 "정리"를 눌러야 ApplyRules를 부른다.
    DrawerWindow.cs      서랍 화면(전체 화면 창). 카드·타일·검색·선택·드래그. 상태는 DrawerManager에 물어본다.
    DesktopDock.cs       바탕화면 독: 즐겨찾기, 실행 중인 앱(점 표시), 맨 끝 서랍 버튼. 1.5초마다 창 목록을 다시 본다.
    DockApps.cs          독 즐겨찾기 id(파일·exe·게임·스토어 앱)의 이름·아이콘·실행. 독과 서랍 왼쪽 즐겨찾기가 같이 쓴다.
    DiscordSetup.cs      "디스코드 음성 연결 (고급)" 창: 디스코드 개발자 앱 만드는 법 안내와 Client ID·Secret 입력.
    SpotifySetup.cs      "Spotify 연결 (고급)" 창: Spotify 개발자 앱 만드는 법 안내, Client ID 입력, 연결/연결 해제.
    SpotifyPicker.cs     독의 Spotify 메뉴가 띄우는 목록 창 하나(검색 결과·내 재생목록·좋아요 곡). 줄을 누르면 바로 실행.
    Hotkey.cs            전역 단축키 등록.
    IconLoader.cs        셸에서 아이콘 이미지 뽑기.

  Desktop/               Windows 바탕화면과 직접 맞닿는 층
    DesktopCatalog.cs    ★ 바탕화면 폴더 두 곳을 읽고, 숨김 속성을 켜고 끄고, 공용 바탕화면 권한을 요청.
    NameOrder.cs         이름 정렬(A→Z 먼저, 그다음 가나다). 구역 정렬과 게임 카드가 쓴다.
    RunningApps.cs       작업 표시줄에 보이는 창을 앱별로 묶어 찾기(EnumWindows, AppUserModelID). 독이 쓴다.
    NowPlaying.cs        지금 재생 중인 미디어(Windows 미디어 컨트롤, WinRT). 독의 미디어 칸이 쓴다.
    BackgroundPlayer.cs  "소리만 듣기". 일반 창 하나에 WPF용 WebView2를 넣고, 재생할 땐 창을 숨긴다(숨겨도 소리는 계속 난다).
                         모든 페이지에 넣는 ReportScript가 "소리 나는 재생 시작/상태(제목·아티스트·아트)"를 알려 주고
                         toggle/next/prev를 받는다. 그래서 독은 Windows 미디어 컨트롤을 거치지 않고 이 플레이어를 직접 조작한다
                         (DesktopDock.OwnPlayer). 음소거 재생(유튜브 미리보기)은 무시. 파일은 PlayerPage에 {cmd:'load'}로 넘기고,
                         로컬 파일은 폴더마다 가짜 주소(https://f<해시>-<실행마다 바뀌는 비밀값>.blueapex/, DenyCors)로 연결한다.
                         숨긴 페이지는 사용자 클릭이 없으니 --autoplay-policy=no-user-gesture-required가 필요하다.
                         볼륨(Volume): 내장 브라우저에는 볼륨 조절 기능이 없어서(음소거 IsMuted만 있다) 페이지에 시킨다.
                         페이지마다 있는 __blueapex.volume(v)가 그 페이지에서 재생하는 것(<video>/<audio>, 또는 Spotify 플레이어)의
                         볼륨을 바꾼다. 새 페이지는 100%로 시작하므로 페이지 로드가 끝날 때·재생이 시작될 때마다 다시 보낸다(ApplyVolume).
                         독의 스피커 버튼을 누르면 뜨는 게이지 바(Drawer/VolumePopup.cs)가 값을 정하고, 손을 뗄 때 DrawerManager.PlayerVolume에 저장한다.
                         독은 클릭만 받고 끌기는 독 이동에 쓰여서, 끌 수 있는 바는 독 안이 아니라 따로 뜨는 작은 창으로 만들었다.
    DiscordVoice.cs      디스코드 로컬 RPC(이름 있는 파이프): 음성 채널·사람·말하는 중·마이크/헤드셋. 사용자 본인 디스코드 앱으로 인증.
    Spotify.cs           Spotify Web API: 검색, 내 재생목록, 좋아요 곡, "이거 틀어". 소리는 내지 않는다
                         (소리는 항상 BackgroundPlayer가 Spotify 기기가 되어 낸다. Spotify 앱은 쓰지 않는다).
                         사용자 본인 Spotify 개발자 앱의 Client ID로 인증(PKCE). 흐름은 3절 "Spotify 연결" 참고.
    ShortcutWriter.cs    게임의 .url 바로가기 만들기. 게임 타일의 "바탕화면에 바로가기 만들기"가 쓴다.
    DesktopIconView.cs   탐색기의 아이콘 뷰(COM). 셸 항목 목록, 위치 읽기/쓰기, 선택 상태. DesktopIcon 레코드(IsGame/IsVirtual)도 여기.
    ShellInterop.cs      위 COM 인터페이스들의 C# 선언(vtable 순서). 손대면 안 되는 파일.
    DesktopHost.cs       탐색기의 바탕화면 창 계층(Progman/DefView) 찾기 + Win32 함수 선언(NativeMethods).
    DesktopFloatWindow.cs ★ 위젯·독을 띄우는 창. 바탕화면 바로 위에 깔린 투명 일반 창(Rainmeter 방식)이라 모서리가 부드럽고
                         배경만 반투명. 클릭·끌기·휠을 직접 받는다. DesktopZOrder가 바탕화면이 앞에 오면 올리고 앱이 오면 내린다.
    DesktopLayerInput.cs 바탕화면 저수준 마우스 훅. 지금은 "바탕화면에 뭔가 떨어졌다"를 알아채 바로 폴링하는 데만 쓴다.
    DesktopPopupMenu.cs  바탕화면 위에서 띄우는 우클릭 메뉴.
    BackupStore.cs       아이콘 위치 스냅샷 저장/복원.

  Games/                 런처(Steam·Epic·GOG)의 게임 목록. 비밀번호는 다루지 않는다.
    GameCatalog.cs       세 런처를 합쳐 읽고, id("game:steam:108600")로 찾고, 실행/설치 URL을 만든다.
    SteamLibrary.cs      설치 게임은 appmanifest(.acf), 보유 게임은 Web API(로그인 세션의 토큰), 그림은 Steam의 캐시 폴더.
    SteamSession.cs      로그인 창 → 쿠키에서 SteamID, 스토어에서 webapi_token. 하루 뒤엔 창 없이 다시 받는다.
    BrowserSession.cs    ★ 내장 브라우저(WebView2, 유일한 NuGet 의존성). 계정 연동의 공통 부품: 창 띄우기, 쿠키 읽기, 페이지 글 가져오기.
    Accounts.cs          계정 카드의 데이터: IAccountSource(Steam/GOG/Epic). 로그인·로그아웃·프로필 가져오기. 새 런처 계정은 여기에 클래스 하나.
    EpicLibrary.cs       설치 게임은 Manifests\*.item, 보유 게임은 런처의 catcache.bin(base64 JSON).
    GogLibrary.cs        레지스트리 GOG.com\Games (설치된 것만).
    BattleNetLibrary.cs  Battle.net: 설치 프로그램 목록의 블리자드 항목(--uid=게임 코드). 실행은 Battle.net.exe --exec="launch 코드".
    HoYoPlayLibrary.cs   HoYoPlay: HKCU\Software\Cognosphere\HYP\1_0\<게임 코드>(원신=hk4e_global)에서 설치 폴더를 읽음.
                         이름은 설치 프로그램 목록의 "--uninstall_game=코드" 항목에서. 실행은 공식 바로가기와 같은
                         launcher.exe --game=코드 — 런처가 업데이트·로그인을 계속 맡게 하려고 게임 exe를 직접 켜지 않음.
    ShortcutLibraries.cs STOVE(sgup://)·Google Play 게임(googleplaygames://): 런처가 만든 바로가기를 찾아 그대로 실행.
    Vdf.cs               Valve 텍스트 KeyValues(.vdf/.acf) 파서.
    ImageCache.cs        타일 그림을 내려받아 %AppData%\BlueApex\cache\games에 둔다. 파일 수정 시각 = 마지막으로 서버와
                         비교한 때. RefreshStale이 오래된 것을 다시 받아 내용이 바뀐 것만 알려 주고, Prune이 오래 안 쓴 것을 지운다.
                         그림이 바뀌었는지는 DrawerManager.RescanGamesAsync가 GameCatalog.ArtStamp(경로·주소·파일 시각)를
                         이전 검색과 비교해 판단한다. 서랍·위젯은 그림을 게임 id별로 기억하므로(IconLoader), 바뀐 게임만
                         IconLoader.Forget으로 잊게 해야 새 그림이 보인다.

  Ui/                    생김새. 화면 코드는 여기 것만 가져다 쓴다 (docs/design.md)
    Theme.cs             색·모서리·간격·글자 크기 토큰. 강조색은 Windows 설정에서 읽는다.
    Styles.xaml          Button/TextBox/CheckBox/ContextMenu/MenuItem/Separator의 암시적 스타일(앱 전체 자동 적용).
    Parts.cs             카드·타일·묶음 머리줄·작은/큰 버튼·드롭 알약·불투명 패널·대화상자 부품.

  Widgets/
    WidgetHost.cs        위젯 인스턴스 생성·이동·삭제. 플러그인에 주는 IWidgetContext 구현도 여기.
    WidgetRegistry.cs    내장 + 플러그인 DLL에서 위젯 종류 찾기.
    BuiltInWidgets.cs    시계·메모·"(없는 위젯)" 자리표시.
    SpotifyWidget.cs     Spotify 위젯: 탭(재생목록·좋아요·검색) + 표지가 있는 스크롤 목록. 재생목록을 누르면 그 안의 곡 목록으로 들어간다.
                         위젯은 마우스 이벤트를 직접 못 받고 "(x, y)를 눌렀다"만 전달받으므로, 맨 위의 배치 상수(TabsH, RowH...)로
                         그림도 그리고 OnClickAt에서 무엇을 눌렀는지도 계산한다. 데이터·재생은 전부 Desktop/Spotify.cs에 맡긴다.
    AccountWidget.cs     런처 계정 카드 위젯(아바타·테두리·배경·상태). 데이터는 Games/Accounts.cs.
    ZoneWidget.cs        구역/게임 격자 위젯. IInternalWidget(클릭 위치·자기 크기)은 내장 전용이라 SDK에 없다. 서랍은 DrawerManager.RequestWidget으로 요청만 한다.
    WidgetSpec.cs        저장 형식(종류, 위치, 크기, 설정 문자열).

  Zones/                 구역 데이터
    Zone.cs, LayoutStore.cs, ZoneRules.cs   구역 모델, layout.json, 자동 분류 규칙
    ZoneMenu.cs                              입력 대화상자(AskText). 이름은 옛 바탕화면 구역 시절의 흔적

src/BlueApex.Sdk/WidgetSdk.cs   플러그인 계약 네 가지(IWidgetProvider, IWidget, IWidgetContext, WidgetMenuItem)
samples/                        플러그인 예제(날씨, 미디어)
tools/DesktopProbe/             진단 도구. 창 구조와 아이콘 목록 출력. 아무것도 바꾸지 않는다.
```

★ 표시 세 파일이 핵심이다. 이 셋을 이해하면 나머지는 부품이다.

## 2. 시작부터 종료까지 (App.xaml.cs)

```
OnStartup
  --exit 인자면: 실행 중인 인스턴스에 "종료" 신호를 보내고 끝
  뮤텍스로 중복 실행 방지
  DrawerManager 생성        → 바탕화면 스캔, 백업, 기본 구역 보장, 꺼내지 않은 파일 숨김, 폴링 시작
  DrawerWindow 생성         → 아직 안 보임
  Hotkey 등록               → 누르면 ToggleDrawer
  DesktopHost.Find()        → 성공하면 DesktopLayerInput(훅) + DesktopDock + WidgetHost
                              실패하면(구조가 다른 Windows) 버튼·위젯 없이 계속
  트레이 아이콘·메뉴 구성
  전역 예외 처리기 등록       → 예외는 로그, 앱은 계속; 진짜 죽을 땐 숨김 풀기 시도
OnExit
  훅 해제, 위젯·버튼·창 정리, DrawerManager.Dispose() → UnhideAll()
```

## 3. 주요 흐름

**시작 시 숨기기** — `DrawerManager` 생성자 → `Reconcile()`: 모든 아이콘에 대해 꺼내진 것은 `Unhide`, 아니면 `Hide`.
`Hide`는 `DesktopCatalog.SetHidden(path, true)`로 파일에 Hidden 속성을 켜고, 성공하면 `HiddenByApp` 목록에 적는다(나중에 풀기 위해).
권한이 없어 실패하면 `_cannotHide`에 넣고 "항상 바탕화면에 있는 항목"으로 취급한다.

**서랍 열기** — 단축키/버튼/트레이 → `App.ToggleDrawer` → 전체 화면 앱이 앞에 있으면 무시 → `DrawerWindow.Open()` →
`Refresh()`가 `DrawerManager.Zones`와 `IconsOf(zone)`를 읽어 카드와 타일을 **매번 새로** 만든다(상태를 창이 따로 들고 있지 않음).

**아이콘 꺼내기/넣기** — 타일 우클릭 또는 드롭 영역 → `DrawerManager.Pin(id)` / `Unpin(id)` → 목록 갱신 + 속성 변경 + 저장 →
`Changed` 이벤트 → 서랍이 열려 있으면 `Refresh()`.

**새 파일이 바탕화면에 생김** — 1초마다 `Poll()`: `DesktopCatalog.Scan`으로 현재 목록을 얻어 직전 목록과 비교.
새 항목이면 규칙(`ZoneRules.Target`)으로 구역을 정하고(없으면 기본 구역) 숨긴 뒤 트레이 알림. 사라진 항목은 모든 목록에서 제거.

**새 폴더 / 폴더 포털** — 카드 우클릭 "새 폴더 만들기" → `DrawerManager.CreateFolder(zone, name)`: `DesktopCatalog.CreateDesktopFolder`로 바탕화면에 만들고,
`Refresh()`로 목록에 먼저 넣은 뒤(폴링이 "새 항목"으로 오해하지 않도록) 구역에 추가하고 숨긴다.
포털은 `Zone.PortalPath`가 있는 구역이다. `IconsOf(zone)`가 멤버 대신 `DesktopCatalog.EnumerateFolder(path)`를 돌려주고,
서랍은 `IsDesktopItem(id)`가 false인 타일(포털 파일)에는 끌기·꺼내기·구역 이동을 주지 않는다. 포털은 규칙·기본 구역 대상에서도 빠지고, 드롭 대상도 아니다(`MoveToZone`이 무시하고 카드도 드롭을 받지 않는다).

**게임 카드** — `DrawerManager.Games`는 `GameCatalog.Scan()`을 백그라운드에서 돌려 채운다. 서랍은 `BuildGamesCard`로
카드를 만들고, 포털과 같은 `LazyTiles`로 한 페이지씩 타일을 만든다. 검색어가 있으면 `LazyTiles.BuildMatching`이 아직 안 만든 타일 중
맞는 것을 즉시 만든다. 게임 타일은 `DesktopIcon.IsGame`으로 구분된다. 구역에 끌어 넣으면 `Zone.Members`에 "game:" id가 그대로 들어가고,
`MembersOf`가 `_gamesById`로 되살린다(파일 없음). "바탕화면에 바로가기 만들기"는 `PinGame` → `ShortcutWriter`로 .url을 만들고
그 파일이 구역에서 게임의 자리를 이어받는다.

**구역 안의 묶음(작은 서랍)** — `Zone.SortMode`가 "manual"이 아니면 `BuildCard`가 `BuildSections`를 부른다. `GroupIcons`가 기준별로
(글자 `Initial`, 날짜 `LastWrite` 버킷, 종류 `Kind`) 묶음을 만들고, 묶음마다 접힌 머리 줄 + `LazyTiles`(열 때 처음 만듦)를 둔다.
열림 상태는 `_openSections`에 세션 동안만 기억한다. 검색 시 `ApplyFilter`가 `_sections`의 묶음을 전부 열어 맞는 타일을 만든다.

**위젯** — `WidgetHost`가 `layout.json`의 `Widgets`마다 `WidgetItem`을 만든다. `WidgetItem`은
`provider.Create(context)`로 플러그인 위젯을 얻고, 그 `View`를 `DesktopFloatWindow`에 넣는다. 창이 마우스를 받아 위젯 항목에 넘긴다.
훅이 그 창 영역의 누름을 감지하면 `MoveTo/Moved/Click/RightClick`을 불러 준다.

**Spotify 연결** — 트레이 "Spotify 연결 (고급)..." → `SpotifySetup`이 Client ID를 `DrawerManager.SetSpotifyApp`으로 저장 →
`Spotify.ConnectAsync()`:

1. 무작위 값(verifier)을 만들고 그 해시(challenge)만 주소에 실어 사용자의 **기본 브라우저**로 Spotify 승인 화면을 연다.
2. 그동안 `HttpListener`로 `http://127.0.0.1:48721/`에 아주 작은 웹 서버를 잠깐 연다(관리자 권한 불필요, 이 PC 안에서만 접속 가능).
3. 사용자가 "동의"를 누르면 Spotify가 브라우저를 `http://127.0.0.1:48721/callback?code=...`로 보낸다. 그 code를 우리가 받는다.
4. code + verifier를 Spotify에 보내 토큰 두 개를 받는다. access token(1시간, 메모리에만)과 refresh token(DPAPI로 암호화해 layout.json에).

왜 이렇게 하나: 앱에 비밀번호나 Client secret을 둘 필요가 없다. code를 누가 가로채도 verifier가 없으면 토큰으로 못 바꾼다(PKCE).
주소가 `localhost`가 아니라 `127.0.0.1`이고 포트가 고정인 것은, Spotify가 등록된 Redirect URI와 **글자 그대로 같은 주소**만 받기 때문이다.

그 뒤 API 호출은 전부 `Spotify.SendAsync` 한 곳을 지난다. 토큰이 낡았으면(401) 한 번 새로 받아 다시 시도하고, 오류는
사용자에게 보여 줄 한국어 문장을 담은 `SpotifyException`으로 바꿔 던진다. `SpotifyPicker`는 그 예외만 잡아 상태 줄에 띄운다.
refresh token은 새로 받을 때마다 바뀔 수 있어서 `_tokenLock`으로 한 번에 하나씩만 갱신한다.

**토큰 갱신과 절전·네트워크 끊김** (`Spotify.TokenAsync` 위 주석에 같은 표가 있다):

- 네트워크가 없을 때(노트북을 막 열어 Wi-Fi가 아직 안 붙음): 요청이 실패할 뿐 Spotify 쪽은 그대로다. 저장된 토큰을 건드리지 않고, 다음 시도에 된다.
- Spotify 것이 아닌 응답(호텔 Wi-Fi 로그인 페이지, 502): 마찬가지로 건드리지 않는다.
- 요청은 갔는데 응답을 못 받은 경우: Spotify가 토큰을 새것으로 바꿨는데 우리는 못 받았을 수 있다. 앱에서 막을 방법이 없고,
  다음 갱신 때 `invalid_grant`가 나와 "연결"을 한 번 다시 눌러야 한다(Client ID는 그대로).
- `invalid_grant`만이 "이 로그인은 끝났다"는 뜻이다(6개월 경과, spotify.com에서 권한 제거 포함). Spotify가 재시도하지 말라고 하므로 토큰을 버린다.
- 만료 여부는 벽시계(`DateTime.UtcNow`)로 본다. 그래야 노트북이 자고 있던 시간이 계산에 들어간다.
- 토큰을 파일에 못 써도(디스크 가득 등) 재생은 실패시키지 않는다(`Keep`). 값은 메모리에 있고 다음 저장 때 같이 써진다.
- "연결 해제" 직전에 출발한 갱신 응답이 뒤늦게 도착해 로그인을 되살리지 않도록 `_signIn` 번호로 걸러 낸다.
- Spotify는 2026년부터 로그인을 "연결"한 날로부터 **6개월** 뒤에 끝낸다(갱신해도 연장 안 됨). 그래서 연결한 날을
  `LayoutFile.SpotifyLinkedAt`에 적어 두고 연결 창에 끝나는 날을 보여 준다.

**layout.json 저장** (`LayoutStore.Save`) — 옆 파일(`layout.json.tmp`)에 쓰고 디스크에 내려쓴 뒤 이름을 바꿔 끼운다. 제자리에 덮어쓰면 쓰는 도중
전원이 나갔을 때(노트북 배터리) 반쪽 파일이 남고, 그러면 빈 레이아웃으로 시작하게 된다. 이름 바꾸기는 "됐거나 안 됐거나" 둘 중 하나다.
Spotify 토큰 갱신이 재생 중 한 시간마다 이 저장을 부르게 되면서 이 위험이 커졌다.
"재생"(`Spotify.StartAsync`)의 길은 하나뿐이다: 내장 플레이어를 (꺼져 있으면) 켜고, 그 기기 id로 재생을 보낸다. Spotify 앱은 켜지도 쓰지도 않는다.
처음엔 "켜진 기기가 있으면 거기, 없으면 내장, 그것도 안 되면 Spotify 앱"으로 골랐는데, 내장 플레이어가 실패한 순간 앱이 켜지고
뒤늦게 내장도 등록되면서 둘이 같은 재생을 두고 다퉜다. 그래서 고르는 로직 자체를 없앴다.

**내장 Spotify 플레이어 (앱 없이 재생)** — `BackgroundPlayer.StartSpotifyDeviceAsync`. "소리만 듣기"의 숨은 브라우저를 Spotify의 Web Playback SDK로
"BlueApex"라는 Spotify 재생 기기로 만든다. 그러면 Spotify 쪽에서는 휴대폰·스피커와 똑같은 기기 하나로 보이고, `Spotify.cs`는 그 기기 id로 재생을 보낼 뿐이다.

- 페이지(`SpotifyPage`)는 파일이 아니라 `https://spotify.blueapex/`라는 가짜 주소로 메모리에서 내준다(`WebResourceRequested`).
  https여야 하는 이유: 보호된 재생(DRM)은 브라우저가 "보안 페이지"로 인정한 곳에서만 된다.
- SDK가 토큰이 필요하면 페이지가 `sp-token`을 보내고 C#이 `{cmd:'token'}`으로 답한다. 기기가 생기면 `sp-ready`(기기 id), 문제는 `sp-error`.
- **`sp-`로 시작하는 메시지는 우리 페이지가 보낸 것만 받는다**(`OnSpotifyPage`). 이 브라우저는 유튜브 등 아무 사이트나 여는 곳이라,
  이 확인이 없으면 다른 사이트가 `sp-token`을 보내 Spotify 토큰을 받아 갈 수 있다. 토큰을 보내기 직전에도 지금 페이지가 우리 것인지 다시 본다.
- 재생 상태는 다른 재생과 같은 `state` 메시지, 버튼은 같은 `window.__blueapex`라서 독의 미디어 칸이 그대로 보여 주고 조작한다.
- 유튜브·파일 등 다른 것을 틀면 페이지가 바뀌므로 기기는 사라진다(`NavigationStarting`). 다음에 Spotify를 틀면 다시 만든다.
- 로그인·계정 오류(`sp-error`)가 나면 페이지를 떠난다(`StopSpotifyPage`). SDK는 그런 오류 뒤에도 "준비됨"을 보낼 수 있는데,
  그 기기로 재생을 보내면 곡마다 실패하며 재생목록을 줄줄이 넘겨 버린다. 같은 이유로 재생 오류가 3초 안에 3번 이어지면 멈추고 알림 창을 띄운다.
- 기억해 둔 기기 번호는 낡을 수 있다. 절전·인터넷 끊김으로 한동안 연결이 없으면 Spotify가 그 기기를 잊고, 그 번호로 재생을 보내면
  "404 Device not found"가 난다(0.8.0의 버그). 그래서 `Spotify.StartAsync`는 404를 둘로 나눠 다룬다: 방금 만든 기기면 서버에 아직
  안 알려진 것이니 잠깐 기다려 다시 보내고, 전부터 기억하던 기기면 `StartSpotifyDeviceAsync(fresh: true)`로 기기를 새로 만들어 거기로 보낸다.
  또 SDK가 재접속용 토큰을 달라고 했는데 못 준 경우(`SendSpotifyTokenAsync` 실패)에도 번호를 버려서, 다음 재생 때 새로 만들게 한다.
- `SpotifyPicker.Pick`은 `_busy`로 한 번에 하나만 보낸다. 더블클릭이 "재생"을 두 번 보내 플레이어 시작이 겹친 적이 있다.

Spotify는 심사받지 않은 앱을 소유자와 등록된 5명만 쓰게 한다(2026년 2월 규정). 그래서 Client ID를 코드에 넣지 않고 각자 입력하게 했고,
입력하지 않은 사용자에게는 독 메뉴에 Spotify 항목이 아예 나오지 않는다(`Spotify.Configured`).

## 4. 지켜야 할 규칙 (왜 이렇게 돼 있는지)

- **훅 콜백 안에서는 아무것도 하지 않는다.** `DesktopLayerInput.HookCallback`은 좌표 비교만 하고 실제 일은
  `_dispatcher.BeginInvoke(...)`로 넘긴다. 훅 안에서 COM 호출을 하면 Windows가 거부하고(RPC_E_CANTCALLOUT_ININPUTSYNCCALL),
  예외가 새어 나가면 프로세스가 죽으면서 마우스가 멈춘다. 그래서 전체가 try/catch로 싸여 있다.
- **아이콘 위치는 건드리지 않는다.** 숨김 속성만 쓴다. 옛 방식(좌표를 화면 밖으로)은 탐색기가 되돌려서 포기했고, 그 코드는 0.4.1에서 지웠다.
- **상태는 `DrawerManager` 한 곳에.** 창·버튼·위젯은 묻고 요청만 한다. 저장은 `Save()` 한 함수.
- **위젯·독은 바탕화면(Progman) 바로 위의 일반 창이다.** 예전엔 Progman의 자식 창으로 아이콘 아래에 그렸는데, 거기서는 창 전체 투명도와
  각진 영역만 돼서 모서리가 계단처럼 보였다. 지금 창은 앱 창들 아래에 깔려 있고, Win+D 등으로 바탕화면이 앞에 오면 `DesktopZOrder`가 같이 올린다.
- **종료 경로는 하나.** 트레이 "종료"든 `--exit`든 로그오프든 `Shutdown()` → `OnExit` → `UnhideAll()`. 강제 종료만 이 길을 건너뛴다.
- **색과 크기는 `Ui/`에만 쓴다.** 화면 코드에 `Color.FromArgb(...)`나 `FontSize = 12` 같은 값이 직접 들어가면
  나중에 디자인을 바꿀 때 파일마다 찾아다녀야 한다. `Theme` 토큰과 `Parts` 부품만 쓰면 한 곳만 고치면 된다. 자세한 것은 `docs/design.md`.

## 5. 이 코드에 나오는 C# 문법

| 보이는 것 | 뜻 | 예 |
|---|---|---|
| `internal sealed class X` | 이 프로젝트 안에서만 쓰는, 상속 못 하는 클래스. 거의 모든 클래스가 이렇다. | |
| `record DesktopIcon(string Id, ...)` | 값 묶음. 생성자·비교·`with`가 자동으로 생긴다. 불변. | `new DesktopIcon(path, name, 0, 0, false, hidden)` |
| `(int Width, int Height)` | 이름 붙은 튜플. 함수가 값 두 개를 돌려줄 때. | `var (w, h) = _desktop.IconAreaSize;` |
| `string? x`, `x?.Foo()`, `x ?? y` | null일 수 있음 / null이면 건너뜀 / null이면 y. | `_session?.TryPauseAsync()` |
| `is { } icon` | "null이 아니면 icon이라는 이름으로 받는다". | `if (Find(p) is { } zone) ...` |
| `x switch { ... }` | 값에 따라 분기해서 값을 만든다. | `code switch { 0 => "맑음", ... }` |
| `=>` (식 본문) | 한 줄짜리 함수/속성. `{ return ...; }`의 줄임. | `public bool IsPinned(string id) => _file.Pinned.Contains(id);` |
| `(_, _) => ...`, `e => ...` | 람다(익명 함수). `_`는 "안 쓰는 인자". 이벤트 핸들러에 많다. | `menu.Items.Add("종료", null, (_, _) => Shutdown());` |
| `event Action? Changed;` / `Changed?.Invoke()` | 이벤트 선언 / 구독자가 있으면 호출. | `_drawer.Changed += () => Refresh();` |
| `async Task`, `await` | 비동기. `await` 뒤는 **다른 스레드에서 이어질 수 있다** → UI는 `Dispatcher`로. | `MediaWidget.RefreshAsync` |
| `using var x = ...` | 블록 끝에서 자동으로 `Dispose()`. | `using var key = Registry...` |
| `.Where(...).Select(...).ToList()` | LINQ. 컬렉션을 파이썬의 리스트 컴프리헨션처럼 다룬다. | `icons.Where(i => !IsParked(i)).ToList()` |
| `[DllImport("user32.dll")] static extern ...` | Windows DLL의 C 함수를 그대로 부른다(ctypes와 같음). | `NativeMethods` |
| `[ComImport, Guid(...)] interface IFolderView2 { ... }` | COM 인터페이스 선언. **메서드 순서가 vtable 순서**라 바꾸면 엉뚱한 함수가 불린다. `_Foo()`는 자리만 채우는 가짜. | `ShellInterop.cs` |
| `Marshal.FreeCoTaskMem(pidl)` | 셸이 준 메모리는 우리가 해제해야 한다. `try/finally`로 반드시. | `DesktopIconView` |
| `Dispatcher.BeginInvoke(f)` | f를 UI 스레드 큐에 넣고 바로 돌아온다. `Invoke`는 끝날 때까지 기다린다. | 훅·네트워크 결과 반영 |
| `DispatcherTimer` | UI 스레드에서 도는 타이머(폴링, 시계). | `DrawerManager._poll` |

## 6. 어디를 고치면 되나

| 하고 싶은 것 | 파일 |
|---|---|
| 트레이 메뉴 항목 추가 | `App.xaml.cs` (`menu.Items.Add`) |
| 서랍 모양·동작 | `DrawerWindow.cs` (`BuildCard`, `BuildTile`, `BuildTileMenu`) |
| 꺼내기/숨기기 규칙 | `DrawerManager.cs` (`Reconcile`, `Poll`, `Hide`, `Unhide`) |
| 자동 분류 패턴 문법 | `Zones/ZoneRules.cs` |
| 포털에 보이는 항목·개수·정렬 | `Desktop/DesktopCatalog.cs` (`EnumerateFolder`) |
| 카드 우클릭 메뉴 | `DrawerWindow.cs` (`BuildCard`의 `menu`) |
| 저장 형식(새 필드) | `Zones/LayoutStore.cs`의 `LayoutFile` (필드만 추가하면 JSON에 자동 반영) |
| 내장 위젯 추가 | `Widgets/BuiltInWidgets.cs` + `WidgetRegistry` 생성자에 `Register` 한 줄 |
| 런처 추가(예: Ubisoft) | `Games/`에 `XxxLibrary.Scan()` 하나 만들고 `GameCatalog.Scan`·`Launch`·`SourceLabel`에 한 줄씩 |
| 새 서비스 계정 연동 | `SteamSession.cs`를 본떠 `XxxSession.cs`: `BrowserSession.OpenAsync` → 로그인 URL → 쿠키/토큰 확인 |
| 게임 카드 메뉴·배치 | `DrawerWindow.cs` (`BuildGamesCard`, `LinkSteam`) |
| 독의 Spotify 메뉴 항목 추가 | `Desktop/Spotify.cs`에 API 호출 하나 + `Drawer/SpotifyPicker.cs`에 목록 하나 + `DesktopDock.RightClick`에 한 줄 |
| 독 모양·슬롯 크기 | `Drawer/DesktopDock.cs` (`Draw`, `AppSlot`, 상수 `Slot`·`IconSize`) |
| 색·글자 크기·모서리 값 | `Ui/Theme.cs` (토큰 하나 = 그 값을 쓰는 모든 곳) |
| 버튼·입력창·메뉴 생김새 | `Ui/Styles.xaml` (컨트롤 종류별 블록) |
| 카드·타일·묶음 머리줄 구조 | `Ui/Parts.cs` (`Card`, `Tile`, `GroupHeader`, `Pill`, `Panel`, `Dialog`) |
| 단축키 기본값 | `LayoutFile.Hotkey` |
| 새 Win32 함수 | `DesktopHost.cs`의 `NativeMethods` |

## 7. 읽는 순서 제안

1. `App.xaml.cs` 전체 (조립)
2. `DrawerManager.cs` 위에서 아래로 (상태와 규칙)
3. `DesktopCatalog.cs` (숨김의 실체)
4. `DrawerWindow.cs`는 `Refresh` → `BuildCard` → `BuildTile`만
5. `DesktopLayerInput.cs`의 `HookCallback` (왜 그렇게 조심스러운지 4절 참고)
6. 필요할 때만 `ShellInterop.cs`, `DesktopIconView.cs`
