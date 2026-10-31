using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenBattlefront.Core;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront.Views;

/// <summary>Drag and drop between the catalog and the playlist: a mode chip or a whole map is
/// copied in, a playlist row moves; an accent line shows where it lands.</summary>
public partial class ServerControlView : UserControl
{
    private const string MissionsFormat = "OpenBattlefront.Missions";
    private const string RowFormat = "OpenBattlefront.PlaylistRow";

    private Point _dragStart;
    private object? _dragItem;
    private bool _dragging;

    public ServerControlView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ServerControlViewModel old)
            {
                old.MessageAdded -= OnMessageAdded;
                old.SayFocusRequested -= OnSayFocus;
            }
            if (e.NewValue is ServerControlViewModel vm)
            {
                vm.MessageAdded += OnMessageAdded;
                vm.SayFocusRequested += OnSayFocus;
            }
        };
    }

    private ServerControlViewModel? Vm => DataContext as ServerControlViewModel;

    private void OnDragSourceDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ((FrameworkElement)sender).DataContext;
        _dragStart = e.GetPosition(this);
        if (e.ClickCount == 2 && _dragItem is CatalogChipViewModel chip)
        {
            chip.AddCommand.Execute(null);
            _dragItem = null;
            e.Handled = true;
        }
    }

    private void OnDragSourceMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(at.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        DataObject? data = item switch
        {
            CatalogChipViewModel chip => new DataObject(MissionsFormat, new[] { chip.Choice }),
            CatalogMapViewModel map => new DataObject(MissionsFormat, map.Map.Missions.ToArray()),
            PlaylistItemViewModel row => new DataObject(RowFormat, row),
            _ => null,
        };
        if (data == null) return;
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, data, item is PlaylistItemViewModel ? DragDropEffects.Move : DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
            EndDrag();
        }
    }

    /// <summary>A click on a map's name opens or closes its modes; a drag adds them all.</summary>
    private void OnMapHeadUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging && _dragItem is CatalogMapViewModel map && ReferenceEquals(map, ((FrameworkElement)sender).DataContext))
            map.IsExpanded = !map.IsExpanded;
        _dragItem = null;
    }

    private void OnPlaylistDragOver(object sender, DragEventArgs e)
    {
        var vm = Vm;
        bool missions = e.Data.GetDataPresent(MissionsFormat);
        bool row = e.Data.GetDataPresent(RowFormat);
        if (vm == null || !missions && !row)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = missions ? DragDropEffects.Copy : DragDropEffects.Move;
        vm.PlaylistDragHover = missions;
        MarkDrop(DropIndex(e));
        AutoScroll(e);
        e.Handled = true;
    }

    private void OnPlaylistDragLeave(object sender, DragEventArgs e)
    {
        // Leaving for a child of the panel also raises this; DragOver puts the marks back.
        var at = e.GetPosition(PlaylistPanel);
        if (at.X < 0 || at.Y < 0 || at.X >= PlaylistPanel.ActualWidth || at.Y >= PlaylistPanel.ActualHeight) EndDrag();
    }

    private void OnPlaylistDrop(object sender, DragEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        int index = DropIndex(e);
        EndDrag();
        if (e.Data.GetData(MissionsFormat) is MissionChoice[] choices) vm.AddMissions(choices, index);
        else if (e.Data.GetData(RowFormat) is PlaylistItemViewModel row) vm.MoveEntry(row, index);
        PlaylistList.Focus();
        e.Handled = true;
    }

    /// <summary>The playlist index the drop lands at: before the first row whose middle is below
    /// the mouse, or at the end.</summary>
    private int DropIndex(DragEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return 0;
        double y = e.GetPosition(PlaylistList).Y;
        for (int i = 0; i < vm.Playlist.Count; i++)
        {
            if (PlaylistList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible) continue;
            double top = row.TranslatePoint(new Point(0, 0), PlaylistList).Y;
            if (y < top + row.ActualHeight / 2) return i;
        }
        return vm.Playlist.Count;
    }

    private void MarkDrop(int index)
    {
        var vm = Vm;
        if (vm == null) return;
        for (int i = 0; i < vm.Playlist.Count; i++)
        {
            vm.Playlist[i].DropBefore = i == index;
            vm.Playlist[i].DropAfter = i == vm.Playlist.Count - 1 && index == vm.Playlist.Count;
        }
    }

    private void EndDrag()
    {
        var vm = Vm;
        if (vm == null) return;
        vm.PlaylistDragHover = false;
        foreach (var item in vm.Playlist)
        {
            item.DropBefore = false;
            item.DropAfter = false;
        }
    }

    private void AutoScroll(DragEventArgs e)
    {
        if (FindChild<ScrollViewer>(PlaylistList) is not { } scroll) return;
        double y = e.GetPosition(scroll).Y;
        if (y < 24) scroll.LineUp();
        else if (y > scroll.ActualHeight - 24) scroll.LineDown();
    }

    /// <summary>Delete removes the selected row; Ctrl+Up and Ctrl+Down move it.</summary>
    private void OnPlaylistKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null || e.OriginalSource is TextBox) return;
        ICommand? command = e.Key switch
        {
            Key.Delete => vm.RemoveSelectedCommand,
            Key.Up when Keyboard.Modifiers == ModifierKeys.Control => vm.MoveSelectedUpCommand,
            Key.Down when Keyboard.Modifiers == ModifierKeys.Control => vm.MoveSelectedDownCommand,
            _ => null,
        };
        if (command == null) return;
        if (command.CanExecute(null)) command.Execute(null);
        if (vm.SelectedEntry != null) PlaylistList.ScrollIntoView(vm.SelectedEntry);
        e.Handled = true;
    }

    private void OnMessageAdded(object? sender, EventArgs e)
    {
        if (FindChild<ScrollViewer>(MessageList) is not { } scroll) return;
        // Follow new lines only when already at the bottom, so reading back isn't interrupted.
        if (scroll.VerticalOffset >= scroll.ScrollableHeight - 4) Dispatcher.BeginInvoke(scroll.ScrollToEnd);
    }

    private void OnSayFocus(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SayBox.Focus();
            SayBox.CaretIndex = SayBox.Text.Length;
        });
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
