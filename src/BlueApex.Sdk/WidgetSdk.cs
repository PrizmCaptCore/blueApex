using System.Windows;
using System.Windows.Threading;

namespace BlueApex.Sdk;

/// <summary>
/// A kind of widget. BlueApex finds every public class implementing this (with a
/// parameterless constructor) in each DLL under <c>%AppData%\BlueApex\widgets\*\</c>
/// and under <c>widgets\</c> next to BlueApex.exe, and lists it in "위젯 추가".
/// </summary>
public interface IWidgetProvider
{
    /// <summary>Stable identifier saved in the layout, e.g. "weather". Lower-case, no spaces.</summary>
    string Id { get; }

    /// <summary>What the user sees in menus, e.g. "날씨".</summary>
    string DisplayName { get; }

    /// <summary>Size of a new instance, in device-independent pixels (96 DPI units).</summary>
    (int Width, int Height) DefaultSizeDip { get; }

    /// <summary>Creates a widget instance. Called on the UI thread; the context is unique to that instance.</summary>
    IWidget Create(IWidgetContext context);
}

/// <summary>
/// One widget on the desktop. Its <see cref="View"/> is drawn on the desktop layer
/// below the icons, which means:
/// <list type="bullet">
/// <item>it never receives mouse or keyboard input of its own; a click arrives as <see cref="OnClick"/>, a right-click as a menu built from <see cref="MenuItems"/>;</item>
/// <item>transparency is one opacity for the whole window (no per-pixel alpha); draw on an opaque background;</item>
/// <item>rendering is software-based, so keep animations modest.</item>
/// </list>
/// Dispose stops timers and network work.
/// </summary>
public interface IWidget : IDisposable
{
    FrameworkElement View { get; }

    /// <summary>Left click (press and release without dragging). Optional.</summary>
    void OnClick() { }

    /// <summary>Extra right-click menu entries, shown above "위젯 삭제".</summary>
    IEnumerable<WidgetMenuItem> MenuItems => Array.Empty<WidgetMenuItem>();
}

/// <summary>A right-click menu entry. A null <paramref name="Label"/> is a separator.</summary>
public sealed record WidgetMenuItem(string? Label, Action? Action = null)
{
    public static readonly WidgetMenuItem Separator = new(Label: null);
}

/// <summary>What BlueApex offers a widget instance.</summary>
public interface IWidgetContext
{
    /// <summary>Free-form settings for this instance (JSON, text...). Assigning saves immediately. Empty for a new widget.</summary>
    string Settings { get; set; }

    /// <summary>The UI thread's dispatcher, for updates coming from timers or network calls.</summary>
    Dispatcher Dispatcher { get; }

    /// <summary>Asks the user for text in a small dialog; null if cancelled. <paramref name="hint"/> is shown above the box.</summary>
    string? AskText(string title, string current, string? hint = null, bool multiline = false);

    /// <summary>Writes a line to BlueApex's log (%AppData%\BlueApex\log.txt).</summary>
    void Log(string message);
}
