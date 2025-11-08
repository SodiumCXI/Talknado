using Talknado.Server.Core.Helpers;

namespace Talknado.Server.Core;

public class AudioManager : IDisposable
{
    private readonly CancellationTokenSource _audioTokenSource;
    private readonly Thread _audioThread;

    private readonly INetworkUtils _networkUtils;

    public AudioManager(INetworkUtils networkUtils)
    {
        _networkUtils = networkUtils;
        _audioTokenSource = new();
        _audioThread = new(() => HandleAudio(_audioTokenSource.Token))
        {
            IsBackground = true
        };
        _audioThread.Start();
    }

    private void HandleAudio(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var dataWithId = _networkUtils.ReceiveAudioPacketAsync(token).GetAwaiter().GetResult();
                if (dataWithId == null)
                    continue;

                var data = dataWithId.Value.Item1;
                var userId = dataWithId.Value.Item2;
                _networkUtils.BroadcastAudioPacket(userId, data, token).GetAwaiter().GetResult();
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
        _audioTokenSource?.Cancel();
        _audioThread?.Join();
        _audioTokenSource?.Dispose();

        GC.SuppressFinalize(this);
    }
}