using System.Security.Cryptography;

namespace EyetrackerGlasses.Mnn;

/// <summary>Copies an Avalonia asset/Android asset stream to a real, versioned local file.</summary>
public static class MnnModelFile
{
    /// <summary>
    /// The caller owns source. destinationDirectory must be an app-private writable directory.
    /// Model bytes determine the filename, so an updated resource cannot reuse a stale cached model.
    /// </summary>
    public static async Task<string> CopyToLocalFileAsync(Stream source, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] hash;
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                target.Position = 0;
                hash = await SHA256.HashDataAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            string destination = Path.Combine(directory, Convert.ToHexStringLower(hash) + ".mnn");
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                // Verify an existing cache entry instead of silently trusting its filename.
                await using var cached = File.OpenRead(destination);
                byte[] actual = await SHA256.HashDataAsync(cached, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(hash, actual)) throw;
            }
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
