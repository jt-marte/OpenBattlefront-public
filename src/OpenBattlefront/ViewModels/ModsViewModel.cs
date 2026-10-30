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

    public ObservableCollection<ModItemViewModel> Items { get; } = new();
    public ObservableCollection<EssentialModViewModel> Essentials { get; } = new();
    public bool IsEmpty => CanManage && Items.Count == 0;
    public bool HasItems => CanManage && Items.Count > 0;
    public bool CanManage => _main.Setup.State.ImportComplete;
    public bool DragHover { get => _dragHover; set => Set(ref _dragHover, value); }

    public ICommand AddFolderCommand { get; }
    public ICommand AddZipCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public void Refresh()
    {
        Items.Clear();
        Essentials.Clear();
        if (CanManage)
        {
            foreach (var mod in Manager.List())
                Items.Add(new ModItemViewModel(this, mod));
            foreach (var mod in EssentialMods.All)
                Essentials.Add(new EssentialModViewModel(_main, mod, EssentialMods.IsInstalled(Layout, mod, _main.Setup.State.EssentialFolders)));
        }
        Raise(nameof(IsEmpty));
        Raise(nameof(HasItems));
        Raise(nameof(CanManage));
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

    internal async Task RemoveAsync(ModItemViewModel item)
    {
        if (!await _main.ConfirmAsync($"Remove {item.Name}?", "It goes to the Recycle Bin, so you can restore it from there.", "Remove")) return;
        try
        {
            RecycleBin.Delete(item.Mod.Path);
            _main.ShowNotice($"Removed {item.Name}.");
        }
        catch (IOException e)
        {
            _main.ShowError(e);
        }
        Refresh();
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a mod folder (the one with addme.script in it)", Multiselect = true };
        if (dialog.ShowDialog() == true) _ = InstallAsync(dialog.FolderNames);
    }

    private void AddZip()
    {
        var dialog = new OpenFileDialog { Title = "Choose a mod archive", Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar", Multiselect = true };
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
    public string State => Mod.Enabled ? "On" : "Off";

    public bool Enabled
    {
        get => Mod.Enabled;
        set => _owner.Toggle(this, value);
    }

    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }
}
