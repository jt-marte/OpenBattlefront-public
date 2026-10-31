using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront.Views;

public partial class RemoteAdminView : UserControl
{
    public static readonly DependencyProperty BackCommandProperty =
        DependencyProperty.Register(nameof(BackCommand), typeof(ICommand), typeof(RemoteAdminView));

    public RemoteAdminView()
    {
        InitializeComponent();
        // A PasswordBox doesn't bind: fill it from the saved one, and hand changes back
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RemoteAdminViewModel vm && PasswordInput.Password != vm.Password) PasswordInput.Password = vm.Password;
        };
    }

    /// <summary>Back to the Server manager's choices.</summary>
    public ICommand? BackCommand
    {
        get => (ICommand?)GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RemoteAdminViewModel vm) vm.Password = PasswordInput.Password;
    }
}
