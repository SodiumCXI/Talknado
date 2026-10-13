using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Talknado.Client.Models;
using Talknado.Client.Properties.Localization;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public interface IMainWindow
{
    void Show();
    bool Activate();
}

public partial class MainWindow : TalknadoWindow, IMainWindow, IDisposable
{
    private CancellationTokenSource? _copyFeedbackCts;

    public MainWindow()
    {
        // Своя анимация свёртывания требует AllowsTransparency, которого у окна нет.
        UseSystemCaption = true;
        UseStateAnimations = false;

        InitializeComponent();
    }

    // Фокус возвращается в поле ввода до открытия диалога: после его закрытия
    // окно восстановит фокус туда, и Enter отправит сообщение, а не нажмёт
    // скрепку повторно.
    private void ComposerButton_Click(object sender, RoutedEventArgs e) => InputTextBox.Focus();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && DataContext is MainWindowViewModel vm)
        {
            vm.Attach(files[0]);
            InputTextBox.Focus();
        }
    }

    /// <summary>
    /// Ctrl+V с картинкой или файлом в буфере обмена прикрепляет их, а не вставляет
    /// текст. Картинка из буфера сохраняется во временную папку как PNG.
    /// Ловится нажатие, а не команда Paste: без текста в буфере TextBox считает
    /// вставку невозможной, и команда не выполняется вовсе.
    /// </summary>
    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || DataContext is not MainWindowViewModel vm)
            return;

        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
            {
                vm.Attach(files[0]!);
                e.Handled = true;
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                Directory.CreateDirectory(FileTransferManager.CacheDirectory);
                var path = Path.Combine(FileTransferManager.CacheDirectory, $"image_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path))
                    encoder.Save(stream);

                vm.Attach(path);
                e.Handled = true;
            }
        }
        catch { /* буфер обмена занят другим приложением: вставка не происходит */ }
    }

    protected override void OnCloseButtonClick()
    {
        var vm = DataContext as MainWindowViewModel;
        vm?.CloseConnectionCommand.Execute(null);

        if (vm?.UseDisconnect != true)
            Application.Current.Shutdown();
    }

    /// <summary>
    /// Щелчок по пустому месту окна снимает фокус с поля ввода: иначе курсор
    /// остаётся в нём и после клика в стороне.
    /// </summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && !IsWithinFocusable(source))
            Keyboard.ClearFocus();
    }

    private static bool IsWithinFocusable(DependencyObject source)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBoxBase or ButtonBase or Slider or ComboBox or ListBoxItem)
                return true;
        }
        return false;
    }

    private async void CopyKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        Clipboard.SetText(vm.ConnectionInfo.ConnectionKey);

        _copyFeedbackCts?.Cancel();
        _copyFeedbackCts = new CancellationTokenSource();
        var token = _copyFeedbackCts.Token;

        CopiedPopup.IsOpen = true;

        try
        {
            await Task.Delay(1200, token);
            CopiedPopup.IsOpen = false;
        }
        catch (TaskCanceledException) { /* пришёл следующий щелчок */ }
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

    public void Dispose()
    {
        UseCustomClose = false;

        try
        {
            Application.Current?.Dispatcher.Invoke(Close);
        }
        catch { /* ignore */ }

        GC.SuppressFinalize(this);
    }
}