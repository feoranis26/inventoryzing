using System.Security.Cryptography;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record CachedPrintArtifact(string Sha256, int Length);

public interface IPrintArtifactCache
{
    ValueTask<CachedPrintArtifact> StoreAsync(
        PrintArtifact artifact,
        CancellationToken cancellationToken);

    ValueTask<byte[]> ReadAsync(string sha256, CancellationToken cancellationToken);
}

public sealed class FilePrintArtifactCache : IPrintArtifactCache
{
    public const int DefaultMaximumArtifactBytes = 16 * 1024 * 1024;

    private readonly string rootPath;
    private readonly int maximumArtifactBytes;

    public FilePrintArtifactCache(
        string rootPath,
        int maximumArtifactBytes = DefaultMaximumArtifactBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("Artifact cache path must be fully qualified.", nameof(rootPath));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumArtifactBytes);

        this.rootPath = Path.GetFullPath(rootPath);
        this.maximumArtifactBytes = maximumArtifactBytes;
    }

    public async ValueTask<CachedPrintArtifact> StoreAsync(
        PrintArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLength(artifact.Length);
        var content = artifact.CopyContent();
        ValidateContent(artifact.Sha256, content);
        var finalPath = ArtifactPath(artifact.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (File.Exists(finalPath))
        {
            return await ValidateExistingAsync(
                finalPath,
                artifact.Sha256,
                artifact.Length,
                cancellationToken).ConfigureAwait(false);
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{artifact.Sha256}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65_536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                return await ValidateExistingAsync(
                    finalPath,
                    artifact.Sha256,
                    artifact.Length,
                    cancellationToken).ConfigureAwait(false);
            }

            return new CachedPrintArtifact(artifact.Sha256, artifact.Length);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async ValueTask<byte[]> ReadAsync(
        string sha256,
        CancellationToken cancellationToken)
    {
        ValidateHash(sha256);
        var path = ArtifactPath(sha256);
        byte[] content;
        try
        {
            ValidateLength(new FileInfo(path).Length);
            content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw new PrintArtifactNotFoundException(
                $"Print artifact '{sha256}' is not present in the cache.",
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new PrintArtifactNotFoundException(
                $"Print artifact '{sha256}' is not present in the cache.",
                exception);
        }

        ValidateContent(sha256, content);
        return content;
    }

    private static async ValueTask<CachedPrintArtifact> ValidateExistingAsync(
        string path,
        string sha256,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (content.Length != expectedLength)
        {
            throw Corrupt(sha256);
        }

        ValidateContent(sha256, content);
        return new CachedPrintArtifact(sha256, content.Length);
    }

    private string ArtifactPath(string sha256)
    {
        ValidateHash(sha256);
        return Path.Combine(rootPath, sha256[..2], $"{sha256}.artifact");
    }

    private static void ValidateHash(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Artifact SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(sha256));
        }
    }

    private static void ValidateContent(string expectedSha256, ReadOnlySpan<byte> content)
    {
        var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(expectedSha256, actual, StringComparison.Ordinal))
        {
            throw Corrupt(expectedSha256);
        }
    }

    private static InvalidDataException Corrupt(string sha256) =>
        new($"Print artifact '{sha256}' failed SHA-256 validation.");

    private void ValidateLength(long length)
    {
        if (length > maximumArtifactBytes)
        {
            throw new InvalidDataException(
                $"Print artifact exceeds the configured {maximumArtifactBytes}-byte staging limit.");
        }
    }
}

public sealed class PrintArtifactNotFoundException(string message, Exception innerException)
    : IOException(message, innerException);
