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

    /// <summary>"game" uses the in-game setting; "vulkan" or "d3d9" override it for each run.</summary>
    public string Renderer { get; set; } = "game";
    public bool Borderless { get; set; }
    public string ExtraArgs { get; set; } = "";
    public bool MinimiseWhilePlaying { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; } = true;
    public string UpdateRepo { get; set; } = "jt-marte/OpenSWBF2";

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
