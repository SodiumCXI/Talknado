using Microsoft.Extensions.DependencyInjection;
using Talknado.Server.Core;

namespace Talknado.Server.Infrastructure;

public static class CoreServicesRegistration
{
    public static IServiceCollection RegisterCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IServerInfo, ServerInfo>();
        services.AddSingleton<ICryptoSessionManager, CryptoSessionManager>();
        services.AddSingleton<IUsersInfo, UsersInfo>();
        services.AddSingleton<INetworkUtils, NetworkUtils>();
        services.AddSingleton<AudioManager>();
        services.AddSingleton<IScreenShareManager, ScreenShareManager>();
        services.AddSingleton<IClientManager, ClientManager>();
        services.AddSingleton<IServerManager, ServerManager>();

        return services;
    }
}