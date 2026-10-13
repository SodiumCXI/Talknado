using System.Security.Cryptography;
using System.Text;

namespace Talknado.Server.Core;

public interface IServerInfo
{
    public int Port { get; set; }
    byte MaxShareQuality { get; set; }
    long FileBandwidth { get; set; }
    Version ServerVersion { get; }
    bool IsClientVersionSupported(string clientVersion);
    bool VerifyPassword(byte[] passwordHashBytes);
    void SetServerPassword(string password);
    void BanAddress(string address);
    bool IsAddressBanned(string address);
}

public class ServerInfo : IServerInfo
{
    /// <summary>
    /// Самая старая версия клиента, с которой совместим протокол сервера.
    /// Поднимается только при несовместимой правке протокола; новые версии
    /// клиента вписывать не нужно — проходит всё, что не старше этой.
    /// </summary>
    private static readonly Version MinClientVersion = new(2, 0, 0);
    private byte[]? _passwordHash = null;

    private readonly HashSet<string> _bannedAddresses = [];

    public int Port { get; set; }

    /// <summary>Потолок качества демонстрации, который сервер выдаёт в ответе #YYC.</summary>
    public byte MaxShareQuality { get; set; }

    /// <summary>Общий поток пересылки файлов, байт в секунду; 0 — без ограничения.</summary>
    public long FileBandwidth { get; set; }

    public void BanAddress(string address)
    {
        if (address != string.Empty)
        {
            lock (_bannedAddresses)
                _bannedAddresses.Add(address);
        }
    }

    public bool IsAddressBanned(string address)
    {
        lock (_bannedAddresses)
            return _bannedAddresses.Contains(address);
    }

    /// <summary>Версия сборки сервера, из файла проекта.</summary>
    public Version ServerVersion { get; } = Normalize(typeof(ServerInfo).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Клиент шлёт версию строкой вида «v2.0.0».</summary>
    public bool IsClientVersionSupported(string clientVersion) =>
        Version.TryParse(clientVersion.TrimStart('v'), out var version) && Normalize(version) >= MinClientVersion;

    // Три числа: у версии из сборки четвёртое — 0, у разобранной строки его нет,
    // и без приведения 2.0.0.0 и 2.0.0 сравнивались бы как разные.
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    public bool VerifyPassword(byte[] passwordHashBytes)
    {
        if (_passwordHash == null)
            return true;

        return CryptographicOperations.FixedTimeEquals(_passwordHash, passwordHashBytes);
    }

    public void SetServerPassword(string? password)
    {
        if (password != null)
            _passwordHash = GetSha256Bytes(password);
    }

    private static byte[] GetSha256Bytes(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        return SHA256.HashData(bytes);
    }
}