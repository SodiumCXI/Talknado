using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Talknado.Client.Properties.Localization;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public interface IMainWindow
{
    void Show();
}

public partial class MainWindow : TalknadoWindow, IMainWindow, IDisposable
{
    private readonly Thickness _defaultMargin;
    private readonly Thickness _increasedMargin;
    private CancellationTokenSource? _copyFeedbackCts;

    public MainWindow()
    {
        _defaultMargin = new Thickness(5, 0, 5, 0);
        _increasedMargin = new Thickness(5);

        InitializeComponent();
    }

    protected override void OnCloseButtonClick()
    {
        var vm = DataContext as MainWindowViewModel;
        vm?.CloseConnectionCommand.Execute(null);

        if (vm?.UseDisconnect != true)
            Application.Current.Shutdown();
    }

    private void TextBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var mwvm = DataContext as MainWindowViewModel;
        var connectionKey = mwvm!.ConnectionInfo.ConnectionKey;
        var formattedConnectionKey = mwvm!.ConnectionInfo.FormattedConnectionKey;

        CopyTextWithFeedback((TextBox)sender, connectionKey, formattedConnectionKey, Strings.CopiedText);
    }

    private async void CopyTextWithFeedback(TextBox textBox, string textToCopy, string originalText, string copiedText)
    {
        _copyFeedbackCts?.Cancel();
        _copyFeedbackCts = new CancellationTokenSource();
        var token = _copyFeedbackCts.Token;

        Clipboard.SetText(textToCopy);
        textBox.Text = copiedText;

        try
        {
            await Task.Delay(1000, token);

            if (!token.IsCancellationRequested && textBox.Text == copiedText)
            {
                textBox.Text = originalText;
            }
        }
        catch (TaskCanceledException) { /* ignore */ }
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