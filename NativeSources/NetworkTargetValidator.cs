using System.Net;
using System.Net.Sockets;

namespace NebulaBridge.NativeSources;

public interface INetworkTargetValidator
{
    Task ValidateAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class NetworkTargetValidator : INetworkTargetValidator
{
    public async Task ValidateAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Only credential-free HTTP(S) targets are allowed.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("The indexer host could not be resolved.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(IsBlocked))
        {
            throw new InvalidOperationException(
                "Indexer targets may not resolve to private, loopback, link-local, or reserved addresses."
            );
        }
    }

    private static bool IsBlocked(IPAddress address)
    {
        if (
            IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal
        )
        {
            return true;
        }

        var bytes = address.MapToIPv6().IsIPv4MappedToIPv6
            ? address.MapToIPv4().GetAddressBytes()
            : address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => true,
                100 when bytes[1] is >= 64 and <= 127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                >= 224 => true,
                _ => false,
            };
        }

        return (bytes[0] & 0xFE) == 0xFC || bytes[0] == 0xFF;
    }
}
