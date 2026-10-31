using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using OpenBattlefront.Core;

namespace OpenBattlefront.ViewModels;

public sealed class ModsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _dragHover;

    public ModsViewModel(MainViewModel main)
    {
        _main = main;
        AddFolderCommand = new RelayCommand(AddFolder, () => CanManage && !_main.Activity.IsBusy);
        AddZipCommand = new RelayCommand(AddZip, () => CanManage && !_main.Activity.IsBusy);
        OpenFolderCommand = new RelayCommand(() => ShellOpen.Folder(Layout.AddOn, create: true), () => CanManage);
    }

    private InstallLayout Layout => _main.Setup.Layout;
    private ModManager Manager => new(Layout);
    private ScriptMods Scripts => new(Layout);

    public ObservableCollection<ModItemViewModel> Items { get; } = new();
    public ObservableCollection<ScriptModItemViewModel> ScriptItems { get; } = new();
    public ObservableCollection<EssentialModViewModel> Essentials { get; } = new();
    public bool IsEmpty => CanManage && Items.Count == 0;
    public bool HasItems => CanManage && Items.Count > 0;
    public bool HasScriptItems => CanManage && ScriptItems.Count > 0;
    public bool CanManage => _main.Setup.State.ImportComplete;

    /// <summary>The footer: whether mods made for the 1.3 patch work with the installed build.</summary>
    public string PatchNote => ScriptMods.IsBuiltIn(_main.Setup.State.InstalledVersion)
        ? "Mods made for the unofficial 1.3 patch work without it: OpenSWBF2 has 1.3-style mod support built in."
        : $"Mods made for the unofficial 1.3 patch need OpenSWBF2 {ScriptMods.BuiltInSince.ToString(3)} or newer, which has that support built in.";
    public bool DragHover { get => _dragHover; set => Set(ref _dragHover, value); }

    public ICommand AddFolderCommand { get; }
    public ICommand AddZipCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public void Refresh()
    {
        Items.Clear();
        ScriptItems.Clear();
        Essentials.Clear();
        if (CanManage)
        {
            foreach (var mod in Manager.List())
                Items.Add(new ModItemViewModel(this, mod));
            foreach (var mod in Scripts.List())
                ScriptItems.Add(new ScriptModItemViewModel(this, mod));
            foreach (var mod in EssentialMods.All)
                Essentials.Add(new EssentialModViewModel(_main, mod, EssentialMods.IsInstalled(Layout, mod, _main.Setup.State.EssentialFolders)));
        }
        Raise(nameof(IsEmpty));
        Raise(nameof(HasItems));
        Raise(nameof(HasScriptItems));
        Raise(nameof(CanManage));
        Raise(nameof(PatchNote));
    }

    public async Task<IReadOnlyList<string>> InstallAsync(IReadOnlyList<string> paths)
    {
        if (!CanManage)
        {
            _main.ShowNotice("Import your game first, on the Play page.", error: true);
            return Array.Empty<string>();
        }
        var added = new List<string>();
        var error = await _main.Activity.RunAsync("Adding mods", async (progress, ct) =>
        {
            foreach (string path in paths)
            {
                progress.Report(new StepUpdate("Adding mods", Path.GetFileName(path)));
                added.AddRange(await Task.Run(() => Manager.Install(path), ct));
            }
        });
        Refresh();
        if (error != null) _main.ShowError(error);
        else _main.ShowNotice(added.Count == 1 ? $"Added {added[0]}." : $"Added {added.Count} mods: {string.Join(", ", added)}.");
        return error == null ? added : Array.Empty<string>();
    }

    internal void Toggle(ModItemViewModel item, bool enabled)
    {
        try
        {
            item.Mod = Manager.SetEnabled(item.Mod, enabled);
        }
        catch (IOException e)
        {
            _main.ShowError(e);
        }
        Refresh();
    }

    internal void Toggle(ScriptModItemViewModel item, bool enabled)
    {
        try
        {
            item.Mod = Scripts.SetEnabled(item.Mod, enabled);
        }
        catch (IOException e)
        {
            _main.ShowError(e);
        }
        Refresh();
    }

    internal Task RemoveAsync(ModItemViewModel item) => RemoveAsync(item.Name, item.Mod.Path);

    internal Task RemoveAsync(ScriptModItemViewModel item) => RemoveAsync(item.Name, item.Mod.Path);

    private async Task RemoveAsync(string name, string path)
    {
        if (!await _main.ConfirmAsync($"Remove {name}?", "It goes to the Recycle Bin, so you can restore it from there.", "Remove")) return;
        try
        {
            RecycleBin.Delete(path);
            _main.ShowNotice($"Removed {name}.");
        }
        catch (IOException e)
        {
            _main.ShowError(e);
        }
        Refresh();
    }

    internal void OpenScriptFolder() => ShellOpen.Folder(Layout.LvlDir, create: true);

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a mod folder (the one with addme.script in it)", Multiselect = true };
        if (dialog.ShowDialog() == true) _ = InstallAsync(dialog.FolderNames);
    }

    private void AddZip()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a mod archive or script mod",
            Filter = "Mods (*.zip;*.7z;*.rar;user_script_*.lvl;custom_gc_*.lvl)|*.zip;*.7z;*.rar;user_script_*.lvl;custom_gc_*.lvl",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) _ = InstallAsync(dialog.FileNames);
    }
}

public sealed class EssentialModViewModel
{
    public EssentialModViewModel(MainViewModel main, CatalogMod mod, bool installed)
    {
        Mod = mod;
        IsInstalled = installed;
        GetCommand = new RelayCommand(() => _ = main.GetEssentialAsync(mod), () => CanGet && !main.Activity.IsBusy);
    }

    public CatalogMod Mod { get; }
    public string Name => Mod.Name;
    public string Summary => Mod.Summary;
    public string Byline => $"by {Mod.Author} on ModDB";
    public bool IsInstalled { get; }
    public bool CanGet => Mod.Available && !IsInstalled;
    public bool IsComing => !Mod.Available && !IsInstalled;
    public string? Note => Mod.Note;
    public ICommand GetCommand { get; }
}

public sealed class ModItemViewModel : ObservableObject
{
    private readonly ModsViewModel _owner;

    public ModItemViewModel(ModsViewModel owner, ModInfo mod)
    {
        _owner = owner;
        Mod = mod;
        OpenCommand = new RelayCommand(() => ShellOpen.Folder(Mod.Path));
        RemoveCommand = new RelayCommand(() => _ = _owner.RemoveAsync(this));
    }

    public ModInfo Mod { get; set; }
    public string Name => Mod.Name;
    public string State => Mod.IsPatch ? "Not needed: built into OpenSWBF2" : Mod.Enabled ? "On" : "Off";
    public bool CanToggle => !Mod.IsPatch;

    public bool Enabled
    {
        get => Mod.Enabled;
        set => _owner.Toggle(this, value);
    }

    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }
}

public sealed class ScriptModItemViewModel : ObservableObject
{
    private readonly ModsViewModel _owner;

    public ScriptModItemViewModel(ModsViewModel owner, ScriptModInfo mod)
    {
        _owner = owner;
        Mod = mod;
        OpenCommand = new RelayCommand(owner.OpenScriptFolder);
        RemoveCommand = new RelayCommand(() => _ = _owner.RemoveAsync(this));
    }

    public ScriptModInfo Mod { get; set; }
    public string Name => Mod.Name;
    public string State => (Mod.Kind == ScriptModKind.Match ? "Runs in every match" : "Runs in the menus") + (Mod.Enabled ? "" : ", off");

    public bool Enabled
    {
        get => Mod.Enabled;
        set => _owner.Toggle(this, value);
    }

    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }
}
