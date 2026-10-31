using System.Security.Cryptography;
using System.Text;

namespace NovaLink.Lumen.Core;

public static class OfflineUuid
{
    public static string ForName(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..];
    }
}
