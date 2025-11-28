using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
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
    [ObservableProperty]
    private string _errorMessage = string.Empty;


    public StartWindowViewModel()
    {
        _clientDisconnectedHandler = SoftRestart;
    }

    [RelayCommand]
    private void ConnectWithServer()
    {
        ErrorMessage = string.Empty;

        _serverHost?.Dispose();
        _clientHost?.Dispose();

        var usernameError = CheckUsername(UsernameTextBoxValue);
        if (usernameError != null)
        {
            ErrorMessage = usernameError;
            return;
        }

        _serverHost = new ServerHost();

        var (isException, serverResult) = _serverHost.StartServer(PasswordTextBoxValue);
        if (isException)
        {
            ErrorMessage = serverResult;
            return;
        }

        _clientHost = new ClientHost();

        var clientResult = _clientHost.TryConnectToServer(serverResult, UsernameTextBoxValue);
        if (clientResult != null)
        {
            ErrorMessage = clientResult;
            return;
        }
        _clientHost.SetConnectionKey(serverResult);
        _clientHost.SubscribeToClientDisconnected(_clientDisconnectedHandler);

        IsVisible = false;
    }

    [RelayCommand]
    private void ConnectWithoutServer()
    {
        ErrorMessage = string.Empty;

        _clientHost?.Dispose();

        var usernameError = CheckUsername(UsernameTextBoxValue);
        if (usernameError != null)
        {
            ErrorMessage = usernameError;
            return;
        }

        _clientHost = new ClientHost();

        var clientResult = _clientHost.TryConnectToServer(ConnectionKeyTextBoxValue, UsernameTextBoxValue);
        if (clientResult != null)
        {
            ErrorMessage = clientResult;
            return;
        }
        _clientHost.SetConnectionKey(ConnectionKeyTextBoxValue);
        _clientHost.SubscribeToClientDisconnected(_clientDisconnectedHandler);

        IsVisible = false;
    }

    private static string? CheckUsername(string username)
    {
        if (username == string.Empty)
            return "Никнейм не может быть пустым";
        else if (username[0].Equals(' ') || username[^1].Equals(' '))
            return "Никнейм не может содержать пробелы в начале и в конце";
        else if (username.Length > 20)
            return "Никнейм не может быть длиннее 20 символов";
        return null;
    }

    private void SoftRestart()
    {
        _clientHost?.UnsubscribeFromClientDisconnected(_clientDisconnectedHandler);

        _clientHost?.CloseConnection();

        _clientHost?.Dispose();
        _serverHost?.Dispose();

        Application.Current.Dispatcher.Invoke(() =>
        {
            IsVisible = true;
        });
    }
}
