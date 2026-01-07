using Microsoft.Extensions.DependencyInjection;
using Talknado.Server.Core;
using Talknado.Server.Infrastructure;

namespace Talknado.Server;

public sealed class ServerHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public ServerHost()
    {
        var services = new ServiceCollection().RegisterCoreServices();
        _provider = services.BuildServiceProvider();

        foreach (var descriptor in services.Where(d => d.Lifetime == ServiceLifetime.Singleton))
        {
            _provider.GetService(descriptor.ServiceType);
        }
    }

    public (bool, string) StartServer(string password)
    {
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
    }
}