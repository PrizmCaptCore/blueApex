using System.Windows.Controls;

namespace BlueApex.Zones;

public partial class ZoneView : UserControl
{
    public ZoneView()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }
}
