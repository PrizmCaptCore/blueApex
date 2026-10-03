# BlueApex 코드 안내

C#을 깊이 몰라도 이 코드를 따라 읽을 수 있도록 쓴 문서다. 전체 약 4,800줄, 그중 지금 실제로 쓰이는 건 절반 정도다.

## 1. 지도

```
src/BlueApex/
  App.xaml.cs            시작·종료, 트레이 메뉴. 모든 부품을 여기서 조립한다.
  Log.cs                 log.txt에 한 줄 쓰기.
  Autostart.cs           로그인 시 자동 실행(레지스트리 Run 키).

  Drawer/                "홈 화면 + 서랍" 기능의 중심
    DrawerManager.cs     ★ 상태의 주인. 어떤 아이콘이 꺼내져 있고 숨겨져 있는지, 구역·규칙, 저장, 1초 폴링.
    DrawerWindow.cs      서랍 화면(전체 화면 창). 카드·타일·검색·선택·드래그. 상태는 DrawerManager에 물어본다.
    DesktopButton.cs     바탕화면의 둥근 버튼.
    Hotkey.cs            전역 단축키 등록.
    IconLoader.cs        셸에서 아이콘 이미지 뽑기.

  Desktop/               Windows 바탕화면과 직접 맞닿는 층
    DesktopCatalog.cs    ★ 바탕화면 폴더 두 곳을 읽고, 숨김 속성을 켜고 끄고, 공용 바탕화면 권한을 요청.
    AppCatalog.cs        설치된 앱 전체 목록(셸 Applications 폴더). id는 "app:..." 형식, 실행은 explorer shell:AppsFolder\id.
    ShortcutWriter.cs    앱의 .lnk 바로가기 만들기(IShellLink에 앱 pidl을 넣음). "바탕화면에 바로가기 만들기"가 쓴다.
    DesktopIconView.cs   탐색기의 아이콘 뷰(COM). 셸 항목 목록, 위치 읽기/쓰기, 선택 상태. DesktopIcon 레코드(IsApp/IsGame/IsVirtual)도 여기.
    ShellInterop.cs      위 COM 인터페이스들의 C# 선언(vtable 순서). 손대면 안 되는 파일.
    DesktopHost.cs       탐색기의 바탕화면 창 계층(Progman/DefView) 찾기 + Win32 함수 선언(NativeMethods).
    DesktopLayerWindow.cs 아이콘 아래 층에 WPF 내용을 그리는 창. 버튼·위젯이 쓴다.
    DesktopLayerInput.cs ★ 그 층의 마우스 처리(저수준 훅 하나). 클릭/끌기/우클릭을 항목에 전달.
    DesktopPopupMenu.cs  바탕화면 위에서 띄우는 우클릭 메뉴.
    BackupStore.cs       아이콘 위치 스냅샷 저장/복원.

  Games/                 런처(Steam·Epic·GOG)의 게임 목록. 비밀번호는 다루지 않는다.
    GameCatalog.cs       세 런처를 합쳐 읽고, id("game:steam:108600")로 찾고, 실행/설치 URL을 만든다.
    SteamLibrary.cs      설치 게임은 appmanifest(.acf), 보유 게임은 Web API(로그인 세션의 토큰), 그림은 Steam의 캐시 폴더.
    SteamSession.cs      로그인 창 → 쿠키에서 SteamID, 스토어에서 webapi_token. 하루 뒤엔 창 없이 다시 받는다.
    BrowserSession.cs    ★ 내장 브라우저(WebView2, 유일한 NuGet 의존성). 계정 연동의 공통 부품: 창 띄우기, 쿠키 읽기, 페이지 글 가져오기.
    EpicLibrary.cs       설치 게임은 Manifests\*.item, 보유 게임은 런처의 catcache.bin(base64 JSON).
    GogLibrary.cs        레지스트리 GOG.com\Games (설치된 것만).
    Vdf.cs               Valve 텍스트 KeyValues(.vdf/.acf) 파서.
    ImageCache.cs        타일 그림을 한 번만 내려받아 %AppData%\BlueApex\cache\games에 둔다.

  Ui/                    생김새. 화면 코드는 여기 것만 가져다 쓴다 (docs/design.md)
    Theme.cs             색·모서리·간격·글자 크기 토큰. 강조색은 Windows 설정에서 읽는다.
    Styles.xaml          Button/TextBox/CheckBox/ContextMenu/MenuItem/Separator의 암시적 스타일(앱 전체 자동 적용).
    Parts.cs             카드·타일·묶음 머리줄·작은/큰 버튼·드롭 알약·불투명 패널·대화상자 부품.

  Widgets/
    WidgetHost.cs        위젯 인스턴스 생성·이동·삭제. 플러그인에 주는 IWidgetContext 구현도 여기.
    WidgetRegistry.cs    내장 + 플러그인 DLL에서 위젯 종류 찾기.
    BuiltInWidgets.cs    시계·메모·"(없는 위젯)" 자리표시.
    WidgetSpec.cs        저장 형식(종류, 위치, 크기, 설정 문자열).

  Zones/                 구역 데이터 + 옛 바탕화면 구역 방식
    Zone.cs, LayoutStore.cs, ZoneRules.cs   ← 지금도 쓴다 (구역 모델, layout.json, 자동 분류 규칙)
    ZoneMenu.cs                              ← 입력 대화상자(AskText)만 쓴다
    ZoneManager/ZoneSurface/ZoneLayout/ZoneMouseInteraction/ZoneStyle*/ZoneView  ← 안 쓴다 (Fences식 옛 방식)

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
  DesktopHost.Find()        → 성공하면 DesktopLayerInput(훅) + DesktopButton + WidgetHost
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
서랍은 `IsDesktopItem(id)`가 false인 타일(포털 파일)에는 끌기·꺼내기·구역 이동을 주지 않는다. 포털은 규칙·기본 구역 대상에서도 빠진다(`RuleZones`).

**설치된 앱 카드** — `DrawerManager.Apps`는 `AppCatalog.Scan()`(약 0.5초)을 백그라운드에서 돌려 채운다. 서랍은 `BuildAppsCard`로
카드를 만들고, 포털과 같은 `LazyTiles`로 한 페이지씩 타일을 만든다. 검색어가 있으면 `LazyTiles.BuildMatching`이 아직 안 만든 타일 중
맞는 것을 즉시 만든다. 앱 타일은 `DesktopIcon.IsApp`으로 구분된다. 구역에 끌어 넣으면 `Zone.Members`에 "app:" id가 그대로 들어가고,
`MembersOf`가 `_appsById`로 되살린다(파일 없음). "바탕화면에 바로가기 만들기"는 `PinApp` → `ShortcutWriter`로 .lnk를 만들고
그 파일이 구역에서 앱의 자리를 이어받는다.

**구역 안의 묶음(작은 서랍)** — `Zone.SortMode`가 "manual"이 아니면 `BuildCard`가 `BuildSections`를 부른다. `GroupIcons`가 기준별로
(글자 `Initial`, 날짜 `LastWrite` 버킷, 종류 `Kind`) 묶음을 만들고, 묶음마다 접힌 머리 줄 + `LazyTiles`(열 때 처음 만듦)를 둔다.
열림 상태는 `_openSections`에 세션 동안만 기억한다. 검색 시 `ApplyFilter`가 `_sections`의 묶음을 전부 열어 맞는 타일을 만든다.

**위젯** — `WidgetHost`가 `layout.json`의 `Widgets`마다 `WidgetItem`을 만든다. `WidgetItem`은
`provider.Create(context)`로 플러그인 위젯을 얻고, 그 `View`를 `DesktopLayerWindow`에 넣고, 자신을 `DesktopLayerInput.Items`에 등록한다.
훅이 그 창 영역의 누름을 감지하면 `MoveTo/Moved/Click/RightClick`을 불러 준다.

## 4. 지켜야 할 규칙 (왜 이렇게 돼 있는지)

- **훅 콜백 안에서는 아무것도 하지 않는다.** `DesktopLayerInput.HookCallback`은 좌표 비교만 하고 실제 일은
  `_dispatcher.BeginInvoke(...)`로 넘긴다. 훅 안에서 COM 호출을 하면 Windows가 거부하고(RPC_E_CANTCALLOUT_ININPUTSYNCCALL),
  예외가 새어 나가면 프로세스가 죽으면서 마우스가 멈춘다. 그래서 전체가 try/catch로 싸여 있다.
- **아이콘 위치는 건드리지 않는다.** 숨김 속성만 쓴다. 옛 방식(좌표를 화면 밖으로)은 탐색기가 되돌려서 포기했다. `Zones/`의 옛 코드가 그 흔적이다.
- **상태는 `DrawerManager` 한 곳에.** 창·버튼·위젯은 묻고 요청만 한다. 저장은 `Save()` 한 함수.
- **바탕화면 층 창은 세 가지 제약**이 있다(`DesktopLayerWindow` 주석): 레이어드 자식 창만 보임, 창 전체 투명도만 됨, 소프트웨어 렌더링.
  이건 Windows 11 24H2+의 바탕화면 구조 때문이고 실험으로 확인한 사실이다.
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
| 바탕화면 버튼 모양 | `Drawer/DesktopButton.cs` (`BuildFace`) |
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
