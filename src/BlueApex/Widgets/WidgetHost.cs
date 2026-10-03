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
    private readonly WidgetRegistry _registry = new();
    private readonly List<WidgetItem> _items = new();

    public WidgetHost(DesktopHost desktop, DesktopLayerInput input, DrawerManager drawer)
    {
        _desktop = desktop;
        _input = input;
        _drawer = drawer;
        foreach (var spec in _drawer.Widgets.ToList())
            Create(spec);
    }

    /// <summary>Widget kinds the user can add, built-in and plugins alike.</summary>
    public IReadOnlyCollection<IWidgetProvider> Kinds => _registry.Providers;

    public void Add(IWidgetProvider provider)
    {
        var scale = _desktop.Dpi / 96.0;
        var (areaWidth, areaHeight) = _desktop.IconAreaSize;
        var offset = (int)Math.Round(24 * scale) * _drawer.Widgets.Count;
        var spec = new WidgetSpec
        {
            Type = provider.Id,
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
internal sealed class WidgetItem : IDesktopLayerItem, IDisposable
{
    private readonly WidgetHost _host;
    private readonly DesktopLayerWindow _window;
    private readonly IWidget _widget;

    public WidgetSpec Spec { get; }

    public WidgetItem(DesktopHost desktop, WidgetSpec spec, IWidgetProvider provider, WidgetHost host)
    {
        Spec = spec;
        _host = host;
        _widget = provider.Create(new Context(spec, host));
        var corner = (int)Math.Round(WidgetStyle.CornerDip * desktop.Dpi / 96.0);
        _window = new DesktopLayerWindow(desktop, _widget.View, spec.X, spec.Y, spec.Width, spec.Height, 0.88, corner);
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

    public void Click() => Guard(_widget.OnClick);

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
