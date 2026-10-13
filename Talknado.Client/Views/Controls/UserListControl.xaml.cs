using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views.Controls;

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

    // Фокус переходит на ползунок: громкость сразу меняется стрелками.
    private void SpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Border volumeBorder)
        {
            ShowVolumeSlider(volumeBorder);
            (volumeBorder.Child as Slider)?.Focus();
        }
    }

    // Esc убирает ползунок и возвращает фокус на кнопку динамика.
    private void VolumeSlider_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Slider { Parent: Border volumeBorder })
            return;

        HideVolumeSlider(volumeBorder);
        (volumeBorder.FindName("SpeakerButton") as Button)?.Focus();
        e.Handled = true;
    }

    // Фокус клавиатуры ушёл из строки — ползунок убирается, как при уходе мыши.
    private void Row_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsKeyboardFocusWithin: false, IsMouseOver: false } row
            && row.Tag is Border volumeBorder)
        {
            HideVolumeSlider(volumeBorder);
        }
    }

    /// <summary>
    /// Захват мыши значит, что пользователь тянет бегунок за пределами строки:
    /// тогда ползунок не убирается.
    /// </summary>
    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement row
            && row.Tag is Border volumeBorder
            && !row.IsMouseCaptureWithin)
        {
            HideVolumeSlider(volumeBorder);
        }
    }

    // Ширина раскрытого ползунка. Кнопка динамика в это же время сжимается
    // до нуля и гаснет, поэтому ряд не прыгает.
    private const double VolumeSliderWidth = 96;

    private static double SpeakerButtonWidth =>
        Application.Current?.TryFindResource("ControlHeight") as double? ?? 34;

    private static readonly Duration SlideDuration =
        new(TimeSpan.FromMilliseconds(160));

    private static readonly IEasingFunction SlideEasing =
        new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void ShowVolumeSlider(Border volumeBorder)
    {
        if (volumeBorder.Visibility == Visibility.Visible)
            return;

        volumeBorder.Visibility = Visibility.Visible;
        volumeBorder.ClipToBounds = true;
        SetSpeakerTabStop(volumeBorder, false);
        Slide(volumeBorder, 0, VolumeSliderWidth, 0, 1, () => volumeBorder.ClipToBounds = false);

        if (volumeBorder.FindName("SpeakerButtonHost") is Border host)
        {
            host.ClipToBounds = true;
            Slide(host, SpeakerButtonWidth, 0, 1, 0);
        }
    }

    private static void HideVolumeSlider(Border volumeBorder)
    {
        if (volumeBorder.Visibility != Visibility.Visible)
            return;

        volumeBorder.ClipToBounds = true;
        SetSpeakerTabStop(volumeBorder, true);
        Slide(volumeBorder, VolumeSliderWidth, 0, 1, 0,
            () => volumeBorder.Visibility = Visibility.Collapsed);

        if (volumeBorder.FindName("SpeakerButtonHost") is Border host)
            Slide(host, 0, SpeakerButtonWidth, 0, 1, () => host.ClipToBounds = false);
    }

    // Сжатая до нуля кнопка динамика не должна ловить Tab, пока открыт ползунок.
    private static void SetSpeakerTabStop(Border volumeBorder, bool value)
    {
        if (volumeBorder.FindName("SpeakerButton") is Button speaker)
            speaker.Focusable = value;
    }

    private static void Slide(FrameworkElement element,
                              double fromWidth, double toWidth,
                              double fromOpacity, double toOpacity,
                              Action? completed = null)
    {
        DoubleAnimation width = new(fromWidth, toWidth, SlideDuration) { EasingFunction = SlideEasing };
        if (completed is not null)
            width.Completed += (_, _) => completed();

        element.BeginAnimation(FrameworkElement.WidthProperty, width);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(fromOpacity, toOpacity, SlideDuration));
    }
}