using System.Security.Cryptography;
using ClipShare.Windows.Application;

namespace ClipShare.Windows.Infrastructure;

/// <summary>生成 256-bit、无 padding Base64URL 幂等键。</summary>
public sealed class SecureIdempotencyKeyFactory : IIdempotencyKeyFactory
{
    public string Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
