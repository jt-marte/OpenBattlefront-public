using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenBattlefront.Core;
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
        for (int i = 0; i < 150 && main.Play.IsSearching; i++) await Settle(200); // the drive search
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
        await CaptureServersAsync(window, main, dir);
        main.Page = Page.Play;
        main.ShowNotice("Added ABC.");
        _ = main.ConfirmAsync("Remove ABC?", "It goes to the Recycle Bin, so you can restore it from there.", "Remove");
        await Settle(300);
        Save(window, Path.Combine(dir, "5-overlays.png"));
    }

    /// <summary>The Server manager's screens, against the sample map list and a simulated server.</summary>
    private static async Task CaptureServersAsync(Window window, MainViewModel main, string dir)
    {
        void SaveAs(string name) => Save(window, Path.Combine(dir, name));
        var servers = main.Servers;
        var control = servers.Control;
        main.Setup.State.Server = new ServerProfile { Name = "Friday Night Battles", AdminPassword = "k7Tq2mXa9RfP" };
        main.Page = Page.Servers;
        servers.Screen = ServerScreen.Options;
        await Settle(300);
        SaveAs("3b-servers-options.png");

        // The real map list first: without a game build that can list its maps, the playlist waits.
        servers.Screen = ServerScreen.Control;
        await control.LoadCatalogAsync(refresh: false);
        await Settle(300);
        SaveAs("3i-servers-no-map-list.png");
        control.UseFakeServer = true;

        await control.LoadCatalogAsync(refresh: false);
        foreach (var map in control.Maps.Where(m => m.Map.Code is "cor1" or "dag1")) map.IsExpanded = true;
        var catalog = SampleCatalog.Load();
        control.AddMissions(new[] { "geo1c_con", "kas2g_ctf", "hot1g_hunt", "mus1g_eli", "BS2c_con", "spa3c_ass" }.Select(m => catalog.Find(m)!));
        control.Playlist[0].Tickets1 = "250";
        await Settle(400);
        SaveAs("3c-servers-playlist.png");

        // Space battles, then ground battles narrowed to one era and mode
        control.Kind = BattleKind.Space;
        await Settle(300);
        SaveAs("3j-servers-space.png");
        control.Kind = BattleKind.Ground;
        control.SelectedEra = control.EraChoices.First(c => c.Id == "g");
        control.SelectedMode = control.ModeChoices.First(c => c.Id == "ctf");
        foreach (var map in control.Maps.Take(2)) map.IsExpanded = true;
        await Settle(300);
        SaveAs("3k-servers-filtered.png");
        control.SelectedEra = control.EraChoices[0];
        control.SelectedMode = control.ModeChoices[0];

        control.PlaylistDragHover = true;
        control.Playlist[2].DropBefore = true;
        await Settle(200);
        SaveAs("3d-servers-drop.png");
        control.PlaylistDragHover = false;
        control.Playlist[2].DropBefore = false;

        control.LaunchCommand.Execute(null);
        await Settle(800);
        SaveAs("3e-servers-lobby.png");

        control.StartMatchCommand.Execute(null);
        await Settle(900);
        control.AddMessage(ChatKind.Chat, "Rex CT-7567: gg, one more on Hoth?");
        control.AddMessage(ChatKind.Log, "[LogNet] OPENSWBF2 NET mission start tps=60 map=geo1c_con");
        control.Tab = ControlTab.Players;
        await Settle(600);
        SaveAs("3f-servers-players.png");

        control.Tab = ControlTab.Chat;
        control.SayText = "Next map is Kashyyyk";
        await Settle(300);
        SaveAs("3g-servers-chat.png");

        control.Tab = ControlTab.Options;
        await Settle(300);
        SaveAs("3h-servers-live-options.png");

        await control.StopNowAsync();
        control.Tab = ControlTab.Playlist;
        main.Setup.State.Server.Playlist.Clear();
        servers.Screen = ServerScreen.Landing;
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
