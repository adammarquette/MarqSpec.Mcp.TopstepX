using System.Runtime.InteropServices;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Replaces a file by writing a temp sibling, syncing it, renaming, then syncing the directory.
/// A throw before the rename leaves no final file (gh#755).
/// </summary>
public static class TapeExportDurableFile
{
    /// <summary>Linux <c>O_DIRECTORY</c>. A numeric constant, so the source has no unicode escape.</summary>
    private const int DirectoryOpenFlag = 65536;

    /// <summary>
    /// Writes <paramref name="finalPath"/> atomically.
    /// </summary>
    /// <param name="finalPath">The published path.</param>
    /// <param name="write">Writes the temp stream. A throw deletes the temp and leaves <paramref name="finalPath"/> untouched.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes after the directory sync.</returns>
    public static async Task CommitAsync(
        string finalPath,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(write);

        string? directory = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Tape export has no directory for " + finalPath);
        }

        Directory.CreateDirectory(directory);
        string temp = finalPath + ".partial";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        try
        {
            await using (FileStream stream = new(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                FileOptions.Asynchronous))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, finalPath, overwrite: true);
            FsyncDirectory(directory);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }
    }

    private static void FsyncDirectory(string directory)
    {
        int fd = OpenDirectory(directory, DirectoryOpenFlag);
        if (fd < 0)
        {
            throw new IOException("Tape export could not open the export directory to sync it.");
        }

        try
        {
            if (SyncFile(fd) != 0)
            {
                throw new IOException("Tape export could not sync the export directory.");
            }
        }
        finally
        {
            _ = CloseDirectory(fd);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string pathname, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncFile(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseDirectory(int fd);
}
