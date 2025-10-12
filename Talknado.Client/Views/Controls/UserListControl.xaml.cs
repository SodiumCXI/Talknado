using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Talknado.Client.ViewModels;

namespace TalknadoClientWPF.Views.Controls;

public partial class UserListControl : UserControl
{
    public UserListControl()
    {
        InitializeComponent();
    }

    private void ViewScreenShareButton_Click(object sender, RoutedEventArgs e)
    {
        var mwvm = DataContext as MainWindowViewModel;
        mwvm?.ViewScreenShareCommand.Execute(null);
    }

    private void SpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Border volumeBorder)
        {
            ShowVolumeSlider(volumeBorder);
        }
    }

    private void ItemGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Grid grid && grid.Tag is Border volumeBorder)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();

                var mousePos = Mouse.GetPosition(grid);
                var rect = new Rect(0, 0, grid.ActualWidth, grid.ActualHeight);

                if (!rect.Contains(mousePos))
                {
                    HideVolumeSlider(volumeBorder);
                }
            };
            timer.Start();
        }
    }

    private static void ShowVolumeSlider(Border volumeBorder)
    {
        ((Button)volumeBorder.FindName("SpeakerButton")).Visibility = Visibility.Collapsed;
        volumeBorder.Visibility = Visibility.Visible;
        DoubleAnimation fadeIn = new(0, 1, TimeSpan.FromMilliseconds(300));
        volumeBorder.BeginAnimation(OpacityProperty, fadeIn);
    }

    private static void HideVolumeSlider(Border volumeBorder)
    {
        DoubleAnimation fadeOut = new(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (s, e) =>
        {
            ((Button)volumeBorder.FindName("SpeakerButton")).Visibility = Visibility.Visible;
            volumeBorder.Visibility = Visibility.Collapsed;
        };
        volumeBorder.BeginAnimation(OpacityProperty, fadeOut);
    }
}