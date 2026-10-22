using System.Security.Cryptography;

namespace BatchAgentRunner.Infrastructure.CredentialVault;

/// <summary>
/// 凭证静态存储保护器（DESIGN-账号凭证切换-v1.0 §8.1）：
/// Vault 内凭证文件必须加密落盘，明文只存在于内存与 Live 位置。
/// </summary>
public interface ICredentialProtector
{
    /// <summary>加密凭证明文。</summary>
    /// <param name="plaintext">待加密的明文字节。</param>
    /// <returns>密文字节（含平台保护器所需的头部信息）。</returns>
    byte[] Protect(byte[] plaintext);

    /// <summary>解密凭证密文。</summary>
    /// <param name="ciphertext">由 <see cref="Protect"/> 产出的密文字节。</param>
    /// <returns>解密后的明文字节；密文被篡改时抛出平台异常。</returns>
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Windows DPAPI（CryptProtectData，CurrentUser 作用域，按当前用户绑定）。
/// 非 Windows 平台不支持——凭证功能仅面向 Windows（DESIGN §2 侦查环境）。
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    /// <summary>用当前用户的平台密钥加密凭证明文。</summary>
    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("凭证静态加密依赖 Windows DPAPI，当前平台不支持");
        }

        return System.Security.Cryptography.ProtectedData.Protect(
            plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    /// <summary>解密凭据密文；密文被篡改时抛平台异常。</summary>
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
