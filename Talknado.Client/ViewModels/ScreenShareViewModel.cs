using CommunityToolkit.Mvvm.ComponentModel;
using Talknado.Client.Models;

namespace Talknado.Client.ViewModels;

public partial class ScreenShareViewModel(IScreenSharePlayer screenSharingPlayer) : ObservableObject
{
    private readonly IScreenSharePlayer _screenSharingPlayer = screenSharingPlayer;

    public IScreenSharePlayer ScreenSharePlayer => _screenSharingPlayer;
}
