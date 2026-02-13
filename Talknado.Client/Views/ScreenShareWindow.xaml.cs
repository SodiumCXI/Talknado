using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.Properties.Localization;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public partial class ScreenShareWindow : TalknadoWindow, IDisposable
{
    public ScreenShareWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnCloseButtonClick()
    {
        if (DataContext is ScreenShareViewModel ssvm)
        {
            ssvm.ScreenSharePlayer.IsWindowVisible = false;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ScreenShareViewModel oldVm &&
            oldVm.ScreenSharePlayer is INotifyPropertyChanged oldNotifier)
        {
            PropertyChangedEventManager.RemoveHandler(oldNotifier, OnPlayerPropertyChanged!, "");
        }

        if (e.NewValue is ScreenShareViewModel newVm &&
            newVm.ScreenSharePlayer is INotifyPropertyChanged newNotifier)
        {
            PropertyChangedEventManager.AddHandler(newNotifier, OnPlayerPropertyChanged!, nameof(IScreenSharePlayer.IsWindowVisible));
            UpdateVisibility(newVm.ScreenSharePlayer.IsWindowVisible, newVm);
        }
    }

    private void OnPlayerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IScreenSharePlayer.IsWindowVisible) &&
            DataContext is ScreenShareViewModel vm)
        {
            UpdateVisibility(vm.ScreenSharePlayer.IsWindowVisible, vm);
        }
    }

    private void UpdateVisibility(bool isVisible, ScreenShareViewModel vm)
    {
        if (isVisible)
        {
            if (vm.UsersInfo.IsWaitingForScreenShareWindow)
                return;

            _ = WaitForKeyFrameAndShowAsync(vm);
        }
        else
        {
            vm.ScreenSharePlayer.IsKeyFrameInitialized = false;
            vm.ScreenSharePlayer.Clear();
            Task.Delay(50).ContinueWith(_ => Application.Current?.Dispatcher.Invoke(Hide));
        }
    }

    private async Task WaitForKeyFrameAndShowAsync(ScreenShareViewModel vm)
    {
        vm.UsersInfo.IsWaitingForScreenShareWindow = true;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            while (!vm.ScreenSharePlayer.IsKeyFrameInitialized)
            {
                await Task.Delay(50, cts.Token);
            }
            Application.Current?.Dispatcher.Invoke(Show);
        }
        catch (OperationCanceledException)
        {
            if (vm.ScreenSharePlayer.LastKeyFrame != null)
            {
                Application.Current?.Dispatcher.Invoke(Show);
                vm.ScreenSharePlayer.UpdateSavedKeyFrame();
            }
            else
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(Strings.NoKeyframeReceivedText, Strings.ScreenDisplayErrorText,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    vm.ScreenSharePlayer.IsWindowVisible = false;
                });
            }
        }
        finally
        {
            vm.UsersInfo.IsWaitingForScreenShareWindow = false;
            cts.Dispose();
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