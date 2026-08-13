using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DownloadManager.Services;

public static class ChecksumService
{
    public static readonly string[] Algorithms = { "SHA-256", "SHA-512", "SHA-1", "MD5" };

    public static async Task<string> ComputeAsync(string algorithm, string filePath,
                                                  CancellationToken ct = default)
    {
        using HashAlgorithm hash = algorithm.ToUpperInvariant() switch
        {
            "MD5" => MD5.Create(),
            "SHA-1" => SHA1.Create(),
            "SHA-256" => SHA256.Create(),
            "SHA-512" => SHA512.Create(),
            _ => throw new NotSupportedException($"Unsupported algorithm: {algorithm}")
        };

        await using var stream = File.OpenRead(filePath);
        var bytes = await hash.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}