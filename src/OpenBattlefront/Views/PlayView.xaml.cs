using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenBattlefront.ViewModels;

namespace OpenBattlefront.Views;

public partial class PlayView : UserControl
{
    public PlayView()
    {
        InitializeComponent();
        Loaded += (_, _) => DrawStars();
    }

    private PlayViewModel? Vm => DataContext as PlayViewModel;

    /// <summary>A fixed scatter of faint stars, the same every time.</summary>
    private void DrawStars()
    {
        Stars.Children.Clear();
        var random = new Random(2005);
        double width = Math.Max(ActualWidth, 752), height = Math.Max(ActualHeight, 600);
        for (int i = 0; i < 90; i++)
        {
            double size = random.NextDouble() < 0.12 ? 2.2 : 1.2;
            bool blue = random.NextDouble() < 0.2;
            var star = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(blue ? Color.FromRgb(0x9F, 0xC2, 0xFF) : Color.FromRgb(0xE4, 0xEA, 0xF5)),
                Opacity = 0.15 + random.NextDouble() * 0.5,
            };
            Canvas.SetLeft(star, random.NextDouble() * width);
            Canvas.SetTop(star, random.NextDouble() * height * 0.75);
            Stars.Children.Add(star);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && Vm?.Stage is PlayStage.Welcome or PlayStage.Invalid or PlayStage.Review;
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
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Vm.Stage is PlayStage.Welcome or PlayStage.Invalid or PlayStage.Review)
            _ = Vm.HandleDropAsync(paths[0]);
    }
}
