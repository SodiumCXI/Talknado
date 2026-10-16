using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Talknado.Server.Core;

namespace Talknado.Server.Linux;

/// <summary>
/// serve [--control PATH] [--max-quality N] [--bandwidth MBIT]
///     Пароль — из TALKNADO_PASSWORD: аргументы видны в ps. С сокетом управления
///     ключ отдаёт команда key, а не вывод: в ключе пароль, а вывод уходит в журнал.
/// ctl PATH COMMAND [ARG]
///     Передаёт команду работающему серверу через его сокет управления.
/// version
/// </summary>
static class Program
{
    static int Main(string[] args) => args.FirstOrDefault() switch
    {
        "serve" => Serve(args[1..]),
        "ctl" when args.Length >= 3 => SendCommand(args[1], string.Join(' ', args[2..])),
        "version" => PrintVersion(),
        _ => Usage()
    };

    static int Serve(string[] args)
    {
        string? controlPath = null;
        byte quality = 2;
        long bandwidthMbits = 0;

        for (int i = 0; i < args.Length; i += 2)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            bool ok = args[i] switch
            {
                "--control" => (controlPath = value) != null,
                "--max-quality" => SessionCommands.TryParseQuality(value, out quality),
                "--bandwidth" => long.TryParse(value, out bandwidthMbits) && bandwidthMbits >= 0,
                _ => false
            };
            if (!ok)
                return Usage();
        }

        using var host = new ServerHost();
        var admin = host.Admin;
        admin.MaxShareQuality = quality;
        admin.FileBandwidth = bandwidthMbits * SessionCommands.BytesPerMbit;

        // Сокет занимается до запуска: ключ на выходе должен означать, что
        // сервер поднялся целиком.
        var commands = new SessionCommands(host);
        ControlSocket? control = null;
        if (controlPath != null)
        {
            try
            {
                control = new ControlSocket(controlPath, commands);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: control socket {controlPath}: {ex.Message}");
                return 1;
            }
        }
        using var _ = control;

        var (failed, result) = host.StartServer(Environment.GetEnvironmentVariable("TALKNADO_PASSWORD") ?? string.Empty, withPortForwarding: false);
        if (failed)
        {
            Console.Error.WriteLine(result);
            return 1;
        }
        commands.Key = result;
        Console.WriteLine(control != null ? $"Talknado Server started on port {host.Port}" : result);

        // Без своих обработчиков SIGTERM завершил бы процесс, не закрыв
        // соединения, и клиенты узнали бы об остановке только по таймауту.
        using var stop = new ManualResetEventSlim();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });
        stop.Wait();

        return 0;
    }

    static int SendCommand(string controlPath, string command)
    {
        string reply;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(controlPath));
            using var stream = new NetworkStream(socket);
            stream.Write(Encoding.UTF8.GetBytes(command + "\n"));
            socket.Shutdown(SocketShutdown.Send);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            reply = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
        {
            Console.Error.WriteLine("Error: server is not running");
            return 2;
        }

        bool isError = reply.StartsWith(SessionCommands.ErrorPrefix, StringComparison.Ordinal);
        (isError ? Console.Error : Console.Out).WriteLine(reply);
        return isError ? 1 : 0;
    }

    static int PrintVersion()
    {
        var version = typeof(ServerInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);
        Console.WriteLine($"Talknado Server v{version.ToString(3)}");
        Console.WriteLine($"Supported clients: v{ServerInfo.MinClientVersion.ToString(3)} and newer");
        return 0;
    }

    static int Usage()
    {
        Console.Error.WriteLine("Usage: talknado-server-bin serve [--control PATH] [--max-quality 720p30|1080p30|1080p60] [--bandwidth MBIT]");
        Console.Error.WriteLine("       talknado-server-bin ctl PATH key|status|users|kick ID|max-quality [VALUE]|bandwidth [MBIT]");
        Console.Error.WriteLine("       talknado-server-bin version");
        return 1;
    }
}
