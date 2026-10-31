using OpenBattlefront.Core;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.Tests;

public class ServerProfileTests
{
    private static ServerProfile Profile(params PlaylistEntry[] playlist) => new() { AdminPassword = "Adm1nPassw0rd", Playlist = playlist.ToList() };

    [Fact]
    public void Settings_file_has_one_admin_command_per_line()
    {
        var profile = Profile();
        profile.BotsPerTeam = 20;
        profile.Difficulty = 4;
        profile.TeamDamage = false;
        profile.HeroUnlock = ServerProfile.HeroUnlockTimer;
        profile.HeroUnlockValue = 90;
        profile.ConquestReinforcements = 150;

        var lines = profile.SettingsLines();

        Assert.All(lines, l => Assert.Matches(@"^/[a-z]+ -?\d+$", l));
        Assert.Contains("/difficulty 4", lines);
        Assert.Contains("/conaiperteam 20", lines);
        Assert.Contains("/ctfaiperteam 20", lines);
        Assert.Contains("/teamdamage 0", lines);
        Assert.Contains("/hrunlock 5", lines);
        Assert.Contains("/hrunlockvalue 90", lines);
        Assert.Contains("/conreinforcements 150", lines);
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(0, 60)]
    [InlineData(45, 60)]
    [InlineData(240, 60)]
    public void Tick_rate_is_only_30_60_or_120(int asked, int used)
    {
        var profile = Profile(new PlaylistEntry { Mission = "geo1c_con" });
        profile.TickRate = asked;
        string args = ServerHost.BuildArguments(profile, new InstallLayout(@"C:\OBF"), out _);
        Assert.Contains($"/tps {used} ", args);
        Assert.DoesNotContain("/tps 0", args);

        profile.Normalize();
        Assert.Equal(used, profile.TickRate);
    }

    [Fact]
    public void Normalize_keeps_values_in_the_games_ranges()
    {
        var profile = new ServerProfile
        {
            Name = "My \"best\" server, with a very long name that goes on",
            JoinPassword = "pa ss\"word",
            AdminPassword = "has spaces",
            MaxPlayers = 100,
            BotsPerTeam = 40,
            Difficulty = 1,
            Port = 80,
            HeroUnlockValue = 0,
            ConquestReinforcements = 5,
            CtfCaptures = 0,
            HuntScore = 500,
            AssaultScore = 10,
            Playlist = { new PlaylistEntry { Mission = "geo1c_con" }, new PlaylistEntry { Mission = "bad name;" } },
        };
        profile.Normalize();

        Assert.DoesNotContain('"', profile.Name);
        Assert.True(profile.Name.Length <= 31);
        Assert.Equal("password", profile.JoinPassword);
        Assert.True(ServerProfile.IsValidAdminPassword(profile.AdminPassword));
        Assert.Equal(64, profile.MaxPlayers);
        Assert.Equal(32, profile.BotsPerTeam);
        Assert.Equal(2, profile.Difficulty);
        Assert.Equal(1024, profile.Port);
        Assert.Equal(1, profile.HeroUnlockValue);
        Assert.Equal(10, profile.ConquestReinforcements);
        Assert.Equal(1, profile.CtfCaptures);
        Assert.Equal(150, profile.HuntScore);
        Assert.Equal(150, profile.AssaultScore);
        Assert.Equal(new[] { "geo1c_con" }, profile.Playlist.Select(e => e.Mission));
    }

    [Fact]
    public void Generated_admin_passwords_are_one_word()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 50; i++)
        {
            string password = ServerProfile.NewAdminPassword();
            Assert.True(ServerProfile.IsValidAdminPassword(password));
            seen.Add(password);
        }
        Assert.True(seen.Count > 45);
    }

    [Theory]
    [InlineData(150, 120, "geo1c_con 150 120")]
    [InlineData(150, null, "geo1c_con 150 150")]
    [InlineData(null, 80, "geo1c_con 80 80")]
    [InlineData(null, null, "geo1c_con")]
    [InlineData(0, 0, "geo1c_con 1 1")]
    public void Tickets_are_both_or_neither(int? t1, int? t2, string expected)
    {
        var entry = new PlaylistEntry { Mission = "geo1c_con", Tickets1 = t1, Tickets2 = t2 };
        Assert.Equal(expected, entry.ToArgument());
    }

    [Fact]
    public void Arguments_carry_the_playlist_lobby_and_settings_folder()
    {
        var layout = new InstallLayout(@"C:\Users\Me\AppData\Local\OpenBattlefront");
        var profile = Profile(
            new PlaylistEntry { Mission = "geo1c_con", Tickets1 = 150, Tickets2 = 150 },
            new PlaylistEntry { Mission = "kas2g_ctf" },
            new PlaylistEntry { Mission = "hot1g_hunt", Tickets1 = 200 });
        profile.Name = "Friday, night";
        profile.Port = 3700;
        profile.MaxPlayers = 24;

        string args = ServerHost.BuildArguments(profile, layout, out int maps);

        Assert.Equal(3, maps);
        Assert.StartsWith("-dedicated geo1c_con 150 150 kas2g_ctf hot1g_hunt 200 200 -lobby ", args);
        Assert.Contains($"/configdir \"{layout.ServerSettings}\"", args);
        Assert.Contains("/gamename \"Friday, night\"", args);
        Assert.Contains("/adminpw Adm1nPassw0rd ", args);
        Assert.Contains("/playerlimit 24 ", args);
        Assert.Contains("-port 3700 ", args);
        Assert.Contains("-logcmds \"all Warning, LogNet Inform, LogCore Inform\"", args);
        Assert.EndsWith($"-log \"{Path.Combine(layout.Logs, "server.log")}\"", args);
        Assert.DoesNotContain("/password", args);

        profile.JoinPassword = "letmein";
        Assert.Contains("/password \"letmein\" ", ServerHost.BuildArguments(profile, layout, out _));
    }

    [Fact]
    public void A_long_playlist_leaves_the_rest_for_rcon()
    {
        var entries = Enumerable.Range(0, 300).Select(i => new PlaylistEntry { Mission = $"abc{i % 10}g_con", Tickets1 = 9999, Tickets2 = 9999 }).ToArray();
        string args = ServerHost.BuildArguments(Profile(entries), new InstallLayout(@"C:\OBF"), out int maps);
        Assert.InRange(maps, 50, 299);
        Assert.True(args.Length < 3800, $"{args.Length} characters");
    }

    [Fact]
    public void An_empty_playlist_cannot_start()
    {
        Assert.Throws<InvalidOperationException>(() => ServerHost.BuildArguments(Profile(), new InstallLayout(@"C:\OBF"), out _));
    }

    [Fact]
    public void Playlist_commands_fit_in_an_admin_line()
    {
        var playlist = new[]
        {
            new PlaylistEntry { Mission = new string('x', 27) + "_con", Tickets1 = 9999, Tickets2 = 9999 },
            new PlaylistEntry { Mission = "geo1c_con" },
            new PlaylistEntry { Mission = "kas2g_ctf", Tickets1 = 5 },
        };
        var commands = PlaylistCommands.ForRotation(playlist);

        Assert.Equal("/clearmaps", commands[0]);
        Assert.Equal(new[] { "/appendmap geo1c_con", "/appendmap kas2g_ctf 5 5" }, commands.Skip(2));
        Assert.All(commands, c => Assert.True(PlaylistCommands.FitsOneLine(c), c));
        Assert.All(commands, c => RconPacket.EncodeLine(1, 0x34, RconPacket.AdminPrefix + c));
        Assert.Throws<ArgumentException>(() => PlaylistCommands.ForRotation(new[] { new PlaylistEntry { Mission = "geo1c_con /shutdown" } }));
    }

    [Fact]
    public void Profile_is_saved_with_the_launcher_state()
    {
        using var temp = new TempDir();
        string file = temp.Sub("launcher.json");
        var state = new LauncherState();
        state.Server.Name = "Saved";
        state.Server.TickRate = 120;
        state.Server.Playlist.Add(new PlaylistEntry { Mission = "geo1c_con", Title = "Geonosis", Tickets1 = 100 });
        state.RunningServer = new ServerSession(1234, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), 3658);
        state.Save(file);

        var loaded = LauncherState.Load(file);
        Assert.Equal("Saved", loaded.Server.Name);
        Assert.Equal(120, loaded.Server.TickRate);
        Assert.Equal(state.Server.AdminPassword, loaded.Server.AdminPassword);
        Assert.Equal("geo1c_con 100 100", loaded.Server.Playlist.Single().ToArgument());
        Assert.Equal(state.RunningServer, loaded.RunningServer);
    }
}
