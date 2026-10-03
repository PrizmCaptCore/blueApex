# BlueApex 위젯 SDK 레퍼런스

어셈블리 `BlueApex.Sdk.dll`, 네임스페이스 `BlueApex.Sdk`. 플러그인은 이 어셈블리만 참조한다.

## 빠른 시작

```csharp
using System.Windows;
using System.Windows.Controls;
using BlueApex.Sdk;

public sealed class HelloProvider : IWidgetProvider
{
    public string Id => "hello";
    public string DisplayName => "인사";
    public (int Width, int Height) DefaultSizeDip => (200, 80);
    public IWidget Create(IWidgetContext context) => new HelloWidget(context);
}

sealed class HelloWidget : IWidget
{
    public HelloWidget(IWidgetContext context) =>
        View = new Border { Background = System.Windows.Media.Brushes.Black, Child = new TextBlock { Text = "안녕", Foreground = System.Windows.Media.Brushes.White } };
    public FrameworkElement View { get; }
    public void Dispose() { }
}
```

프로젝트: `net9.0-windows` + `UseWPF`, `src/BlueApex.Sdk/BlueApex.Sdk.csproj`를 `Private="false"`로 참조.
빌드한 DLL을 `%AppData%\BlueApex\widgets\<이름>\`에 넣고 BlueApex를 다시 시작하면 "위젯 추가"에 나타난다.

## 생명 주기

1. 시작 시 BlueApex가 플러그인 폴더의 DLL을 로드하고, `IWidgetProvider`를 구현한 public 클래스를 **매개변수 없는 생성자**로 하나씩 만든다.
2. 사용자가 "위젯 추가"에서 고르거나 저장된 레이아웃에 있으면 `Create(context)`가 호출된다. 인스턴스마다 `context`가 따로 있다.
3. `View`가 바탕화면 층 창에 넣어져 그려진다. 사용자의 클릭은 `OnClick()`, 우클릭은 `MenuItems`로 온다.
4. 사용자가 위젯을 지우거나 앱이 종료되면 `Dispose()`가 호출된다.

모든 호출은 UI 스레드에서 온다. 다른 스레드에서 UI를 바꾸려면 `IWidgetContext.Dispatcher`를 쓴다.

---

## interface `IWidgetProvider`

위젯 "종류" 하나. 메뉴에 올리고 인스턴스를 만드는 공장.

### `string Id { get; }`

- **용도**: 레이아웃 파일에 저장되는 종류 식별자. 같은 Id의 제공자가 둘이면 나중 것은 무시된다(로그에 남음).
- **값**: 소문자, 공백 없이. 한 번 정하면 바꾸지 않는다(바꾸면 저장된 위젯이 "(없는 위젯)"이 된다).

### `string DisplayName { get; }`

- **용도**: "위젯 추가" 메뉴에 보이는 이름. 바꿔도 된다.

### `(int Width, int Height) DefaultSizeDip { get; }`

- **용도**: 새 인스턴스의 창 크기.
- **단위**: 96 DPI 기준 장치 독립 픽셀. 150% 배율 화면에서는 1.5배 물리 픽셀로 그려진다.
- **참고**: 현재 사용자가 크기를 바꿀 수 없으므로 내용이 들어갈 만큼 넉넉히 잡는다.

### `IWidget Create(IWidgetContext context)`

- **용도**: 인스턴스 생성.
- **매개변수** `context`: 이 인스턴스 전용. 저장해 두고 써도 된다.
- **반환**: `IWidget`. 예외가 나면 BlueApex가 로그에 남기고 "(없는 위젯)" 자리표시로 대체한다.
- **호출 시점**: UI 스레드.

---

## interface `IWidget : IDisposable`

화면에 놓인 위젯 하나.

### `FrameworkElement View { get; }`

- **용도**: 그려질 WPF 요소. `Create` 직후 한 번 읽어 창에 넣으므로 이후 다른 요소로 바꿔도 반영되지 않는다. 내용만 갱신한다.
- **제약**(바탕화면 아이콘 아래 층에 그려지기 때문):
  - 마우스·키보드 입력을 **받지 못한다**. 버튼·텍스트 상자를 넣어도 동작하지 않는다. 클릭은 `OnClick`, 메뉴는 `MenuItems`로 처리한다.
  - 투명도는 창 전체 한 값(현재 88%)이다. 픽셀 단위 투명은 없으므로 **불투명 배경** 위에 그린다. 창 모서리는 BlueApex가 둥글게 자른다(14 DIP).
  - 소프트웨어 렌더링이다. 매 프레임 애니메이션은 피하고, 1초 이하 간격 갱신은 가볍게 유지한다.

### `void OnClick()`

- **용도**: 사용자가 위젯을 끌지 않고 눌렀다 뗐을 때.
- **기본 구현**: 아무것도 안 함(생략 가능).
- **참고**: 예외는 로그에 남고 앱은 계속 실행된다.

### `IEnumerable<WidgetMenuItem> MenuItems { get; }`

- **용도**: 우클릭 메뉴에 들어갈 항목. 메뉴를 열 때마다 읽으므로 상태에 따라 다른 항목을 돌려줘도 된다.
- **반환**: 항목 목록. BlueApex가 뒤에 구분선과 "위젯 삭제"를 붙인다.
- **기본 구현**: 빈 목록(생략 가능).

### `void Dispose()`

- **용도**: 타이머 중지, 네트워크 취소, 이벤트 구독 해제. 호출 뒤에는 `Dispatcher`로 UI를 건드리지 않는다.

---

## record `WidgetMenuItem(string? Label, Action? Action = null)`

우클릭 메뉴 항목 하나.

- `Label`: 표시 문구. `null`이면 구분선.
- `Action`: 선택 시 실행. UI 스레드에서 호출되며 예외는 로그로 격리된다.
- `WidgetMenuItem.Separator`: 구분선 상수.

---

## interface `IWidgetContext`

BlueApex가 위젯 인스턴스에 주는 것.

### `string Settings { get; set; }`

- **용도**: 이 인스턴스의 설정 저장소. 형식은 자유(문자열, JSON 등).
- **읽기**: 새 위젯이면 빈 문자열. 저장된 위젯이면 마지막에 대입한 값.
- **쓰기**: 대입하는 즉시 레이아웃 파일에 저장된다. 자주 바뀌는 값(초 단위 상태)은 넣지 않는다.

### `Dispatcher Dispatcher { get; }`

- **용도**: UI 스레드 디스패처. 타이머·네트워크·시스템 이벤트 결과를 `View`에 반영할 때 `Dispatcher.Invoke`/`BeginInvoke`로 감싼다.
- **참고**: WPF 개체(이미지 포함)는 이 디스패처 안에서 만든다. 다른 스레드에서 만든 개체를 넘기면 "다른 스레드가 소유" 예외가 난다.

### `string? AskText(string title, string current, string? hint = null, bool multiline = false)`

- **용도**: 입력 대화상자. 설정 편집용.
- **매개변수**: `title` 창 제목, `current` 미리 채울 값, `hint` 입력란 위 안내문, `multiline` 여러 줄 입력 여부.
- **반환**: 확인하면 입력한 문자열(앞뒤 공백 포함), 취소하면 `null`.
- **호출 시점**: UI 스레드에서만. 대화상자가 닫힐 때까지 반환하지 않는다.

### `void Log(string message)`

- **용도**: `%AppData%\BlueApex\log.txt`에 한 줄 기록. 위젯 Id가 앞에 붙는다.

---

## 의존성

본체에 없는 API(WinRT 등)를 쓰려면 그 DLL을 플러그인과 **같은 폴더**에 둔다. 본체는 플러그인의 의존성을 플러그인 폴더에서 찾는다.
`samples/MediaWidget`가 `Microsoft.Windows.SDK.NET.dll`/`WinRT.Runtime.dll`을 복사하는 방식을 참고. `BlueApex.Sdk.dll`은 복사하지 않는다(본체 것을 쓴다).

## 설치 위치

- `%AppData%\BlueApex\widgets\<이름>\` (사용자별)
- `<BlueApex.exe 폴더>\widgets\<이름>\`

로드 결과는 로그의 `widget plugin loaded` / `widget plugin skipped` 줄로 확인한다. 플러그인은 BlueApex와 같은 프로세스에서 실행되므로 신뢰할 수 있는 DLL만 넣는다.

## 호환성

SDK는 BlueApex와 함께 버전이 올라간다. 기존 멤버는 바꾸지 않고, 추가는 기본 구현이 있는 멤버로만 한다. 따라서 오래된 플러그인도 새 BlueApex에서 그대로 로드된다.

## 예제

- `samples/WeatherWidget` — 설정(`Settings`), 주기 갱신, 네트워크, 메뉴, 출처 표기
- `samples/MediaWidget` — WinRT API, 시스템 이벤트를 `Dispatcher`로 반영, 클릭 동작, 외부 의존 DLL 동봉
