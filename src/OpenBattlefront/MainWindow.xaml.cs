using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel main)
            {
                main.DownloadMod = mod => Views.ModDownloadWindow.Show(this, mod, main.Setup.Layout);
                main.MinimiseRequested += (_, _) => WindowState = WindowState.Minimized;
                main.RestoreRequested += (_, _) =>
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                };
            }
        };
        Activated += (_, _) => (DataContext as MainViewModel)?.Play.Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11: dark frame, rounded corners and a hairline border in the theme's colour.
        // Older Windows ignores these.
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x003B2A21; // COLORREF 0x00BBGGRR for #212A3B
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private bool _askedToClose;

    /// <summary>Closing while hosting asks whether to stop the server or leave it running.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_askedToClose || DataContext is not MainViewModel main || !main.Servers.Control.IsRunning) return;
        e.Cancel = true;
        if (main.ConfirmOpen) return;
        await main.BeforeCloseAsync();
        _askedToClose = true;
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
