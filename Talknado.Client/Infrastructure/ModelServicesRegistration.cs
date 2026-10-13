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
        services.AddSingleton<IHostPanelManager, HostPanelManager>();
        services.AddSingleton<IScreenMonitorManager, ScreenMonitorManager>();
        services.AddSingleton<IConnectionInfo, ConnectionInfo>();
        services.AddSingleton<IUsersInfo, UsersInfo>();
        services.AddSingleton<IMessagesManager, MessagesManager>();
        services.AddSingleton<IFileChannel, FileChannel>();
        services.AddSingleton<IFileTransferManager, FileTransferManager>();
        services.AddSingleton<IMediaViewerManager, MediaViewerManager>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INetworkUtils, NetworkUtils>();
        services.AddSingleton<ICryptoSessionManager, CryptoSessionManager>();
        services.AddSingleton<IUsersAudioPlayer, UsersAudioPlayer>();
        services.AddSingleton<IAudioManager, AudioManager>();
        services.AddSingleton<IClientManager, ClientManager>();
        services.AddSingleton<IScreenShareManager, ScreenShareManager>();
        services.AddSingleton<IScreenSharePlayer, ScreenSharePlayer>();

        services.AddSingleton<MonitorSelectionViewModel>();
        services.AddSingleton(sp => new MonitorSelectionWindow
        {
            DataContext = sp.GetRequiredService<MonitorSelectionViewModel>()
        });

        services.AddSingleton<SettingsWindowViewModel>();
        services.AddSingleton(sp => new SettingsWindow
        {
            DataContext = sp.GetRequiredService<SettingsWindowViewModel>()
        });

        services.AddSingleton<HostPanelViewModel>();
        services.AddSingleton(sp => new HostPanelWindow
        {
            DataContext = sp.GetRequiredService<HostPanelViewModel>()
        });

        services.AddSingleton<MediaViewerViewModel>();
        services.AddSingleton(sp => new MediaViewerWindow
        {
            DataContext = sp.GetRequiredService<MediaViewerViewModel>()
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