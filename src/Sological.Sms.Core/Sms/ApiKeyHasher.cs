using System.Security.Cryptography;
using System.Text;

namespace Sological.Sms.Core.Sms;

/// <summary>Channel API keys are high-entropy random strings, stored as plain SHA-256
/// (lowercase hex) — no KDF needed for non-human secrets, and lookups stay O(1)-cheap.</summary>
public static class ApiKeyHasher
{
    public static string Sha256Hex(string apiKey)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
