using System.Net;
using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class LobbyTests
{
    private static readonly ServerAddress Local = new("127.0.0.1", 3700);

    private static HttpClient Http(FakeModServer server) => ModService.CreateHttp(new Version(0, 3, 0), server);

    [Theory]
    [InlineData("lobby", "none", false, false)]
    [InlineData("lobby", "loading", false, true)]
    [InlineData("lobby", "loading", true, false)]   // still downloading: joins when done
    [InlineData("warmup", "none", false, true)]
    [InlineData("playing", "none", false, true)]    // started elsewhere, or no lobby at all
    [InlineData("playing", "none", true, false)]
    [InlineData("ended", "none", false, false)]     // the next map may be held again
    [InlineData("offline", "none", false, false)]
    [InlineData("shell", "none", false, false)]
    public void Opens_the_game_once_the_match_starts(string phase, string start, bool downloading, bool launch) =>
        Assert.Equal(launch, LobbyRules.ShouldLaunch(new MatchStatus { Schema = 1, Phase = phase, Start = start }, downloading));

    [Fact]
    public void Reads_the_status_and_cleans_other_players_names()
    {
        var status = MatchStatus.Parse("""
            {"schema":1,"server":"S","phase":"lobby","map":"BS1g_con","next":"geo1c_con","players":2,
             "inGame":["Rex\u0007","Player 2"],"lobby":[{"name":"Pat\u0000Overlong-name-here","state":"downloading","progress":250}],
             "start":"loading","startIn":42,"inLobby":true}
            """);
        Assert.Equal("lobby", status.Phase);
        Assert.True(status.IsLoading);
        Assert.Equal(42, status.StartIn);
        Assert.Equal(new[] { "Rex", "Player 2" }, status.InGame);
        Assert.Equal("PatOverlong-nam", status.Lobby[0].Name);
        Assert.Equal(100, status.Lobby[0].Progress);
        Assert.True(status.Lobby[0].IsDownloading);
        Assert.Equal("offline", MatchStatus.Parse("""{"schema":1,"phase":"hacked"}""").Phase);
        Assert.Throws<InvalidDataException>(() => MatchStatus.Parse("""{"schema":2}"""));
        Assert.Throws<InvalidDataException>(() => MatchStatus.Parse("<html>"));
    }

    [Fact]
    public async Task Joins_says_how_the_download_goes_and_leaves()
    {
        var server = new FakeModServer { JoinPassword = "secret" };
        using var http = Http(server);
        var lobby = new LobbyClient(http, Local, "secret");
        Assert.True(await lobby.JoinAsync("Pat", default));
        Assert.Equal(("Pat", "downloading", 0), server.LobbyEntries.Values.Single());

        await lobby.UpdateAsync(downloading: true, 45, default);
        var status = await lobby.StatusAsync(default);
        Assert.True(status.InLobby);
        Assert.Equal(45, status.Lobby.Single().Progress);

        await lobby.UpdateAsync(downloading: false, 100, default);
        Assert.Equal("waiting", server.LobbyEntries.Values.Single().State);
        Assert.All(server.Requests, r => Assert.Equal("secret", r.Auth));

        await lobby.LeaveAsync(default);
        Assert.Empty(server.LobbyEntries);
        Assert.Null(lobby.Token);
        Assert.False((await lobby.StatusAsync(default)).InLobby);
    }

    [Fact]
    public async Task An_older_service_has_no_lobby()
    {
        var server = new FakeModServer { HasLobby = false };
        using var http = Http(server);
        Assert.False(await new LobbyClient(http, Local, null).JoinAsync("Pat", default));
        Assert.Null(await new ModUploadClient(http, Local, server.AdminPassword).StatusAsync(default));
    }

    [Fact]
    public async Task The_admin_starts_and_goes_back_to_the_lobby()
    {
        var server = new FakeModServer();
        using var http = Http(server);
        var admin = new ModUploadClient(http, Local, server.AdminPassword);
        await admin.StartMatchAsync(default);
        Assert.True((await admin.StatusAsync(default))!.IsLoading);
        await admin.CancelStartAsync(default);
        Assert.False((await admin.StatusAsync(default))!.IsLoading);

        server.Phase = "playing";
        var refused = await Assert.ThrowsAsync<ModServiceException>(() => admin.StartMatchAsync(default));
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("The match isn't waiting in the lobby.", refused.Message);
        await admin.BackToLobbyAsync(default);
        Assert.Equal("lobby", (await admin.StatusAsync(default))!.Phase);
        Assert.Equal(new[] { "POST ?do=start", "DELETE ", "POST ?do=lobby" }, server.MatchCalls);

        var player = new ModUploadClient(http, Local, "not the admin");
        await Assert.ThrowsAsync<ModServiceException>(() => player.StartMatchAsync(default));
    }
}
