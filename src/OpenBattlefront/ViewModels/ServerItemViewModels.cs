using System.Windows.Input;
using OpenBattlefront.Core;
using OpenBattlefront.Core.Rcon;

namespace OpenBattlefront.ViewModels;

public sealed class CatalogMapViewModel : ObservableObject
{
    private readonly ServerControlViewModel _owner;
    private bool _isExpanded;

    /// <summary><paramref name="choices"/>: the map's missions that fit the filters; the chips
    /// show those, one line per era, and "add every mode" adds those.</summary>
    public CatalogMapViewModel(ServerControlViewModel owner, MapInfo map, IReadOnlyList<MissionChoice> choices, bool expanded)
    {
        _owner = owner;
        Map = map;
        Choices = choices;
        _isExpanded = expanded;
        Eras = choices.GroupBy(c => c.EraName)
            .Select(g => new CatalogEraViewModel(g.Key, g.Select(c => new CatalogChipViewModel(owner, c)).ToList()))
            .ToList();
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        AddAllCommand = new RelayCommand(() => owner.AddMissions(choices));
    }

    public MapInfo Map { get; }
    public IReadOnlyList<MissionChoice> Choices { get; }
    public string Name => Map.Name;
    public bool IsMod => Map.IsMod;
    public IReadOnlyList<CatalogEraViewModel> Eras { get; }

    public string Summary
    {
        get
        {
            var modes = Choices.Select(m => m.ModeName).Distinct().ToList();
            int eras = Eras.Count;
            string list = modes.Count <= 3 ? string.Join(", ", modes) : $"{string.Join(", ", modes.Take(3))} +{modes.Count - 3}";
            return $"{list}  ·  {eras} {(eras == 1 ? "era" : "eras")}";
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value)) _owner.SetExpanded(Map.Code, value);
        }
    }

    public ICommand ToggleCommand { get; }
    public ICommand AddAllCommand { get; }
}

/// <summary>One era's line under an open map: the era's name, then its modes as chips.</summary>
public sealed record CatalogEraViewModel(string Name, IReadOnlyList<CatalogChipViewModel> Chips);

public sealed class CatalogChipViewModel
{
    public CatalogChipViewModel(ServerControlViewModel owner, MissionChoice choice)
    {
        Choice = choice;
        AddCommand = new RelayCommand(() => owner.AddMissions(new[] { choice }));
    }

    public MissionChoice Choice { get; }
    public string Label => Choice.ModeName;
    public string Tip => $"{Choice.Title}  ({Choice.Mission})  ·  drag into the playlist, or double-click";
    public ICommand AddCommand { get; }
}

public sealed class PlaylistItemViewModel : ObservableObject
{
    private readonly ServerControlViewModel _owner;
    private int _number;
    private bool _isCurrent;
    private bool _isNext;
    private bool _dropBefore;
    private bool _dropAfter;

    public PlaylistItemViewModel(ServerControlViewModel owner, PlaylistEntry entry, MissionChoice? choice, bool catalogLoaded)
    {
        _owner = owner;
        Entry = entry;
        Choice = choice;
        IsUnknown = catalogLoaded && choice == null;
        if (choice != null) entry.Title = choice.Title;
        RemoveCommand = new RelayCommand(() => owner.Remove(this));
        PlayNowCommand = new RelayCommand(() => _ = owner.PlayNowAsync(this), () => owner.CanPlayNow(this));
    }

    public PlaylistEntry Entry { get; }
    public MissionChoice? Choice { get; }

    /// <summary>A mission the map list doesn't have (a removed mod, a typo): the server would
    /// fail to load it.</summary>
    public bool IsUnknown { get; }

    public string MapName => Choice?.MapName ?? (Entry.Title.Length > 0 ? Entry.Title.Split(" · ")[0] : Entry.Mission);
    public string Detail => Choice != null ? Choice.Label : IsUnknown ? $"{Entry.Mission} · not in the map list" : Entry.Mission;
    public bool IsMod => Choice?.IsMod == true;

    public int Number { get => _number; set => Set(ref _number, value); }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool IsNext { get => _isNext; set => Set(ref _isNext, value); }
    public bool DropBefore { get => _dropBefore; set => Set(ref _dropBefore, value); }
    public bool DropAfter { get => _dropAfter; set => Set(ref _dropAfter, value); }

    /// <summary>Blank = the map's own reinforcements.</summary>
    public string Tickets1
    {
        get => Entry.Tickets1?.ToString() ?? "";
        set => SetTickets(value, t => Entry.Tickets1 = t);
    }

    public string Tickets2
    {
        get => Entry.Tickets2?.ToString() ?? "";
        set => SetTickets(value, t => Entry.Tickets2 = t);
    }

    private void SetTickets(string text, Action<int?> set)
    {
        set(int.TryParse(text.Trim(), out int tickets) && tickets > 0 ? Math.Min(tickets, 9999) : null);
        Raise(nameof(Tickets1));
        Raise(nameof(Tickets2));
        _owner.AfterPlaylistChange();
    }

    public ICommand RemoveCommand { get; }

    /// <summary>Changes the server to this map now (/changemap).</summary>
    public ICommand PlayNowCommand { get; }
}

public sealed class PlayerRowViewModel
{
    public PlayerRowViewModel(ServerControlViewModel owner, PlayerInfo info)
    {
        Info = info;
        KickCommand = new RelayCommand(() => _ = owner.KickAsync(info));
        SwapCommand = new RelayCommand(() => _ = owner.SwapAsync(info), () => info.Team is 1 or 2);
        MessageCommand = new RelayCommand(() => owner.MessagePlayer(info));
    }

    public PlayerInfo Info { get; }
    public string Name => Info.DisplayName;
    public string Team => Info.Team switch { 1 => "Team 1", 2 => "Team 2", _ => "Choosing" };
    public int TeamNumber => Info.Team;
    public int Score => Info.Score;
    public string KillsDeaths => $"{Info.Kills} / {Info.Deaths}";
    public string Ping => $"{Info.Ping} ms";
    public ICommand KickCommand { get; }
    public ICommand SwapCommand { get; }
    public ICommand MessageCommand { get; }
}

public enum ChatKind { Chat, Event, Admin, Log }

public sealed record ChatLine(DateTime Time, ChatKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}
