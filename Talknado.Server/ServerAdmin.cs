using Talknado.Server.Core;

namespace Talknado.Server;

/// <summary>
/// Права хоста над своей сессией. Хост держит сервер в своём же процессе,
/// поэтому команд администрирования по сети нет и подделать хоста нечем.
/// </summary>
public sealed class ServerAdmin(IUsersInfo usersInfo, IClientManager clientManager, IServerInfo serverInfo)
{
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly IClientManager _clientManager = clientManager;
    private readonly IServerInfo _serverInfo = serverInfo;

    /// <summary>Потолок качества демонстрации: 0 — 720p30, 1 — 1080p30, 2 — 1080p60.</summary>
    public byte MaxShareQuality
    {
        get => _serverInfo.MaxShareQuality;
        set
        {
            if (_serverInfo.MaxShareQuality == value)
                return;

            _serverInfo.MaxShareQuality = value;

            // Кодировщик берёт ступень один раз при запуске, поэтому идущая
            // демонстрация останавливается.
            _clientManager.StopScreenShare();
        }
    }

    /// <summary>Общий поток пересылки файлов, байт в секунду; 0 — без ограничения.</summary>
    public long FileBandwidth
    {
        get => _serverInfo.FileBandwidth;
        set => _serverInfo.FileBandwidth = value;
    }

    public IReadOnlyList<SessionUser> GetUsers() => _usersInfo.GetSessionUsers();

    public void Kick(ushort userId) => _clientManager.KickUser(userId);
}
