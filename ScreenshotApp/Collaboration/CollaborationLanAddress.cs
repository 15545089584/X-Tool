using System.Net;

namespace ScreenshotApp.Collaboration;

internal static class CollaborationLanAddress
{
    internal static bool IsSameSubnet(IPAddress local, IPAddress peer, int prefixLength)
    {
        var address = local.GetAddressBytes();
        var remote = peer.GetAddressBytes();
        if (address.Length != 4 || remote.Length != 4 || prefixLength is < 1 or > 32) return false;
        for (var index = 0; index < 4; index++)
        {
            var bits = Math.Clamp(prefixLength - index * 8, 0, 8);
            var mask = (byte)(0xff << (8 - bits));
            if ((address[index] & mask) != (remote[index] & mask)) return false;
        }
        return true;
    }
}
