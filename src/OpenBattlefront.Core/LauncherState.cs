using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBattlefront.Core;

/// <summary>Settings and install state, saved as launcher.json in the default root.</summary>
public sealed class LauncherState
{
    public static string DefaultFile => Path.Combine(InstallLayout.DefaultRoot, "launcher.json");

    /// <summary>Where Game\, Stock\ and Builds\ live. It can only change before the first import.</summary>
    public string? InstallRoot { get; set; }

    public string? ImportedFrom { get; set; }
    public DateTime? ImportedAt { get; set; }
    public bool ImportComplete { get; set; }

    public string? InstalledVersion { get; set; }
    public string? PreviousVersion { get; set; }

    /// <summary>The version of the included content (GameContent) last put in Game\AddOn.</summary>
    public string? ContentVersion { get; set; }

    /// <summary>"game" uses the in-game setting; "vulkan" or "d3d9" override it for each run.</summary>
    public string Renderer { get; set; } = "game";
    public bool Borderless { get; set; } = true;

    /// <summary>Skip the logos and legal screens at start (-nointro).</summary>
    public bool SkipIntro { get; set; } = true;

    /// <summary>The profile the game signs into, or makes (-name). Null until the launcher picks one
    /// (PlayerProfiles.DefaultName); empty means the game's own profile screen.</summary>
    public string? PlayerName { get; set; }
    public string ExtraArgs { get; set; } = "";
    public bool MinimiseWhilePlaying { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>Offer the Essential mods (EssentialMods.All) once the game is set up.</summary>
    public bool EssentialMods { get; set; } = true;

    /// <summary>The Essential mods were offered once after setup; later they live on the Mods page.</summary>
    public bool EssentialsOffered { get; set; }

    /// <summary>The AddOn folders each Essential mod installed, by catalog id.</summary>
    public Dictionary<string, string[]> EssentialFolders { get; set; } = new();

    public string UpdateRepo { get; set; } = "jt-marte/OpenSWBF2";

    /// <summary>The Server manager's host settings and playlist.</summary>
    public ServerProfile Server { get; set; } = new();

    /// <summary>A server left running when the launcher closed, to attach to again.</summary>
    public ServerSession? RunningServer { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [JsonIgnore]
    public InstallLayout Layout => new(InstallRoot ?? InstallLayout.DefaultRoot);

    public static LauncherState Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(file), Options) ?? new LauncherState();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file starts fresh rather than stopping the launcher.
        }
        return new LauncherState();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, file, overwrite: true);
    }
}
