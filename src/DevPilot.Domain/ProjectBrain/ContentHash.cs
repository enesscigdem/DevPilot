using System.Security.Cryptography;
using System.Text;

namespace DevPilot.Domain.ProjectBrain;

public static class ContentHash
{
    public static string Compute(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return ComputeHashBytes(Array.Empty<byte>());
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        return ComputeHashBytes(bytes);
    }

    private static string ComputeHashBytes(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
