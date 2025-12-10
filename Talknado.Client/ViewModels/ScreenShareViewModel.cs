using CommunityToolkit.Mvvm.ComponentModel;
using Talknado.Client.Models;

namespace Talknado.Client.ViewModels;

public partial class ScreenShareViewModel(IScreenSharePlayer screenSharingPlayer, IUsersInfo usersInfo) : ObservableObject
{
    private readonly IScreenSharePlayer _screenSharingPlayer = screenSharingPlayer;
    private readonly IUsersInfo _usersInfo = usersInfo;
    public IScreenSharePlayer ScreenSharePlayer => _screenSharingPlayer;
    public IUsersInfo UsersInfo => _usersInfo;
}
