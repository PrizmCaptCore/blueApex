using System.Windows;
using BlueApex.Desktop;
using BlueApex.Drawer;
using BlueApex.Zones;

namespace BlueApex.Widgets;

/// <summary>Creates the widgets saved in the layout, and adds, moves and removes them.</summary>
internal sealed class WidgetHost : IDisposable
{
    public static readonly IReadOnlyList<(string Type, string Label)> Kinds = new[] { ("clock", "시계"), ("memo", "메모") };

    private readonly DesktopHost _desktop;
    private readonly DesktopLayerInput _input;
    private readonly DrawerManager _drawer;
    private readonly List<WidgetItem> _items = new();

    public WidgetHost(DesktopHost desktop, DesktopLayerInput input, DrawerManager drawer)
    {
        _desktop = desktop;
        _input = input;
        _drawer = drawer;
        foreach (var spec in _drawer.Widgets.ToList())
            Create(spec);
    }

    public void Add(string type)
    {
        var scale = _desktop.Dpi / 96.0;
        var (w, h) = type == "memo" ? MemoView.DefaultSizeDip : ClockView.DefaultSizeDip;
        var (areaWidth, areaHeight) = _desktop.IconAreaSize;
        var offset = (int)Math.Round(24 * scale) * _drawer.Widgets.Count;
        var spec = new WidgetSpec
        {
            Type = type,
            Width = (int)Math.Round(w * scale),
            Height = (int)Math.Round(h * scale),
        };
        spec.X = Math.Clamp((areaWidth - spec.Width) / 2 + offset, 0, areaWidth - spec.Width);
        spec.Y = Math.Clamp((areaHeight - spec.Height) / 3 + offset, 0, areaHeight - spec.Height);
        _drawer.Widgets.Add(spec);
        _drawer.SaveLayout();
        Create(spec);
    }

    private void Create(WidgetSpec spec)
    {
        var item = new WidgetItem(_desktop, spec, this);
        _items.Add(item);
        _input.Items.Add(item);
    }

    internal void Remove(WidgetItem item)
    {
        _items.Remove(item);
        _input.Items.Remove(item);
        _drawer.Widgets.Remove(item.Spec);
        _drawer.SaveLayout();
        item.Dispose();
    }

    internal void Save() => _drawer.SaveLayout();

    public void Dispose()
    {
        foreach (var item in _items) item.Dispose();
        _items.Clear();
    }
}

/// <summary>One widget on the desktop layer: its window, its view and its mouse behaviour.</summary>
internal sealed class WidgetItem : IDesktopLayerItem, IDisposable
{
    private readonly WidgetHost _host;
    private readonly DesktopLayerWindow _window;
    private readonly FrameworkElement _view;

    public WidgetSpec Spec { get; }

    public WidgetItem(DesktopHost desktop, WidgetSpec spec, WidgetHost host)
    {
        Spec = spec;
        _host = host;
        _view = spec.Type == "memo" ? new MemoView { Text = spec.Text } : new ClockView();
        var corner = (int)Math.Round(WidgetStyle.CornerDip * desktop.Dpi / 96.0);
        _window = new DesktopLayerWindow(desktop, _view, spec.X, spec.Y, spec.Width, spec.Height, 0.88, corner);
    }

    public NativeMethods.RECT Bounds => new() { Left = Spec.X, Top = Spec.Y, Right = Spec.X + Spec.Width, Bottom = Spec.Y + Spec.Height };
    public bool Movable => true;

    public void MoveTo(int x, int y)
    {
        Spec.X = x;
        Spec.Y = y;
        _window.SetBounds(x, y, Spec.Width, Spec.Height);
    }

    public void Moved() => _host.Save();

    public void Click()
    {
        // Widgets have no click action yet; a memo opens its editor for convenience.
        if (_view is MemoView) EditMemo();
    }

    public void RightClick(int screenX, int screenY)
    {
        var items = new List<(string? Label, Action? Action)>();
        if (_view is MemoView) items.Add(("메모 편집...", EditMemo));
        items.Add(("위젯 삭제", () => _host.Remove(this)));
        DesktopPopupMenu.Show(screenX, screenY, items.ToArray());
    }

    private void EditMemo()
    {
        var text = ZoneMenu.AskText("메모", Spec.Text, null, multiline: true);
        if (text == null) return;
        Spec.Text = text.Trim();
        ((MemoView)_view).Text = Spec.Text;
        _host.Save();
    }

    public void Dispose() => _window.Dispose();
}
