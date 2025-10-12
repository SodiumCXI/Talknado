using System.IO;
using System.Net.Sockets;

namespace Talknado.Client.Models.Helpers;

public static class NetworkExceptionHelper
{
    public static bool IsNetworkException(Exception ex)
    {
        if (ex is SocketException)
            return true;

        if (ex is IOException)
            return true;

        return false;
    }
}