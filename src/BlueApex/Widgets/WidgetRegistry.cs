using System.IO;
using System.Reflection;
using BlueApex.Sdk;

namespace BlueApex.Widgets;

/// <summary>
/// The widget kinds available: the built-in ones plus every <see cref="IWidgetProvider"/>
/// found in DLLs under the plugin folders. A plugin folder is one subfolder per
/// plugin so its own dependencies can sit next to it.
/// </summary>
internal sealed class WidgetRegistry
{
    public static readonly string UserPluginFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueApex", "widgets");

    public static readonly string AppPluginFolder = Path.Combine(AppContext.BaseDirectory, "widgets");

    private readonly Dictionary<string, IWidgetProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public WidgetRegistry(Drawer.DrawerManager drawer)
    {
        Register(new ClockProvider());
        Register(new MemoProvider());
        Register(new ZoneWidgetProvider(drawer));
        foreach (var folder in new[] { AppPluginFolder, UserPluginFolder })
            LoadFolder(folder);
    }

    public IReadOnlyCollection<IWidgetProvider> Providers => _providers.Values;

    /// <summary>The provider for a saved widget type, or a placeholder when its plugin is gone.</summary>
    public IWidgetProvider Resolve(string id) =>
        _providers.TryGetValue(id, out var provider) ? provider : new MissingProvider(id);

    private void Register(IWidgetProvider provider)
    {
        if (_providers.ContainsKey(provider.Id))
        {
            Log.Write($"widget provider '{provider.Id}' ignored: id already registered");
            return;
        }
        _providers[provider.Id] = provider;
    }

    private void LoadFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(dll).Equals("BlueApex.Sdk.dll", StringComparison.OrdinalIgnoreCase)) continue; // a stray copy of the SDK
            try
            {
                var assembly = Assembly.LoadFrom(dll);
                var found = 0;
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(IWidgetProvider).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null) continue;
                    Register((IWidgetProvider)Activator.CreateInstance(type)!);
                    found++;
                }
                if (found > 0) Log.Write($"widget plugin loaded: {dll} ({found} provider(s))");
            }
            catch (Exception ex) when (ex is BadImageFormatException or ReflectionTypeLoadException or FileLoadException or TargetInvocationException)
            {
                Log.Write($"widget plugin skipped: {dll}: {ex.Message}");
            }
        }
    }
}
