using System.Security.Cryptography;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// 凭证静态存储保护器（DESIGN-账号凭证切换-v1.0 §8.1）：
/// Vault 内凭证文件必须加密落盘，明文只存在于内存与 Live 位置。
/// </summary>
public interface ICredentialProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Windows DPAPI（CryptProtectData，CurrentUser 作用域，按当前用户绑定）。
/// 非 Windows 平台不支持——凭证功能仅面向 Windows（DESIGN §2 侦查环境）。
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("凭证静态加密依赖 Windows DPAPI，当前平台不支持");
        }

        return System.Security.Cryptography.ProtectedData.Protect(
            plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("凭证静态解密依赖 Windows DPAPI，当前平台不支持");
        }

        return System.Security.Cryptography.ProtectedData.Unprotect(
            ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
