using System.Net;
using System.Text;
using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public class AccessManager : IDisposable
{
    private readonly CancellationTokenSource _accessTokenSource;
    private readonly Thread _accessThread;

    private readonly IUsersInfo _usersInfo;
    private readonly INetworkUtils _networkUtils;
    private readonly ICryptoSessionManager _cryptoSessionManager;

    public AccessManager(IUsersInfo usersInfo, INetworkUtils networkUtils, ICryptoSessionManager cryptoSessionManager)
    {
        _usersInfo = usersInfo;
        _networkUtils = networkUtils;
        _cryptoSessionManager = cryptoSessionManager;

        _accessTokenSource = new();
        _accessThread = new(() => HandleAccess(_accessTokenSource.Token))
        {
            IsBackground = true
        };
        _accessThread.Start();
    }

    private void HandleAccess(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var context = _networkUtils.ReceiveAccessPacketAsync(token).GetAwaiter().GetResult();
                if (context == null || context.Value.Item1.Length != 32)
                    continue;

                var data = context.Value.Item1;
                byte[] decryptedData;
                try
                {
                    decryptedData = _cryptoSessionManager.DecryptMessage(data);
                }
                catch
                {
                    continue;
                }

                var endPoint = context.Value.Item2;
                var command = Encoding.UTF8.GetString(decryptedData.AsSpan(..2));
                var userId = BitConverter.ToUInt16(decryptedData.AsSpan(2..));
                ExecuteCommand(command, userId, endPoint);
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex))
            {
                return;
            }
            catch { /* ignore */ }
        }
    }

    private void ExecuteCommand(string command, ushort userId, IPEndPoint endPoint)
    {
        switch (command)
        {
            case var _ when command.Equals("#A"):

                _usersInfo.UpdateUser(userId, endPoint, null);
                break;

            case var _ when command.Equals("#S"):

                _usersInfo.UpdateUser(userId, null, endPoint);
                break;

            default:

                break;
        }
    }

    public void Dispose()
    {
        _accessTokenSource?.Cancel();
        _accessThread?.Join();
        _accessTokenSource?.Dispose();

        GC.SuppressFinalize(this);
    }
}
