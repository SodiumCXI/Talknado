using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Talknado.Client.Models;
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
    // Пустая строка — кнопки обновления нет
    [ObservableProperty]
    private string _updateButtonText = string.Empty;
    [ObservableProperty]
    private string _updateToolTip = string.Empty;

    private AppUpdate? _update;

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

        UsernameTextBoxValue = Settings.Default.Nickname;
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

    public async Task CheckForUpdateAsync()
    {
        _update = await AppUpdate.FindNewerAsync();
        if (_update == null)
            return;

        string version = _update.Version.ToString(3);
        UpdateToolTip = string.Format(CanInstall(_update) ? Strings.UpdateInstallToolTip : Strings.UpdateDownloadToolTip, version);
        UpdateButtonText = "v" + version;
    }

    private static bool CanInstall(AppUpdate update) => AppUpdate.IsInstalled && update.InstallerUrl != null;

    [RelayCommand]
    private async Task Update()
    {
        if (_update == null)
            return;

        ErrorMessage = string.Empty;
        string versionText = UpdateButtonText;
        try
        {
            if (!CanInstall(_update))
                _update.OpenPage();
            else
            {
                // Пока идёт загрузка, вместо версии на кнопке процент
                UpdateButtonText = "0";
                if (await _update.InstallAsync(new Progress<int>(percent => UpdateButtonText = percent.ToString())))
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(Application.Current.Shutdown);
                    return;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException
                                      or TaskCanceledException or Win32Exception)
        {
            ErrorMessage = Strings.UpdateFailedText;
        }
        UpdateButtonText = versionText;
    }

    [RelayCommand]
    private void ConnectWithServer() => Connect(true);

    [RelayCommand]
    private void ConnectWithoutServer() => Connect(false);

    private void Connect(bool withServer)
    {
        IsWaitingForConnection = true;
        ErrorMessage = string.Empty;
        Settings.Default.Nickname = UsernameTextBoxValue;
        Settings.Default.Save();

        Task.Run(() =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var username = UsernameTextBoxValue.Trim();
                    var usernameError = CheckUsername(username);
                    if (usernameError != null) { SetError(usernameError); return; }

                    // Непустая ссылка от прошлой попытки выдала бы панель хоста
                    // при гостевом входе.
                    _serverHost = null;

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
                            _serverHost = null;
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
                        _serverHost = null;
                        _clientHost = null;
                        return;
                    }

                    _clientHost.SetConnectionKey(connectionKey);
                    _clientHost.SubscribeToClientDisconnected(_clientDisconnectedHandler);

                    if (_serverHost != null)
                        _clientHost.AttachServerAdmin(_serverHost.Admin);

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

        _clientHost = null;
        _serverHost = null;

        IsVisible = true;
    }
}
