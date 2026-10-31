namespace OpenBattlefront.Core;

/// <summary>What the playlist's map list shows: ground or space battles, one era or all, one
/// mode or all, stock maps and/or mods, and the words searched for.</summary>
public sealed record CatalogQuery(bool Space = false, string? EraId = null, string? ModeId = null,
                                  bool Stock = true, bool Mods = true, string Search = "");

/// <summary>Filters the map catalog for the Server manager's playlist, so a 48-map list with
/// hundreds of era × mode combinations stays readable.</summary>
public static class CatalogFilter
{
    /// <summary>The map's missions that fit the era and mode chosen.</summary>
    public static IReadOnlyList<MissionChoice> Choices(MapInfo map, CatalogQuery query) =>
        map.Missions.Where(c =>
                (query.EraId == null || c.EraId.Equals(query.EraId, StringComparison.OrdinalIgnoreCase)) &&
                (query.ModeId == null || c.ModeId.Equals(query.ModeId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>The map belongs in the list: the right kind of battle, a source that is shown,
    /// every searched word in its name, code or mode names, and at least one mission left.</summary>
    public static bool Matches(MapInfo map, CatalogQuery query)
    {
        if (map.IsSpace != query.Space) return false;
        if (map.IsMod ? !query.Mods : !query.Stock) return false;
        string[] words = query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            string haystack = $"{map.Name} {map.Code} {string.Join(' ', map.Modes.Select(m => m.Name))}";
            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) return false;
        }
        return Choices(map, query).Count > 0;
    }

    /// <summary>The maps to list, by name; a mod's version sits next to the stock one.</summary>
    public static IReadOnlyList<MapInfo> Maps(IEnumerable<MapInfo> maps, CatalogQuery query) =>
        maps.Where(m => Matches(m, query))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.IsMod)
            .ToList();

    /// <summary>The eras that ground (or space) battles have, in the catalog's order.</summary>
    public static IReadOnlyList<MapEra> Eras(IEnumerable<MapInfo> maps, bool space)
    {
        var eras = new List<MapEra>();
        foreach (var map in maps.Where(m => m.IsSpace == space))
            foreach (var choice in map.Missions)
                if (eras.All(e => !e.Id.Equals(choice.EraId, StringComparison.OrdinalIgnoreCase)))
                    eras.Add(new MapEra(choice.EraId, choice.EraName));
        return eras;
    }

    /// <summary>The modes that ground (or space) battles have, by name.</summary>
    public static IReadOnlyList<MapMode> Modes(IEnumerable<MapInfo> maps, bool space)
    {
        var modes = new List<MapMode>();
        foreach (var map in maps.Where(m => m.IsSpace == space))
            foreach (var mode in map.Modes)
                if (modes.All(m => !m.Id.Equals(mode.Id, StringComparison.OrdinalIgnoreCase)))
                    modes.Add(mode);
        return modes.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
