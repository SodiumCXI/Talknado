using Microsoft.Extensions.DependencyInjection;
using Talknado.Client.Models;
using Talknado.Client.ViewModels;
using Talknado.Client.Views;

namespace Talknado.Client.Infrastructure;

public static class ModelServicesRegistration
{
    public static IServiceCollection RegisterModelServices(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<IWindowsState, WindowsState>();
        services.AddSingleton<IConnectionInfo, ConnectionInfo>();
        services.AddSingleton<IUsersInfo, UsersInfo>();
        services.AddSingleton<IMessagesManager, MessagesManager>();
        services.AddSingleton<INetworkUtils, NetworkUtils>();
        services.AddSingleton<ICryptoSessionManager, CryptoSessionManager>();
        services.AddSingleton<IUsersAudioPlayer, UsersAudioPlayer>();
        services.AddSingleton<IAudioManager, AudioManager>();
        services.AddSingleton<IClientManager, ClientManager>();
        services.AddSingleton<IScreenShareManager, ScreenShareManager>();
        services.AddSingleton<IScreenSharePlayer, ScreenSharePlayer>();

        services.AddSingleton<SettingsWindowViewModel>();
        services.AddSingleton(sp => new SettingsWindow
        {
            DataContext = sp.GetRequiredService<SettingsWindowViewModel>()
        });

        services.AddSingleton<ScreenShareViewModel>();
        services.AddSingleton(sp => new ScreenShareWindow
        {
            DataContext = sp.GetRequiredService<ScreenShareViewModel>()
        });

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<IMainWindow>(sp => new MainWindow
        {
            DataContext = sp.GetRequiredService<MainWindowViewModel>()
        });

        return services;
    }
}