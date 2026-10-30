using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using OpenBattlefront.Core;

namespace OpenBattlefront.Views;

/// <summary>
/// A small browser on a mod's own download page. The player clicks the download there, as they
/// would in any browser; the window catches the file, so nothing has to be found in Downloads.
/// </summary>
public partial class ModDownloadWindow : Window
{
    private readonly CatalogMod _mod;
    private readonly InstallLayout _layout;

    private ModDownloadWindow(CatalogMod mod, InstallLayout layout)
    {
        InitializeComponent();
        _mod = mod;
        _layout = layout;
        ModName.Text = $"Get {mod.Name}";
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Web.Dispose();
    }

    public string? DownloadedFile { get; private set; }

    /// <summary>Shows the window and returns the downloaded archive, or null if the player closed
    /// it first.</summary>
    public static string? Show(Window owner, CatalogMod mod, InstallLayout layout)
    {
        var window = new ModDownloadWindow(mod, layout) { Owner = owner };
        window.ShowDialog();
        return window.DownloadedFile;
    }

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(_layout.Root);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_layout.Root, "WebView2"));
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // No WebView2 (rare on Windows 10/11): fall back to the normal browser.
            ShellOpen.Url(_mod.PageUrl);
            App.Launcher?.ShowNotice($"Opened {_mod.Name} in your browser. Download it, then drop the file on the Mods page.");
            Close();
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        // Pages that open a new window (mirror pickers) stay in this one.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.NavigationCompleted += (_, _) =>
        {
            // WebView2 draws in its own window, which WPF can't overlay, so it stays
            // hidden behind the "Opening ModDB" line until the page is there.
            Loading.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
        };
        core.DownloadStarting += OnDownloadStarting;
        core.Navigate(_mod.PageUrl);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        string name = Path.GetFileName(e.ResultFilePath);
        if (!Archives.IsArchive(name))
        {
            e.Cancel = true;
            ShowStatus($"{name} isn't a .zip, .7z or .rar. OpenBattlefront never runs mod installers.", null);
            return;
        }

        Directory.CreateDirectory(_layout.Downloads);
        e.ResultFilePath = Path.Combine(_layout.Downloads, name);
        e.Handled = true; // no browser download bubble; the header shows progress
        var download = e.DownloadOperation;
        ShowStatus($"Downloading {name}", null);

        download.BytesReceivedChanged += (_, _) =>
        {
            long? total = (long?)download.TotalBytesToReceive;
            ShowStatus(total is > 0 ? $"{Sizes.Format(download.BytesReceived)} of {Sizes.Format(total.Value)}" : Sizes.Format(download.BytesReceived),
                total is > 0 ? (double)download.BytesReceived / total.Value : null);
        };
        download.StateChanged += (_, _) =>
        {
            if (download.State == CoreWebView2DownloadState.Completed)
            {
                DownloadedFile = download.ResultFilePath;
                Close();
            }
            else if (download.State == CoreWebView2DownloadState.Interrupted)
            {
                ShowStatus($"The download stopped ({download.InterruptReason}). Click download to try again.", null);
                Progress.IsIndeterminate = false;
                Progress.Value = 0;
            }
        };
    }

    private void ShowStatus(string text, double? fraction)
    {
        ProgressArea.Visibility = Visibility.Visible;
        ProgressText.Text = text;
        Progress.IsIndeterminate = fraction == null;
        if (fraction != null) Progress.Value = fraction.Value;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x003B2A21; // #212A3B
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
