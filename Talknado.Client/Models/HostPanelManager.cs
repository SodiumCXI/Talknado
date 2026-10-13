using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using Talknado.Client.Models.Helpers.ScreenShare;
using Talknado.Client.Properties;
using Talknado.Server;
using Talknado.Server.Core;

namespace Talknado.Client.Models;

public interface IHostPanelManager
{
    bool IsHost { get; }
    bool IsWindowVisible { get; set; }
    int MaxShareQuality { get; set; }
    int FileBandwidthMbits { get; set; }
    ObservableCollection<HostPanelManager.SessionUserItem> Users { get; }
    void Attach(ServerAdmin admin);
    void Refresh();
    void Kick(ushort userId);
}

/// <summary>
/// Панель хоста говорит с сервером напрямую: сервер живёт в этом же процессе.
/// У гостя объект остаётся пустым, и кнопка панели не показывается.
/// </summary>
public partial class HostPanelManager(IConnectionInfo connectionInfo) : ObservableObject, IHostPanelManager
{
    private readonly IConnectionInfo _connectionInfo = connectionInfo;

    private ServerAdmin? _admin;

    [ObservableProperty]
    private bool _isWindowVisible;

    [ObservableProperty]
    private int _maxShareQuality;

    private const long BytesPerMbit = 1_000_000 / 8;

    /// <summary>Предел передачи файлов, Мбит/с; 0 — без ограничения.</summary>
    [ObservableProperty]
    private int _fileBandwidthMbits;

    public ObservableCollection<SessionUserItem> Users { get; } = [];

    public bool IsHost => _admin != null;

    // Значения хранятся в настройках, как прочие настройки приложения, и при
    // каждом новом сервере отдаются ему заново: сервер их не помнит.
    public void Attach(ServerAdmin admin)
    {
        _admin = admin;
        MaxShareQuality = Math.Min(Settings.Default.HostShareQuality, ShareQuality.Levels - 1);
        FileBandwidthMbits = Settings.Default.HostFileBandwidthMbits;
        admin.MaxShareQuality = (byte)MaxShareQuality;
        admin.FileBandwidth = FileBandwidthMbits * BytesPerMbit;
        OnPropertyChanged(nameof(IsHost));
    }

    // Список правится на месте: полная замена коллекции перестраивала бы
    // разметку раз в секунду.
    public void Refresh()
    {
        if (_admin == null)
            return;

        var sessionUsers = _admin.GetUsers();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            for (int i = Users.Count - 1; i >= 0; i--)
            {
                if (!sessionUsers.Any(u => u.Id == Users[i].Id))
                    Users.RemoveAt(i);
            }

            foreach (var sessionUser in sessionUsers)
            {
                var item = Users.FirstOrDefault(u => u.Id == sessionUser.Id);

                if (item == null)
                {
                    item = new SessionUserItem(sessionUser);
                    Users.Add(item);
                }
                else
                {
                    item.Update(sessionUser);
                }

                item.IsLocal = sessionUser.Id == _connectionInfo.LocalUserId;
            }
        });
    }

    public void Kick(ushort userId)
    {
        _admin?.Kick(userId);
        Refresh();
    }

    partial void OnMaxShareQualityChanged(int value)
    {
        if (_admin != null)
            _admin.MaxShareQuality = (byte)value;
        Settings.Default.HostShareQuality = value;
        Settings.Default.Save();
    }

    partial void OnFileBandwidthMbitsChanged(int value)
    {
        if (_admin != null)
            _admin.FileBandwidth = value * BytesPerMbit;
        Settings.Default.HostFileBandwidthMbits = value;
        Settings.Default.Save();
    }

    public partial class SessionUserItem : ObservableObject
    {
        [ObservableProperty]
        private string _username = string.Empty;

        [ObservableProperty]
        private string _address = string.Empty;

        [ObservableProperty]
        private bool _isLocal;

        public ushort Id { get; }

        public SessionUserItem(SessionUser sessionUser)
        {
            Id = sessionUser.Id;
            Update(sessionUser);
        }

        public void Update(SessionUser sessionUser)
        {
            Username = sessionUser.Username;
            Address = sessionUser.Address;
        }
    }
}
