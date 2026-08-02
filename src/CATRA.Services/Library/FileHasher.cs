using System.Security.Cryptography;

namespace CATRA.Services.Library;

/// <summary>
/// Change-detection hash for large video files: SHA-256 over
/// <c>file size + first 1 MB + last 1 MB</c> (partial hash — fast for multi-GB
/// episodes). Files of 2 MB or less are hashed whole. Output is lowercase hex.
/// </summary>
public static class FileHasher
{
    private const int ChunkSize = 1024 * 1024; // 1 MB

    /// <summary>Computes the partial SHA-256 hash of a file.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static string ComputeHash(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = File.OpenRead(filePath);
        var length = stream.Length;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Mix the size in so same-content head/tail collisions across sizes are impossible.
        sha.AppendData(BitConverter.GetBytes(length));

        if (length <= ChunkSize * 2L)
        {
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
        }
        else
        {
            var head = new byte[ChunkSize];
            stream.ReadExactly(head);
            sha.AppendData(head);

            stream.Seek(-ChunkSize, SeekOrigin.End);
            var tail = new byte[ChunkSize];
            stream.ReadExactly(tail);
            sha.AppendData(tail);
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
