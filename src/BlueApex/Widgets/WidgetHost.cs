using System.Windows.Threading;
using BlueApex.Desktop;
using BlueApex.Drawer;
using BlueApex.Sdk;
using BlueApex.Zones;

namespace BlueApex.Widgets;

/// <summary>Creates the widgets saved in the layout, and adds, moves and removes them.</summary>
internal sealed class WidgetHost : IDisposable
{
    private readonly DesktopHost _desktop;
    private readonly DesktopLayerInput _input;
    private readonly DrawerManager _drawer;
    private readonly WidgetRegistry _registry;
    private readonly List<WidgetItem> _items = new();

    public WidgetHost(DesktopHost desktop, DesktopLayerInput input, DrawerManager drawer)
    {
        _desktop = desktop;
        _input = input;
        _drawer = drawer;
        _registry = new WidgetRegistry(drawer);
        foreach (var spec in _drawer.Widgets.ToList())
            Create(spec);
        // The drawer asks for a widget (e.g. "open this zone on the desktop") without knowing about us.
        _drawer.WidgetRequested += (type, settings) => Add(_registry.Resolve(type), settings);
    }

    /// <summary>Widget kinds the user can add from the tray menu: built-in and plugins, minus those that need a card to come from.</summary>
    public IEnumerable<IWidgetProvider> Kinds => _registry.Providers.Where(p => p is not ZoneWidgetProvider);

    public void Add(IWidgetProvider provider, string settings = "")
    {
        var scale = _desktop.Dpi / 96.0;
        var (areaWidth, areaHeight) = _desktop.IconAreaSize;
        var offset = (int)Math.Round(24 * scale) * _drawer.Widgets.Count;
        var spec = new WidgetSpec
        {
            Type = provider.Id,
            Text = settings,
            Width = (int)Math.Round(provider.DefaultSizeDip.Width * scale),
            Height = (int)Math.Round(provider.DefaultSizeDip.Height * scale),
        };
        spec.X = Math.Clamp((areaWidth - spec.Width) / 2 + offset, 0, Math.Max(0, areaWidth - spec.Width));
        spec.Y = Math.Clamp((areaHeight - spec.Height) / 3 + offset, 0, Math.Max(0, areaHeight - spec.Height));
        _drawer.Widgets.Add(spec);
        _drawer.SaveLayout();
        Create(spec);
    }

    private void Create(WidgetSpec spec)
    {
        var provider = _registry.Resolve(spec.Type);
        WidgetItem item;
        try
        {
            item = new WidgetItem(_desktop, spec, provider, this);
        }
        catch (Exception ex)
        {
            // A plugin that throws while creating its view must not take the app down.
            Log.Write($"widget '{spec.Type}' failed to create: {ex}");
            item = new WidgetItem(_desktop, spec, new MissingProvider(spec.Type), this);
        }
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

/// <summary>One widget on the desktop layer: its window, the plugin's widget and the mouse behaviour.</summary>
internal sealed class WidgetItem : IDesktopLayerItem, IDesktopLayerScrollable, IDisposable
{
    private readonly WidgetHost _host;
    private readonly DesktopLayerWindow _window;
    private readonly IWidget _widget;
    private readonly double _scale;

    public WidgetSpec Spec { get; }

    public WidgetItem(DesktopHost desktop, WidgetSpec spec, IWidgetProvider provider, WidgetHost host)
    {
        Spec = spec;
        _host = host;
        _scale = desktop.Dpi / 96.0;
        _widget = provider.Create(new Context(spec, host));
        if (_widget is IInternalWidget sized)
        {
            // Built-in widgets size themselves to their content.
            var size = ToPixels(sized.DesiredSizeDip);
            if (size != (spec.Width, spec.Height))
            {
                (spec.Width, spec.Height) = size;
                host.Save();
            }
            sized.Resized += () => Resize(ToPixels(sized.DesiredSizeDip));
        }
        var corner = (int)Math.Round(WidgetStyle.CornerDip * _scale);
        _window = new DesktopLayerWindow(desktop, _widget.View, spec.X, spec.Y, spec.Width, spec.Height, 0.88, corner);
    }

    private (int Width, int Height) ToPixels((double Width, double Height) dip) =>
        ((int)Math.Round(dip.Width * _scale), (int)Math.Round(dip.Height * _scale));

    private void Resize((int Width, int Height) size)
    {
        if (size == (Spec.Width, Spec.Height)) return;
        (Spec.Width, Spec.Height) = size;
        _window.SetBounds(Spec.X, Spec.Y, Spec.Width, Spec.Height);
        _host.Save();
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

    public void Click(int x, int y)
    {
        if (_widget is IInternalWidget positional) Guard(() => positional.OnClickAt(x / _scale, y / _scale));
        else Guard(_widget.OnClick);
    }

    public bool ScrollsAt(int x, int y) => _widget is IInternalWidget w && w.ScrollsAt(x / _scale, y / _scale);

    public void ScrollBy(int deltaY)
    {
        if (_widget is IInternalWidget w) Guard(() => w.ScrollBy(deltaY / _scale));
    }

    public void Wheel(int notches)
    {
        if (_widget is IInternalWidget w) Guard(() => w.Wheel(notches));
    }

    public void RightClick(int screenX, int screenY)
    {
        var items = new List<(string? Label, Action? Action)>();
        foreach (var entry in _widget.MenuItems)
            items.Add((entry.Label, entry.Action == null ? null : () => Guard(entry.Action)));
        if (items.Count > 0) items.Add((null, null));
        items.Add(("위젯 삭제", () => _host.Remove(this)));
        DesktopPopupMenu.Show(screenX, screenY, items.ToArray());
    }

    // Plugin code runs inside our UI thread; an exception there is logged, not fatal.
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"widget '{Spec.Type}' error: {ex}");
        }
    }

    public void Dispose()
    {
        Guard(_widget.Dispose);
        _window.Dispose();
    }

    private sealed class Context : IWidgetContext
    {
        private readonly WidgetSpec _spec;
        private readonly WidgetHost _host;

        public Context(WidgetSpec spec, WidgetHost host)
        {
            _spec = spec;
            _host = host;
            // Captured here, on the UI thread. Dispatcher.CurrentDispatcher evaluated later
            // from a worker thread would hand the plugin a dispatcher for that thread instead.
            Dispatcher = Dispatcher.CurrentDispatcher;
        }

        public string Settings
        {
            get => _spec.Text;
            set
            {
                _spec.Text = value;
                _host.Save();
            }
        }

        public Dispatcher Dispatcher { get; }

        public string? AskText(string title, string current, string? hint = null, bool multiline = false) =>
            ZoneMenu.AskText(title, current, hint, multiline);

        public void Log(string message) => BlueApex.Log.Write($"[{_spec.Type}] {message}");
    }
}
