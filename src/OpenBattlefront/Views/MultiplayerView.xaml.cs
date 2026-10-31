using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront.Views;

public partial class MultiplayerView : UserControl
{
    private MultiplayerViewModel? _vm;

    public MultiplayerView()
    {
        InitializeComponent();
        // A PasswordBox doesn't bind: follow the view model's password (a recent server fills it), and hand typing back
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as MultiplayerViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            PasswordInput.Password = _vm.Password;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MultiplayerViewModel.Password) && _vm != null && PasswordInput.Password != _vm.Password)
            PasswordInput.Password = _vm.Password;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm != null && _vm.Password != PasswordInput.Password) _vm.Password = PasswordInput.Password;
    }
}
