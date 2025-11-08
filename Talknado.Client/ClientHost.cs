using Microsoft.Extensions.DependencyInjection;
using Talknado.Client.Infrastructure;
using Talknado.Client.Models;
using Talknado.Client.Views;

namespace Talknado.Client;

public sealed class ClientHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public ClientHost()
    {
        var services = new ServiceCollection().RegisterModelServices();
        _provider = services.BuildServiceProvider();

        foreach (var descriptor in services.Where(d => d.Lifetime == ServiceLifetime.Singleton))
        {
            _provider.GetService(descriptor.ServiceType);
        }
    }

    public string? TryConnectToServer(string connectionKey, string username)
    {
        var clientManager = _provider.GetRequiredService<IClientManager>();
        var result = clientManager.TryConnect(connectionKey, username);
        if (result == null)
            _provider.GetRequiredService<IMainWindow>().Show();
        return result;
    }

    public void CloseConnection()
    {
        var clientManager = _provider.GetRequiredService<IClientManager>();
        clientManager.CloseConnection();
    }

    public void SubscribeToClientDisconnected(Action action)
    {
        var windowsState = _provider.GetRequiredService<IWindowsState>();
        windowsState.ClientDisconnected += action;
    }

    public void UnsubscribeFromClientDisconnected(Action action)
    {
        var windowsState = _provider.GetRequiredService<IWindowsState>();
        windowsState.ClientDisconnected -= action;
    }

    public void SetConnectionKey(string conectionKey)
    {
        var connectionInfo = _provider.GetRequiredService<IConnectionInfo>();
        connectionInfo.ConnectionKey = conectionKey;
    }

    public void Dispose()
    {
        _provider.Dispose();
    }
}