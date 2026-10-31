using OpenBattlefront.Core;

namespace OpenBattlefront.Tests;

public class CatalogFilterTests
{
    private static readonly MapCatalog Catalog = MapCatalog.Parse(MapCatalogTests.Sample);

    private static string[] Codes(CatalogQuery query) => CatalogFilter.Maps(Catalog.Maps, query).Select(m => m.Code).ToArray();

    [Fact]
    public void Ground_and_space_battles_are_listed_apart()
    {
        Assert.Equal(new[] { "AB1", "bes2", "geo1", "kas2" }, Codes(new CatalogQuery()));
        Assert.Equal(new[] { "S07" }, Codes(new CatalogQuery(Space: true)));
        Assert.True(new MapInfo("spa1", "Space Yavin", false, Array.Empty<MapEra>(),
            new[] { new MapMode("1flag", "1-flag CTF", new Dictionary<string, string> { ["g"] = "spa1g_1flag" }) }).IsSpace);
    }

    [Fact]
    public void Era_and_mode_narrow_the_maps_and_their_missions()
    {
        var gcw = new CatalogQuery(EraId: "g");
        Assert.Equal(new[] { "bes2", "kas2" }, Codes(gcw));
        Assert.Equal(new[] { "kas2g_con" }, CatalogFilter.Choices(Catalog.Maps[1], gcw).Select(c => c.Mission));

        var ctf = new CatalogQuery(ModeId: "ctf");
        Assert.Equal(new[] { "geo1" }, Codes(ctf));
        Assert.Equal(new[] { "geo1c_ctf" }, CatalogFilter.Choices(Catalog.Maps[0], ctf).Select(c => c.Mission));
    }

    [Fact]
    public void Stock_mods_and_search_filter_the_rest()
    {
        Assert.Equal(new[] { "geo1", "kas2" }, Codes(new CatalogQuery(Mods: false)));
        Assert.Equal(new[] { "AB1", "bes2" }, Codes(new CatalogQuery(Stock: false)));
        Assert.Equal(new[] { "kas2" }, Codes(new CatalogQuery(Search: "beach")));
        Assert.Equal(new[] { "geo1" }, Codes(new CatalogQuery(Search: "geonosis capture")));
        Assert.Empty(Codes(new CatalogQuery(Search: "hoth")));
    }

    [Fact]
    public void Filters_offer_only_what_the_section_has()
    {
        Assert.Equal(new[] { "c", "g", "q", "k" }, CatalogFilter.Eras(Catalog.Maps, space: false).Select(e => e.Id));
        Assert.Equal(new[] { "c" }, CatalogFilter.Eras(Catalog.Maps, space: true).Select(e => e.Id));
        Assert.Equal(new[] { "1-flag CTF", "Space Assault" }, CatalogFilter.Modes(Catalog.Maps, space: true).Select(m => m.Name));
        Assert.DoesNotContain(CatalogFilter.Modes(Catalog.Maps, space: false), m => m.Id == "ass");
    }
}
