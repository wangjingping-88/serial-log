using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SerialLog.App.Infrastructure;

namespace SerialLog.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(Window owner, string version)
    {
        InitializeComponent();
        Owner = owner;
        AppearanceResources.CaptureOwner(this, owner);
        VersionText.Text = $"版本 {version}";
        ContentRendered += (_, _) => CloseButton.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: not null } source)
            source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
