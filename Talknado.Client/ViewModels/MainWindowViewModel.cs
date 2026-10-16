using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using Talknado.Client.Models;
using Talknado.Client.Models.Helpers.Media;

namespace Talknado.Client.ViewModels;

public partial class MainWindowViewModel(IClientManager clientManager,
    IUsersInfo usersInfo, IMessagesManager messagesManager, IScreenShareManager screenShareManager,
    IConnectionInfo connectionInfo, IScreenSharePlayer screenSharePlayer, IAudioManager audioManager,
    ISettingsManager settingsManager, IScreenMonitorManager screenMonitorManager,
    IHostPanelManager hostPanelManager, IFileTransferManager fileTransferManager,
    IMediaViewerManager mediaViewerManager, IDialogService dialogService) : ObservableObject
{
    private readonly IFileTransferManager _fileTransferManager = fileTransferManager;
    private readonly IMediaViewerManager _mediaViewerManager = mediaViewerManager;
    private readonly IDialogService _dialogService = dialogService;

    private readonly IHostPanelManager _hostPanelManager = hostPanelManager;

    private readonly IClientManager _clientManager = clientManager;
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly IMessagesManager _messagesManager = messagesManager;
    private readonly IScreenShareManager _screenShareManager = screenShareManager;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;
    private readonly IScreenSharePlayer _screenSharePlayer = screenSharePlayer;
    private readonly IAudioManager _audioManager = audioManager;
    private readonly ISettingsManager _settingsManager = settingsManager;
    private readonly IScreenMonitorManager _screenMonitorManager = screenMonitorManager;

    public IScreenShareManager ScreenShareManager => _screenShareManager;
    public IConnectionInfo ConnectionInfo => _connectionInfo;
    public IAudioManager AudioManager => _audioManager;
    public IHostPanelManager HostPanelManager => _hostPanelManager;

    public ObservableCollection<MessagesManager.Message> Messages => _messagesManager.Messages;
    public ObservableCollection<UsersInfo.UserItem> Users => _usersInfo.Users;

    [ObservableProperty]
    private string _inputTextBoxValue = string.Empty;
    public bool UseDisconnect { get; set; } = false;

    [ObservableProperty]
    private PendingAttachment? _pendingAttachment;

    [RelayCommand]
    private async Task SendMessage()
    {
        var text = InputTextBoxValue.Trim();
        var pending = PendingAttachment;

        if (pending == null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            _clientManager.SendMessage(InputTextBoxValue);
            _messagesManager.AddMessage(_connectionInfo.LocalUserId, InputTextBoxValue);
            InputTextBoxValue = string.Empty;
            return;
        }

        PendingAttachment = null;
        InputTextBoxValue = string.Empty;

        var probe = await pending.Probe;
        var attachment = _fileTransferManager.Share(pending.Path, probe, text);
        _messagesManager.AddMessage(_connectionInfo.LocalUserId, text, attachment);
    }

    [RelayCommand]
    private void AttachFile()
    {
        if (_dialogService.PickFileToAttach() is { } path)
            Attach(path);
    }

    public void Attach(string path)
    {
        if (!File.Exists(path))
            return;

        PendingAttachment = new PendingAttachment(path);
    }

    [RelayCommand]
    private void RemovePendingAttachment()
    {
        PendingAttachment = null;
    }

    [RelayCommand]
    private void OpenAttachment(Attachment? attachment)
    {
        if (attachment == null)
            return;

        if (!attachment.IsFile)
        {
            _mediaViewerManager.Open(attachment);
            return;
        }

        // Скачанный или свой файл открывается программой по умолчанию.
        if (attachment.State == AttachmentState.Ready && attachment.LocalPath != null)
            _dialogService.OpenFile(attachment.LocalPath);
        else if (attachment.State == AttachmentState.Remote)
            _fileTransferManager.SaveToDownloads(attachment);
        else if (attachment.State == AttachmentState.Downloading)
            _fileTransferManager.CancelDownload(attachment);
    }

    [RelayCommand]
    private void SaveAttachment(Attachment? attachment)
    {
        if (attachment == null || attachment.State == AttachmentState.Failed)
            return;

        if (attachment.SavedPath != null)
            _dialogService.ShowInFolder(attachment.SavedPath);
        else
            _fileTransferManager.SaveToDownloads(attachment);
    }

    [RelayCommand]
    private void CloseConnection()
    {
        _clientManager.CloseConnection();
    }

    [RelayCommand]
    private void ToggleMicrophone()
    {
        _audioManager.ToggleMicrophoneStatus();
    }

    [RelayCommand]
    private void ViewMonitorSelection()
    {
        if (!_screenShareManager.IsSharing)
        {
            _screenMonitorManager.CaptureAll();

            if (_screenMonitorManager.Monitors.Count == 1)
            {
                _clientManager.ToggleScreenShare();
            }
            else
            {
                _screenMonitorManager.IsWindowVisible = true;
            }
        }
        else
            _clientManager.ToggleScreenShare();
    }

    [RelayCommand]
    private void ViewScreenShare()
    {
        _screenSharePlayer.IsWindowVisible = true;
    }

    [RelayCommand]
    private void ViewHostPanel()
    {
        _hostPanelManager.IsWindowVisible = true;
    }

    [RelayCommand]
    private void ViewSettings()
    {
        _settingsManager.IsWindowVisible = true;
    }

    [RelayCommand]
    private void SoftReboot()
    {
        UseDisconnect = true;
        _connectionInfo.InvokeClientDisconnected();
    }

    [RelayCommand]
    private static void OpenLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/SodiumCXI/Talknado",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть ссылку: {ex.Message}");
        }
    }
}

public partial class PendingAttachment : ObservableObject
{
    public string Path { get; }
    public string FileName { get; }
    public string SizeText { get; }
    public Task<ProbeResult> Probe { get; }

    [ObservableProperty]
    private ImageSource? _thumbnail;

    public PendingAttachment(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        SizeText = Attachment.FormatSize(new FileInfo(path).Length);
        Probe = Task.Run(() => MediaProbe.Probe(path));
        Probe.ContinueWith(t => Thumbnail = t.Result.Thumbnail, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
