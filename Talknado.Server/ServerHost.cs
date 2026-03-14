using Microsoft.Extensions.DependencyInjection;
using Talknado.Server.Core;
using Talknado.Server.Core.Helpers;
using Talknado.Server.Infrastructure;

namespace Talknado.Server;

public sealed class ServerHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly PortForwardingHelper _forwardingHelper = new();
    private bool _isPortForwarded;
    private int _port;

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
        if (withPortForwarding)
        {
            _port = _provider.GetRequiredService<IServerInfo>().Port;

            var result = Task.Run(() => _forwardingHelper.EnsurePortForwardedAsync(_port)).GetAwaiter().GetResult();
            if (result != null)
                return (true, result);

            _isPortForwarded = true;
        }

        var serverManager = _provider.GetRequiredService<IServerManager>();
        if (password != string.Empty)
        {
            return serverManager.Start(password);
        }
        else
        {
            return serverManager.Start(null);
        }
    }

    public void Dispose()
    {
        _provider.Dispose();

        if (_isPortForwarded)
            Task.Run(() => _forwardingHelper.RemovePortForwardingAsync(_port)).GetAwaiter().GetResult();
    }
}