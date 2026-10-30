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
        if (CanManage)
            foreach (var mod in Manager.List())
                Items.Add(new ModItemViewModel(this, mod));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasItems));
        Raise(nameof(CanManage));
    }

    public async Task InstallAsync(IReadOnlyList<string> paths)
    {
        if (!CanManage)
        {
            _main.ShowNotice("Import your game first, on the Play page.", error: true);
            return;
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
        var dialog = new OpenFileDialog { Title = "Choose a mod .zip", Filter = "Zip files (*.zip)|*.zip", Multiselect = true };
        if (dialog.ShowDialog() == true) _ = InstallAsync(dialog.FileNames);
    }
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
