using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using Talknado.Client.Models;
using Talknado.Server;

namespace Talknado.Client.ViewModels;

public partial class StartWindowViewModel : ObservableObject
{
    private ServerHost? _serverHost;
    private ClientHost? _clientHost;

    private readonly Action _clientDisconnectedHandler;

    [ObservableProperty]
    private bool _isVisible = true;
    [ObservableProperty]
    private string _usernameTextBoxValue = string.Empty;
    [ObservableProperty]
    private string _connectionKeyTextBoxValue = string.Empty;
    [ObservableProperty]
    private string _passwordTextBoxValue = string.Empty;


    public StartWindowViewModel()
    {
        _clientDisconnectedHandler = SoftRestart;
    }

    [RelayCommand]
    private void ConnectWithServer()
    {
        _serverHost?.Dispose();
        _clientHost?.Dispose();

        _serverHost = new ServerHost();

        var (exception, localConnectionKey, connectionKey) = _serverHost.StartServer(PasswordTextBoxValue);
        if (exception != null)
        {
            MessageBox.Show(exception, "Ошибка сервера", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _clientHost = new ClientHost();

        var clientResult = _clientHost.TryConnectToServer(localConnectionKey!, UsernameTextBoxValue);
        if (clientResult != null)
        {
            MessageBox.Show(clientResult, "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _clientHost.SetConnectionKey(connectionKey!);
        _clientHost.SubscribeToClientDisconnected(_clientDisconnectedHandler);

        IsVisible = false;
    }

    [RelayCommand]
    private void ConnectWithoutServer()
    {
        _clientHost = new ClientHost();

        var clientResult = _clientHost.TryConnectToServer(ConnectionKeyTextBoxValue, UsernameTextBoxValue);
        if (clientResult != null)
        {
            MessageBox.Show(clientResult, "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _clientHost.SetConnectionKey(ConnectionKeyTextBoxValue);
        _clientHost.SubscribeToClientDisconnected(_clientDisconnectedHandler);

        IsVisible = false;
    }

    private void SoftRestart()
    {
        _clientHost?.UnsubscribeFromClientDisconnected(_clientDisconnectedHandler);

        _clientHost?.Dispose();
        _serverHost?.Dispose();

        IsVisible = true;
    }
}
