# BlueApex 위젯 SDK

위젯은 DLL 하나로 추가한다. 앱을 고칠 필요가 없다.

## 만들기

1. `net9.0-windows`, `UseWPF` 클래스 라이브러리를 만들고 `src/BlueApex.Sdk/BlueApex.Sdk.csproj`를 참조한다
   (`Private="false"`로 두어 SDK DLL이 플러그인 폴더에 복사되지 않게 한다).
2. `IWidgetProvider`를 구현하는 public 클래스(매개변수 없는 생성자)를 둔다.
   - `Id`: 레이아웃에 저장되는 고유 이름(소문자, 공백 없음). `DisplayName`: 메뉴에 보이는 이름. `DefaultSizeDip`: 새 위젯 크기.
   - `Create(IWidgetContext)`: 위젯 인스턴스를 만든다.
3. `IWidget`을 구현한다.
   - `View`: WPF `FrameworkElement`. 바탕화면 아이콘 아래 층에 그려지므로
     **마우스·키보드 입력을 직접 받지 못한다**(클릭은 `OnClick`, 우클릭은 `MenuItems`로 온다),
     **투명도는 창 전체 한 값**(픽셀 단위 알파 없음, 불투명 배경 위에 그릴 것),
     소프트웨어 렌더링(과한 애니메이션 금지).
   - `Dispose`: 타이머·네트워크 작업을 멈춘다.
4. `IWidgetContext`로 할 수 있는 것: `Settings`(인스턴스별 문자열, 대입하면 저장), `Dispatcher`(UI 스레드),
   `AskText`(입력 대화상자), `Log`.

예제: `samples/WeatherWidget` (Open-Meteo, 키 불필요).

## 설치

빌드한 DLL(과 그 의존 DLL)을 아래 중 한 곳의 **하위 폴더**에 넣고 BlueApex를 다시 시작한다.

- `%AppData%\BlueApex\widgets\<플러그인 이름>\`
- `<BlueApex.exe 폴더>\widgets\<플러그인 이름>\`

트레이 메뉴 "위젯 추가"에 `DisplayName`이 나타난다. 로드 결과는 `%AppData%\BlueApex\log.txt`에 남는다
(`widget plugin loaded` / `widget plugin skipped`). 플러그인을 지우면 그 위젯 자리는 "(없는 위젯)"으로 남는다.

## 호환성 규칙

- SDK는 BlueApex와 같은 저장소에서 버전이 올라간다. 인터페이스에 기본 구현이 있는 멤버(`OnClick`, `MenuItems`)는 생략해도 된다.
- 플러그인 예외는 로그에 남기고 앱은 계속 실행된다. 뷰 생성 자체가 실패하면 "(없는 위젯)"으로 대체된다.
