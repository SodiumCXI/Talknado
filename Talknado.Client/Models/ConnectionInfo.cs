using CommunityToolkit.Mvvm.ComponentModel;

namespace Talknado.Client.Models;

public interface IConnectionInfo
{
    string ServerIP { get; set; }
    int Port { get; set; }
    int ServerPort { get; set; }
    bool ConnectionState { get; set; }
    ushort LocalUserId { get; set; }
    string ConnectionKey { get; set; }
    string FormattedConnectionKey { get; set; }
}
public partial class ConnectionInfo : ObservableObject, IConnectionInfo
{
    public string ServerIP { get; set; } = string.Empty;
    public int Port { get; set; }
    public int ServerPort { get; set; }
    public bool ConnectionState { get; set; } = false;
    public ushort LocalUserId { get; set; }

    [ObservableProperty]
    private string _connectionKey = string.Empty;

    [ObservableProperty]
    private string _formattedConnectionKey = string.Empty;

    partial void OnConnectionKeyChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            FormattedConnectionKey = string.Empty;
            return;
        }

        int questionMarkIndex = value.IndexOf('?');
        if (questionMarkIndex >= 0)
        {
            FormattedConnectionKey = value.Substring(0, questionMarkIndex + 1) + "...";
        }
        else
        {
            FormattedConnectionKey = value;
        }
    }
}