namespace BlueApex.Widgets;

/// <summary>A widget on the home screen, as saved in the layout file. Position and size are icon-view pixels.</summary>
internal sealed class WidgetSpec
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The provider id: built-in "clock"/"memo", or a plugin's <c>IWidgetProvider.Id</c>.</summary>
    public string Type { get; set; } = "clock";

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The widget's own settings (<c>IWidgetContext.Settings</c>). Named Text for compatibility with early layouts.</summary>
    public string Text { get; set; } = "";
}
