using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public interface IScreenShareManager
{
    ushort ScreenSharerId { get; set; }
}

public class ScreenShareManager : IScreenShareManager, IDisposable
{
    private readonly CancellationTokenSource _screenShareTokenSource;
    private readonly Thread _screenShareThread;

    private readonly INetworkUtils _networkUtils;

    public ushort ScreenSharerId { get; set; } = 0;

    public ScreenShareManager(INetworkUtils networkUtils)
    {
        _networkUtils = networkUtils;

        _screenShareTokenSource = new();
        _screenShareThread = new(() => HandleScreenShare(_screenShareTokenSource.Token))
        {
            IsBackground = true
        };
        _screenShareThread.Start();
    }

    private void HandleScreenShare(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var (packet, userId) = _networkUtils.ReceiveScreenSharePacket(token);
                try
                {
                    if (userId == ScreenSharerId)
                        _networkUtils.BroadcastScreenSharePacket(userId, packet.Span);
                }
                finally
                {
                    packet.Return();
                }
            }
            catch (Exception ex) when (NetworkExceptionHelper.IsNetworkException(ex)) { /* ignore */ }
            catch
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _screenShareTokenSource?.Cancel();
        _screenShareThread?.Join();
        _screenShareTokenSource?.Dispose();

        GC.SuppressFinalize(this);
    }
}