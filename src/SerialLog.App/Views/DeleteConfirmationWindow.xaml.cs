using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SerialLog.App.Views;

public partial class DeleteConfirmationWindow : Window
{
    public DeleteConfirmationWindow(string title, string message, string confirmText = "删除")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        DetailText.Text = message;
        DeleteButton.Content = confirmText;
        ContentRendered += (_, _) => CancelButton.Focus();
    }

    public static bool Confirm(string title, string message, Window? owner = null, string confirmText = "删除")
    {
        var dialog = new DeleteConfirmationWindow(title, message, confirmText)
        {
            Owner = owner ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                ?? Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 与检查更新窗口一致，使用不透明窗口和软件渲染，保留原生窗口阴影。
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: not null } source)
            source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
