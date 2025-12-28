using System.ComponentModel;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views
{
    /// <summary>
    /// Логика взаимодействия для SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : TalknadoWindow, IDisposable
    {
        public SettingsWindow()
        {
            InitializeComponent();

            DataContextChanged += OnDataContextChanged;
        }

        protected override void OnCloseButtonClick()
        {
            if (DataContext is SettingsWindowViewModel ssvm)
            {
                ssvm.SettingManager.IsWindowVisible = false;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is SettingsWindowViewModel oldVm &&
                oldVm.SettingManager is INotifyPropertyChanged oldNotifier)
            {
                PropertyChangedEventManager.RemoveHandler(oldNotifier, OnPlayerPropertyChanged!, "");
            }

            if (e.NewValue is SettingsWindowViewModel newVm &&
                newVm.SettingManager is INotifyPropertyChanged newNotifier)
            {
                PropertyChangedEventManager.AddHandler(newNotifier, OnPlayerPropertyChanged!, nameof(ISettingsManager.IsWindowVisible));
                UpdateVisibility(newVm.SettingManager.IsWindowVisible);
            }
        }

        private void OnPlayerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISettingsManager.IsWindowVisible) &&
                DataContext is SettingsWindowViewModel vm)
            {
                UpdateVisibility(vm.SettingManager.IsWindowVisible);
            }
        }

        private void UpdateVisibility(bool isVisible)
        {
            if (isVisible)
                Show();
            else
                Hide();
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (DataContext is SettingsWindowViewModel ssvm)
            {
                ssvm.SettingManager.IsWindowVisible = false;
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
}
