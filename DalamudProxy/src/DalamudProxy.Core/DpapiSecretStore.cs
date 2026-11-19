using System.Security.Cryptography;
using System.Text;

namespace DalamudProxy.Core;

public static class DpapiSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DalamudProxy.Secret.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅支持 Windows。");

        var plaintext = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅支持 Windows。");

        try
        {
            var protectedBytes = Convert.FromBase64String(value);
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("无法解密已保存的代理密码。请在设置中重新输入密码。", ex);
        }
    }
}
