using System.Net.Sockets;
using System.Text;

namespace Talknado.Server.Linux;

/// <summary>
/// Сокет управления работающим сервером: команды из скрипта talknado.
/// Это Unix-сокет в каталоге данных пользователя — по сети он недоступен,
/// а права на файл не пускают к нему других пользователей машины.
/// </summary>
public sealed class ControlSocket : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly SessionCommands _commands;

    public ControlSocket(string path, SessionCommands commands)
    {
        _path = path;
        _commands = commands;

        // Файл сокета остаётся после аварийного завершения и мешал бы занять путь.
        File.Delete(path);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(4);

        new Thread(AcceptLoop) { IsBackground = true, Name = "Control" }.Start();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            Socket client;
            try
            {
                client = _listener.Accept();
            }
            catch
            {
                return;
            }

            using (client)
                Handle(client);
        }
    }

    private void Handle(Socket client)
    {
        try
        {
            client.ReceiveTimeout = 2000;
            using var stream = new NetworkStream(client);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string reply;
            try
            {
                reply = _commands.Execute(reader.ReadLine() ?? string.Empty);
            }
            catch (Exception ex)
            {
                reply = $"{SessionCommands.ErrorPrefix} {ex.Message}";
            }

            stream.Write(Encoding.UTF8.GetBytes(reply));
        }
        catch { /* клиент ушёл, не дождавшись ответа */ }
    }

    public void Dispose()
    {
        _listener.Dispose();
        try { File.Delete(_path); } catch { /* ignore */ }
    }
}
