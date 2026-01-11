using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Talknado.Server.Core;

public interface IServerInfo
{
    public int Port { get; set; }
    string GetServerVersion();
    ImmutableArray<string> GetValidClientVersions();
    bool VerifyPassword(byte[] passwordHashBytes);
    void SetServerPassword(string password);
}

public class ServerInfo : IServerInfo
{
    private readonly string _serverVersion = "v1.1.3";
    private readonly ImmutableArray<string> _validClientVersions = ["v1.3.2", "v1.3.3"];
    private byte[]? _passwordHash = null;

    public int Port { get; set; } = 37535;

    public string GetServerVersion()
    {
        return _serverVersion;
    }

    public ImmutableArray<string> GetValidClientVersions()
    {
        return _validClientVersions;
    }

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