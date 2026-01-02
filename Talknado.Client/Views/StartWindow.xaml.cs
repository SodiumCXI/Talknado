using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class StartWindow : TalknadoWindow, IDisposable
{
    private double _originalHeight;
    private bool _isHeightInitialized = false;

    public StartWindow()
    {
        InitializeComponent();

        var startWindowViewModel = new StartWindowViewModel();
        DataContext = startWindowViewModel;

        Loaded += (s, e) =>
        {
            if (!_isHeightInitialized)
            {
                _originalHeight = ActualHeight;
                _isHeightInitialized = true;
            }
        };

        startWindowViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(startWindowViewModel.IsVisible))
            {
                Visibility = startWindowViewModel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (e.PropertyName == nameof(startWindowViewModel.ErrorMessage))
            {
                AdjustWindowHeight();
            }
        };
    }

    private void AdjustWindowHeight()
    {
        if (!_isHeightInitialized || DataContext is not StartWindowViewModel viewModel)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();

            double errorHeight = 0;

            if (!string.IsNullOrEmpty(viewModel.ErrorMessage))
            {
                var errorTextBlocks = FindVisualChildren<TextBlock>(this)
                    .Where(tb => tb.Visibility == Visibility.Visible &&
                                !string.IsNullOrEmpty(tb.Text) &&
                                tb.Text == viewModel.ErrorMessage);

                foreach (var textBlock in errorTextBlocks)
                {
                    double textBlockHeight = textBlock.ActualHeight;

                    var margin = textBlock.Margin;
                    textBlockHeight += margin.Top + margin.Bottom;

                    errorHeight = Math.Max(errorHeight, textBlockHeight);
                }
            }

            double newHeight = _originalHeight + errorHeight;
            Height = newHeight;
            MinHeight = newHeight;
            MaxHeight = newHeight;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);

            if (child is T t)
                yield return t;

            foreach (T childOfChild in FindVisualChildren<T>(child))
                yield return childOfChild;
        }
    }

    protected override void OnMaximizeButtonClick()
    {
        return;
    }

    protected override void OnCloseButtonClick()
    {
        Application.Current.Shutdown();
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