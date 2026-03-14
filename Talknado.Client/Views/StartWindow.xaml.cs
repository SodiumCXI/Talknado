using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        startWindowViewModel.SetDispatcher(Dispatcher);
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
            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(startWindowViewModel.IsVisible))
                {
                    if (startWindowViewModel.IsVisible)
                    {
                        Show();
                        Activate();
                    }
                    else
                        Hide();
                }
                else if (e.PropertyName == nameof(startWindowViewModel.ErrorMessage))
                {
                    AdjustWindowHeight(startWindowViewModel);
                }
                else if (e.PropertyName == nameof(startWindowViewModel.IsServerTabSelected))
                {
                    startWindowViewModel.ErrorMessage = string.Empty;
                    AdjustWindowHeight(startWindowViewModel);
                }
            });
        };
    }

    private void AdjustWindowHeight(StartWindowViewModel viewModel)
    {
        if (!_isHeightInitialized)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();

            double errorHeight = 0;
            double checkboxHeight = 0;

            if (!string.IsNullOrEmpty(viewModel.ErrorMessage))
            {
                var errorTextBlocks = FindVisualChildren<TextBlock>(this)
                    .Where(tb => tb.Visibility == Visibility.Visible &&
                                 !string.IsNullOrEmpty(tb.Text) &&
                                 tb.Text == viewModel.ErrorMessage);

                foreach (var textBlock in errorTextBlocks)
                    errorHeight = Math.Max(errorHeight, textBlock.ActualHeight + textBlock.Margin.Top + textBlock.Margin.Bottom);
            }

            if (viewModel.IsServerTabSelected)
            {
                var checkboxes = FindVisualChildren<CheckBox>(this)
                    .Where(cb => cb.Visibility == Visibility.Visible);

                foreach (var checkbox in checkboxes)
                    checkboxHeight = Math.Max(checkboxHeight, checkbox.ActualHeight + checkbox.Margin.Top + checkbox.Margin.Bottom);
            }

            double newHeight = _originalHeight + errorHeight + checkboxHeight;
            Height = newHeight;
            MinHeight = newHeight;
            MaxHeight = newHeight;

        }), DispatcherPriority.Loaded);
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
        Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
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