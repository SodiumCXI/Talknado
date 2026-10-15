using System.Text;

namespace Talknado.Server.Linux;

/// <summary>
/// Настройки сессии — то же, что панель хоста в приложении: участники,
/// исключение, потолок качества демонстрации и предел скорости файлов.
/// Команда — одна строка, ответ — текст; ошибка начинается с «Error:».
/// </summary>
public sealed class SessionCommands(ServerHost host)
{
    public const string ErrorPrefix = "Error:";
    public const long BytesPerMbit = 1_000_000 / 8;

    private static readonly string[] QualityNames = ["720p30", "1080p30", "1080p60"];

    private readonly ServerHost _host = host;
    private readonly ServerAdmin _admin = host.Admin;

    /// <summary>Ключ подключения; до конца запуска сервера — null.</summary>
    public string? Key { get; set; }

    public static bool TryParseQuality(string? value, out byte quality)
    {
        int index = Array.IndexOf(QualityNames, value?.ToLowerInvariant());
        if (index < 0 && byte.TryParse(value, out var number) && number < QualityNames.Length)
            index = number;

        quality = (byte)Math.Max(index, 0);
        return index >= 0;
    }

    public string Execute(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var argument = parts.Length > 1 ? parts[1] : null;

        return parts.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "key" => Key ?? $"{ErrorPrefix} Server is starting",
            "status" => Status(),
            "users" => Users(),
            "kick" => Kick(argument),
            "quality" => Quality(argument),
            "bandwidth" => Bandwidth(argument),
            _ => $"{ErrorPrefix} Unknown command"
        };
    }

    private string Status() =>
        $"Port: {_host.Port}\n" +
        $"Users: {_admin.GetUsers().Count}\n" +
        $"Screen share quality: {QualityNames[Math.Min(_admin.MaxShareQuality, QualityNames.Length - 1)]}\n" +
        $"File bandwidth: {BandwidthText()}";

    private string Users()
    {
        var users = _admin.GetUsers();
        if (users.Count == 0)
            return "No users";

        var text = new StringBuilder($"{"ID",-6}{"Ping",-7}{"Address",-40}Name");
        foreach (var user in users)
        {
            var ping = user.Ping < 0 ? "-" : user.Ping > 999 ? ">999" : user.Ping.ToString();
            text.Append($"\n{user.Id,-6}{ping,-7}{user.Address,-40}{user.Username}");
        }
        return text.ToString();
    }

    private string Kick(string? argument)
    {
        if (!ushort.TryParse(argument, out var userId))
            return $"{ErrorPrefix} Usage: kick ID (see 'users')";

        var user = _admin.GetUsers().FirstOrDefault(u => u.Id == userId);
        if (user.Id == 0)
            return $"{ErrorPrefix} No user with ID {userId}";

        _admin.Kick(userId);
        return $"Kicked {user.Username}; address {user.Address} is banned until restart";
    }

    private string Quality(string? argument)
    {
        if (argument != null)
        {
            if (!TryParseQuality(argument, out var quality))
                return $"{ErrorPrefix} Quality must be one of: {string.Join(", ", QualityNames)}";

            _admin.MaxShareQuality = quality;
        }
        return $"Screen share quality: {QualityNames[Math.Min(_admin.MaxShareQuality, QualityNames.Length - 1)]}";
    }

    private string Bandwidth(string? argument)
    {
        if (argument != null)
        {
            if (!long.TryParse(argument, out var mbits) || mbits < 0)
                return $"{ErrorPrefix} Bandwidth must be a whole number of Mbit/s, 0 = unlimited";

            _admin.FileBandwidth = mbits * BytesPerMbit;
        }
        return $"File bandwidth: {BandwidthText()}";
    }

    private string BandwidthText() =>
        _admin.FileBandwidth == 0 ? "unlimited" : $"{_admin.FileBandwidth / BytesPerMbit} Mbit/s";
}
