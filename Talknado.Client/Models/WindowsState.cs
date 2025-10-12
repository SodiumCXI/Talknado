using CommunityToolkit.Mvvm.ComponentModel;

namespace Talknado.Client.Models;

public interface IWindowsState
{
    event Action? ClientDisconnected;
    bool ScreenShareWindowIsVisible { get; set; }
    void InvokeClientDisconnected();
}

public partial class WindowsState : ObservableObject, IWindowsState
{
    public event Action? ClientDisconnected;

    [ObservableProperty]
    private bool _screenShareWindowIsVisible;

    public void InvokeClientDisconnected()
    {
        ClientDisconnected?.Invoke();
    }
}
