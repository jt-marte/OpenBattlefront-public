using System.Windows;
using System.Windows.Controls;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront.Views;

public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();

    private ModsViewModel? Vm => DataContext as ModsViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && Vm?.CanManage == true;
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        if (Vm != null) Vm.DragHover = files;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (Vm != null) Vm.DragHover = false;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm == null) return;
        Vm.DragHover = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) _ = Vm.InstallAsync(paths);
    }
}
