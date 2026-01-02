using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using Talknado.Client.Properties;
using Talknado.Client.Properties.Localization;
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
    [ObservableProperty]
    private int _selectedLanguageIndex;


    public StartWindowViewModel()
    {
        _clientDisconnectedHandler = SoftRestart;

        var lang = Settings.Default.Language;
        SelectedLanguageIndex = lang switch
        {
            "en" => 0,
            "ru" => 1,
            "zh" => 2,
            _ => 0
        };
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        string langCode = value switch
        {
            0 => "en",
            1 => "ru",
            2 => "zh",
            _ => "en"
        };

        if (langCode != Settings.Default.Language)
        {
            Settings.Default.Language = langCode;
            Settings.Default.Save();

            System.Diagnostics.Process.Start(Environment.ProcessPath!);
            Application.Current.Shutdown();
        }
    }

    [RelayCommand]
    private void ConnectWithServer()
    {
        ErrorMessage = string.Empty;

        _serverHost?.Dispose();
        _clientHost?.Dispose();

        var username = UsernameTextBoxValue.Trim();
        var usernameError = CheckUsername(username);
        if (usernameError != null)
        {
            ErrorMessage = usernameError;
            return;
        }

        var passwordError = CheckPassword(PasswordTextBoxValue);
        if (passwordError != null)
        {
            ErrorMessage = passwordError;
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

        var clientResult = _clientHost.TryConnectToServer(serverResult, username);
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

        var username = UsernameTextBoxValue.Trim();
        var usernameError = CheckUsername(username);
        if (usernameError != null)
        {
            ErrorMessage = usernameError;
            return;
        }

        _clientHost = new ClientHost();

        var clientResult = _clientHost.TryConnectToServer(ConnectionKeyTextBoxValue, username);
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
            return Strings.NicknameCannotBeEmptyText;
        
        return null;
    }

    private static string? CheckPassword(string password)
    {
        if (password.Contains(' '))
            return Strings.PasswordCannotContainSpacesText;

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
