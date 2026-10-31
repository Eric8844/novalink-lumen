using System.Security.Cryptography;
using System.Text;

namespace NovaLink.Lumen.Services.Storage;

/// <summary>
/// DPAPI backed secret storage. Values written by the Electron build use a
/// different protector and therefore fail closed: the caller must re-authenticate.
/// </summary>
public sealed class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NovaLink.Lumen.Accounts.v1");

    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            return "enc:" + Convert.ToBase64String(bytes);
        }
        catch
        {
            return "raw:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }
    }

    public string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            if (value.StartsWith("raw:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(value[4..]));
            if (value.StartsWith("enc:", StringComparison.Ordinal))
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[4..]), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            return "";
        }
        catch
        {
            return "";
        }
    }
}
