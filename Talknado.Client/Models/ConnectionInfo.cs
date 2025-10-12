using CommunityToolkit.Mvvm.ComponentModel;

namespace Talknado.Client.Models;

public interface IConnectionInfo
{
    string ServerIP { get; set; }
    int ServerPort { get; set; }
    bool ConnectionState { get; set; }
    ushort LocalUserId { get; set; }
    string ConnectionKey { get; set; }
}
public partial class ConnectionInfo : ObservableObject, IConnectionInfo
{
    public string ServerIP { get; set; } = string.Empty;
    public int ServerPort { get; set; }
    public bool ConnectionState { get; set; } = false;
    public ushort LocalUserId { get; set; }

    [ObservableProperty]
    private string _connectionKey = string.Empty;
}