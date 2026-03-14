using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Threading;
using Talknado.Client.Properties;
using Talknado.Client.Properties.Localization;
using Talknado.Server;

namespace Talknado.Client.ViewModels;

public partial class StartWindowViewModel : ObservableObject
{
    private ServerHost? _serverHost;
    private ClientHost? _clientHost;

    private readonly Action _clientDisconnectedHandler;
    private Dispatcher? _dispatcher;

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
    [ObservableProperty]
    private bool _isServerTabSelected;
    [ObservableProperty]
    private bool _isUpnpEnabled;
    [ObservableProperty]
    private bool _isWaitingForConnection;

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

    public void SetDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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
            Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
        }
    }

    [RelayCommand]
    private void ConnectWithServer() => Connect(true);

    [RelayCommand]
    private void ConnectWithoutServer() => Connect(false);

    private void Connect(bool withServer)
    {
        IsWaitingForConnection = true;
        ErrorMessage = string.Empty;

        Task.Run(() =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var username = UsernameTextBoxValue.Trim();
                    var usernameError = CheckUsername(username);
                    if (usernameError != null) { SetError(usernameError); return; }

                    string connectionKey;
                    if (withServer)
                    {
                        var passwordError = CheckPassword(PasswordTextBoxValue);
                        if (passwordError != null) { SetError(passwordError); return; }

                        _serverHost = new ServerHost();
                        var (isException, serverResult) = _serverHost.StartServer(PasswordTextBoxValue, IsUpnpEnabled);
                        if (isException)
                        {
                            SetError(serverResult);
                            _serverHost?.Dispose();
                            return;
                        }

                        connectionKey = serverResult;
                    }
                    else
                    {
                        connectionKey = ConnectionKeyTextBoxValue;
                    }

                    _clientHost = new ClientHost();
                    var clientResult = _clientHost.TryConnectToServer(connectionKey, username);
                    if (clientResult != null)
                    {
                        SetError(clientResult);
                        _serverHost?.Dispose();
                        _clientHost?.Dispose();
                        return;
                    }

                    _clientHost.SetConnectionKey(connectionKey);
                    _clientHost.SubscribeToClientDisconnected(_clientDisconnectedHandler);
                    IsVisible = false;
                }
                finally
                {
                    _dispatcher?.Invoke(() => IsWaitingForConnection = false);
                }
            });
        });
    }

    private void SetError(string message)
    {
        if (message.StartsWith('#'))
        {
            message = message switch
            {
                "#0" => Strings.UpnpRouterNotFoundText,
                "#1" => Strings.UpnpPortAlreadyInUseText,
                "#2" => Strings.UpnpMappingRefusedText,
                _ => message
            };
        }
        _dispatcher?.Invoke(() => ErrorMessage = message);
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

        IsVisible = true;
    }
}
