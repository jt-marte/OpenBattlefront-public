using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront;

/// <summary>Renders each page to a PNG (the --screenshot development switch).</summary>
internal static class Screenshots
{
    public static async Task CaptureAsync(Window window, MainViewModel main, string dir, string? drop)
    {
        Directory.CreateDirectory(dir);
        main.Play.Refresh();
        await Settle(1500);
        if (drop != null)
        {
            await main.Play.HandleDropAsync(drop);
            await Settle(300);
        }
        Save(window, Path.Combine(dir, "1-play.png"));
        foreach (var page in new[] { Page.Mods, Page.Servers, Page.Settings })
        {
            main.Page = page;
            await Settle(300);
            Save(window, Path.Combine(dir, $"{(int)page + 1}-{page.ToString().ToLowerInvariant()}.png"));
        }
        main.Page = Page.Play;
        main.ShowNotice("Added ABC.");
        _ = main.ConfirmAsync("Remove ABC?", "It goes to the Recycle Bin, so you can restore it from there.", "Remove");
        await Settle(300);
        Save(window, Path.Combine(dir, "5-overlays.png"));
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string file)
    {
        var root = (FrameworkElement)window.Content;
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
