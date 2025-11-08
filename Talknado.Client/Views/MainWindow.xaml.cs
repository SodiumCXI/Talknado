using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public interface IMainWindow
{
    void Show();
    void Close();
}

public partial class MainWindow : Window, IMainWindow, IDisposable
{
    private readonly Thickness _defaultMargin;
    private readonly Thickness _increasedMargin;

    private double _actualTop;
    private WindowState _previousWindowState;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    public MainWindow()
    {
        _defaultMargin = new Thickness(5, 0, 5, 0);
        _increasedMargin = new Thickness(5);

        InitializeComponent();
    }

    private void TextBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CopyTextWithFeedback((TextBox)sender, "Скопировано!");
    }

    private static async void CopyTextWithFeedback(TextBox textBox, string copiedText)
    {
        var originalText = textBox.Text;

        Clipboard.SetText(originalText);
        textBox.Text = copiedText;

        await Task.Delay(1000);
        if (textBox.Text == copiedText)
        {
            textBox.Text = originalText;
        }
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            UpdateTextBoxMargin(textBox);
        }
    }

    private void InputTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            UpdateTextBoxMargin(textBox);
        }
    }

    private void UpdateTextBoxMargin(TextBox textBox)
    {
        if (textBox.LineCount > 1)
        {
            textBox.Margin = _increasedMargin;
        }
        else
        {
            textBox.Margin = _defaultMargin;
        }
    }

    private Point GetTextBoxPosition(TextBox textBox)
    {
        return textBox.TransformToAncestor(this).Transform(new Point(0, 0));
    }
    public void AnimateInputMessage()
    {
        int lastLineIndex = InputTextBox.LineCount - 1;
        string lastLineText = InputTextBox.GetLineText(lastLineIndex);

        var rand = new Random();
        var textBoxPos = GetTextBoxPosition(InputTextBox);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var typeface = new Typeface(
            InputTextBox.FontFamily,
            InputTextBox.FontStyle,
            InputTextBox.FontWeight,
            InputTextBox.FontStretch);

        double baseX = textBoxPos.X - 5;
        double baseY = textBoxPos.Y - 2;

        double currentOffset = 0;

        double lineSpacing, lineY;
        if (InputTextBox.LineCount > 1)
        {
            double y0 = InputTextBox.GetRectFromCharacterIndex(InputTextBox.GetCharacterIndexFromLineIndex(0)).Top;
            double y1 = InputTextBox.GetRectFromCharacterIndex(InputTextBox.GetCharacterIndexFromLineIndex(1)).Top;

            lineSpacing = y1 - y0;

            lineY = baseY + (lastLineIndex * lineSpacing) - InputTextBox.Margin.Top;
        }
        else
        {
            lineY = baseY;
        }

        foreach (char c in lastLineText)
        {
            string charText = c.ToString();

            var formattedText = new FormattedText(
                charText,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                InputTextBox.FontSize,
                InputTextBox.Foreground,
                dpi);

            double letterWidth = formattedText.WidthIncludingTrailingWhitespace;

            var letter = new TextBlock
            {
                Text = charText,
                FontSize = InputTextBox.FontSize,
                Foreground = InputTextBox.Foreground,
                FontFamily = InputTextBox.FontFamily,
                FontWeight = InputTextBox.FontWeight
            };

            AnimationCanvas.Children.Add(letter);

            double offsetX = baseX + currentOffset;

            Canvas.SetLeft(letter, offsetX);
            Canvas.SetTop(letter, lineY);

            var transform = new TranslateTransform();
            letter.RenderTransform = transform;

            var moveXAnimation = new DoubleAnimation(rand.NextDouble() * 20 - 10, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var moveYAnimation = new DoubleAnimation(15, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var fadeOutAnimation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOutAnimation.Completed += (s, e) => AnimationCanvas.Children.Remove(letter);

            transform.BeginAnimation(TranslateTransform.XProperty, moveXAnimation);
            transform.BeginAnimation(TranslateTransform.YProperty, moveYAnimation);
            letter.BeginAnimation(OpacityProperty, fadeOutAnimation);

            currentOffset += letterWidth;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        var element = (UIElement)sender;
        var startPoint = e.GetPosition(null);
        element.CaptureMouse();

        MouseEventHandler? moveHandler = null;
        MouseButtonEventHandler? upHandler = null;

        moveHandler = (_, _) =>
        {
            var currentPoint = e.GetPosition(null);
            if (Math.Abs(currentPoint.X - startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentPoint.Y - startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                element.MouseMove -= moveHandler;
                element.MouseLeftButtonUp -= upHandler;
                element.ReleaseMouseCapture();

                if (WindowState == WindowState.Maximized)
                {
                    double ratio = currentPoint.X / ActualWidth;
                    var screenPos = PointToScreen(currentPoint);

                    WindowState = WindowState.Normal;
                    _previousWindowState = WindowState.Normal;
                    UpdateLayout();

                    Left = screenPos.X - ratio * Width;

                    var handle = new WindowInteropHelper(this).Handle;
                    MoveWindow(handle, (int)Left, 0, (int)Width, (int)Height, true);
                }

                DragMove();

                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleY = dpiInfo.DpiScaleY;
                Point currentPos = PointToScreen(new Point(0, 0));
                _actualTop = currentPos.Y / dpiScaleY;
            }
        };

        upHandler = (_, _) =>
        {
            element.MouseMove -= moveHandler;
            element.MouseLeftButtonUp -= upHandler;
            element.ReleaseMouseCapture();
        };

        element.MouseMove += moveHandler;
        element.MouseLeftButtonUp += upHandler;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            var sb = new Storyboard();

            double targetTop = _actualTop + 200;
            var move = new DoubleAnimation
            {
                From = _actualTop,
                To = targetTop,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(move, this);
            Storyboard.SetTargetProperty(move, new PropertyPath("Top"));
            sb.Children.Add(move);

            // Анимация прозрачности
            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fade, this);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);

            sb.Completed += (_, _) =>
            {
                WindowState = WindowState.Minimized;
                _previousWindowState = WindowState.Minimized;
                UpdateLayout();
            };
            sb.Begin();
        }
        else if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Minimized;
            _previousWindowState = WindowState.Minimized;
        }
    }
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        vm?.CloseConnectionCommand.Execute(null);

        Application.Current.Shutdown();
    }
    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Normal)
        {
            MaxWidth = SystemParameters.WorkArea.Width + (BorderAround.BorderThickness.Left + BorderAround.BorderThickness.Right);
            MaxHeight = SystemParameters.WorkArea.Height + (BorderAround.BorderThickness.Top + BorderAround.BorderThickness.Bottom);

            WindowState = WindowState.Maximized;
            _previousWindowState = WindowState.Maximized;
            UpdateLayout();
        }
        else
        {
            WindowState = WindowState.Normal;
            _previousWindowState = WindowState.Normal;
            UpdateLayout();
        }
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && _previousWindowState == WindowState.Minimized)
        {
            var sb = new Storyboard();

            double targetTop = _actualTop + 200;
            var moveBack = new DoubleAnimation
            {
                From = targetTop,
                To = _actualTop,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(moveBack, this);
            Storyboard.SetTargetProperty(moveBack, new PropertyPath("Top"));
            sb.Children.Add(moveBack);

            var fadeBack = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeBack, this);
            Storyboard.SetTargetProperty(fadeBack, new PropertyPath("Opacity"));
            sb.Children.Add(fadeBack);

            sb.Completed += (_, _) =>
            {
                WindowState = WindowState.Normal;
                _previousWindowState = WindowState.Normal;
                UpdateLayout();
            };

            sb.Begin();
        }
        else if (WindowState == WindowState.Maximized && _previousWindowState == WindowState.Minimized)
        {
            var sb = new Storyboard();

            var fadeBack = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeBack, this);
            Storyboard.SetTargetProperty(fadeBack, new PropertyPath("Opacity"));
            sb.Children.Add(fadeBack);

            sb.Completed += (_, _) => _previousWindowState = WindowState.Maximized;

            sb.Begin();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _previousWindowState = WindowState;

        var dpiInfo = VisualTreeHelper.GetDpi(this);
        double dpiScaleY = dpiInfo.DpiScaleY;

        Point currentPos = PointToScreen(new Point(0, 0));
        _actualTop = currentPos.Y / dpiScaleY;
    }

    public void Dispose()
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(Close);
        }
        catch { /* ignore */ }

        GC.SuppressFinalize(this);
    }
}