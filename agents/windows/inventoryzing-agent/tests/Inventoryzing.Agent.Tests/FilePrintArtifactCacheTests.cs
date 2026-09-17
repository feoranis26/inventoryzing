using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;

namespace Inventoryzing.Agent.Tests;

public sealed class FilePrintArtifactCacheTests : IDisposable
{
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-artifact-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task Store_is_content_addressed_idempotent_and_durable()
    {
        var artifact = Artifact([1, 2, 3, 4]);
        var cache = new FilePrintArtifactCache(directoryPath);

        var stored = await cache.StoreAsync(artifact, CancellationToken.None);
        var replayed = await cache.StoreAsync(artifact, CancellationToken.None);
        var reopened = new FilePrintArtifactCache(directoryPath);
        var content = await reopened.ReadAsync(artifact.Sha256, CancellationToken.None);

        Assert.Equal(new CachedPrintArtifact(artifact.Sha256, 4), stored);
        Assert.Equal(stored, replayed);
        Assert.Equal([1, 2, 3, 4], content);
        Assert.Single(Directory.GetFiles(directoryPath, "*.artifact", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Corrupt_cached_bytes_are_rejected_on_reuse_and_read()
    {
        var artifact = Artifact([1, 2, 3, 4]);
        var cache = new FilePrintArtifactCache(directoryPath);
        await cache.StoreAsync(artifact, CancellationToken.None);
        var path = Assert.Single(
            Directory.GetFiles(directoryPath, "*.artifact", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(path, [4, 3, 2, 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.StoreAsync(artifact, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.ReadAsync(artifact.Sha256, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Missing_and_noncanonical_hashes_are_rejected()
    {
        var cache = new FilePrintArtifactCache(directoryPath);
        var missing = new string('0', 64);

        await Assert.ThrowsAsync<PrintArtifactNotFoundException>(() =>
            cache.ReadAsync(missing, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.ReadAsync("../artifact", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.ReadAsync(new string('A', 64), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Oversized_artifacts_are_rejected_before_staging()
    {
        var artifact = Artifact([1, 2, 3, 4, 5]);
        var cache = new FilePrintArtifactCache(directoryPath, maximumArtifactBytes: 4);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.StoreAsync(artifact, CancellationToken.None).AsTask());

        Assert.False(Directory.Exists(directoryPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static PrintArtifact Artifact(byte[] content) =>
        new("image/png", 62, 29, ArtifactColorSpace.Srgb, content);
}
