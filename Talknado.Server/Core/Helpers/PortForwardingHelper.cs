using Open.Nat;
using System.Net;

namespace Talknado.Server.Core.Helpers;

public sealed class PortForwardingHelper
{
    private readonly Dictionary<(int, Protocol), Mapping> _managed = [];
    private NatDevice? _device;

    public async Task<string?> EnsurePortForwardedAsync(int port, string description = "Talknado")
    {
        return await EnsurePortForwardedSingleAsync(port, Protocol.Tcp, description)
            ?? await EnsurePortForwardedSingleAsync(port, Protocol.Udp, description);
    }

    public async Task RemovePortForwardingAsync(int port)
    {
        await RemovePortForwardingSingleAsync(port, Protocol.Tcp);
        await RemovePortForwardingSingleAsync(port, Protocol.Udp);
    }

    public async Task<IPAddress> GetExternalIPAsync()
    {
        _device ??= await DiscoverAsync();
        return await _device.GetExternalIPAsync();
    }

    private async Task<string?> EnsurePortForwardedSingleAsync(int port, Protocol protocol, string description)
    {
        var key = (port, protocol);
        if (_managed.ContainsKey(key))
            return null;

        try
        {
            _device ??= await DiscoverAsync();
        }
        catch
        {
            return "#0";
        }

        foreach (var m in await _device.GetAllMappingsAsync())
        {
            if (m.Protocol != protocol || m.PublicPort != port) continue;
            if (m.Description == description)
            {
                _managed[key] = m;
                return null;
            }
            return $"#1";
        }

        var mapping = new Mapping(protocol, port, port, 0, description);
        try
        {
            await _device.CreatePortMapAsync(mapping);
        }
        catch (MappingException ex) when (ex.ErrorCode == 501)
        {
            return "#2";
        }

        _managed[key] = mapping;

        return null;
    }

    private async Task RemovePortForwardingSingleAsync(int port, Protocol protocol)
    {
        var key = (port, protocol);
        if (!_managed.TryGetValue(key, out var mapping)) return;
        await _device!.DeletePortMapAsync(mapping);
        _managed.Remove(key);
    }

    private static async Task<NatDevice> DiscoverAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            return await new NatDiscoverer().DiscoverDeviceAsync(PortMapper.Upnp, cts);
        }
        catch (NatDeviceNotFoundException)
        {
            throw new InvalidOperationException();
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException();
        }
    }
}