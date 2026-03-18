using Microsoft.Extensions.DependencyInjection;
using Talknado.Server.Core;
using Talknado.Server.Core.Helpers;
using Talknado.Server.Infrastructure;

namespace Talknado.Server;

public sealed class ServerHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly PortForwardingHelper _forwardingHelper = new();
    private int _port;
    private bool _isPortForwarded;

    public ServerHost()
    {
        var services = new ServiceCollection().RegisterCoreServices();
        _provider = services.BuildServiceProvider();

        foreach (var descriptor in services.Where(d => d.Lifetime == ServiceLifetime.Singleton))
        {
            _provider.GetService(descriptor.ServiceType);
        }
    }

    public (bool, string) StartServer(string password, bool withPortForwarding)
    {
        var serverManager = _provider.GetRequiredService<IServerManager>();
        var serverResult = password != string.Empty ? serverManager.Start(password) : serverManager.Start(null);

        if (serverResult.Item1)
            return serverResult;

        if (withPortForwarding)
        {
            _port = _provider.GetRequiredService<IServerInfo>().Port;
            var forwardingResult = Task.Run(() => _forwardingHelper.EnsurePortForwardedAsync(_port)).GetAwaiter().GetResult();
            if (forwardingResult != null)
                return (true, forwardingResult);
            _isPortForwarded = true;
        }

        return serverResult;
    }

    public void Dispose()
    {
        _provider.Dispose();

        if (_isPortForwarded)
            Task.Run(() => _forwardingHelper.RemovePortForwardingAsync(_port)).GetAwaiter().GetResult();
    }
}