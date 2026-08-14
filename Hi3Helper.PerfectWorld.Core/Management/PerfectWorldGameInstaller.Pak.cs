using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.PerfectWorld.Core.Management.Api;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management;

public partial class PerfectWorldGameInstaller
{
    /// <summary>
    ///     Returns <c>true</c> when every file packed inside <paramref name="pak"/> is already present on disk with
    ///     the expected size (and, when <paramref name="verifyHash"/> is set, the expected MD5). A pak is treated as
    ///     atomic: if any packed file is missing or wrong, the whole pak must be re-downloaded because its entries
    ///     are not individually addressable on the CDN.
    /// </summary>
    private static async Task<bool> IsPakCompleteAsync(PerfectWorldPakEntry pak, string installPath, bool verifyHash,
        CancellationToken token)
    {
        foreach (PerfectWorldPakFile file in pak.Files)
        {
            token.ThrowIfCancellationRequested();

            string destPath = Path.Combine(installPath, file.Filename.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destPath)) return false;
            if (new FileInfo(destPath).Length != file.Size) return false;
            if (verifyHash && !await CheckMd5Async(destPath, file.Md5, token).ConfigureAwait(false)) return false;
        }

        return true;
    }

    /// <summary>
    ///     Downloads and extracts a batch of paks. Each pak blob is downloaded whole (content-addressed) into a
    ///     staging directory, verified, sliced into its packed files, then deleted. <paramref name="onBytes"/> is
    ///     invoked with download deltas; <paramref name="onFilesDone"/> is invoked once per pak with the number of
    ///     files it contributed.
    /// </summary>
    private async Task DownloadPaksAsync(IReadOnlyCollection<PerfectWorldPakEntry> paks, string installPath,
        Action<long> onBytes, Action<int> onFilesDone, CancellationToken token)
    {
        if (paks.Count == 0) return;

        string stagingDir = Path.Combine(installPath, PakStagingDirName);
        try
        {
            await Parallel.ForEachAsync(paks,
                new ParallelOptions { MaxDegreeOfParallelism = PakDownloadParallelism, CancellationToken = token },
                async (pak, ct) =>
                {
                    await DownloadAndExtractPakAsync(pak, installPath, stagingDir, ct, onBytes).ConfigureAwait(false);
                    onFilesDone(pak.Files.Count);
                }).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>
    ///     Downloads a single pak blob (content-addressed, resumable), verifies its MD5 and extracts every packed
    ///     file. The staged pak blob is always removed afterwards.
    /// </summary>
    private async Task DownloadAndExtractPakAsync(PerfectWorldPakEntry pak, string installPath, string stagingDir,
        CancellationToken token, Action<long> onBytes)
    {
        Directory.CreateDirectory(stagingDir);
        string pakTempPath = Path.Combine(stagingDir, pak.Md5 + ".pak");

        try
        {
            await DownloadContentFileAsync(pak.Md5, pak.FileSize, pakTempPath, token, onBytes).ConfigureAwait(false);

            if (!await CheckMd5Async(pakTempPath, pak.Md5, token).ConfigureAwait(false))
                throw new IOException($"Pak blob MD5 mismatch for {pak.Md5}.");

            await ExtractPakEntriesAsync(pak, pakTempPath, installPath, token).ConfigureAwait(false);
        }
        finally
        {
            ForceDeleteFile(pakTempPath);
        }
    }

    /// <summary>
    ///     Slices every packed file out of an already-downloaded pak blob (reading entries in ascending offset order
    ///     so the blob is read mostly sequentially), MD5-verifies each one and atomically moves it into place.
    ///     Packed files that are already present and correct are skipped.
    /// </summary>
    private static async Task ExtractPakEntriesAsync(PerfectWorldPakEntry pak, string pakPath, string installPath,
        CancellationToken token)
    {
        await using var pakStream = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        long pakLength = pakStream.Length;

        foreach (PerfectWorldPakFile file in pak.Files.OrderBy(f => f.Offset))
        {
            token.ThrowIfCancellationRequested();

            if (file.Offset < 0 || file.Size < 0 || file.Offset + file.Size > pakLength)
                throw new IOException(
                    $"Pak entry '{file.Filename}' is out of bounds (offset={file.Offset}, size={file.Size}, pak={pakLength}).");

            string destPath = Path.Combine(installPath, file.Filename.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(destPath) && new FileInfo(destPath).Length == file.Size &&
                await CheckMd5Async(destPath, file.Md5, token).ConfigureAwait(false))
                continue; // already extracted and correct

            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tempPath = destPath + ".pak.tmp";
            try
            {
                pakStream.Seek(file.Offset, SeekOrigin.Begin);

                await using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                                 FileShare.None, BufferSize, FileOptions.Asynchronous))
                {
                    await CopyExactAsync(pakStream, outStream, file.Size, token).ConfigureAwait(false);
                }

                if (!await CheckMd5Async(tempPath, file.Md5, token).ConfigureAwait(false))
                    throw new IOException($"Extracted MD5 mismatch for {file.Filename}.");

                ForceDeleteFile(destPath);
                File.Move(tempPath, destPath);
            }
            finally
            {
                ForceDeleteFile(tempPath);
            }
        }
    }

    /// <summary>
    ///     Copies exactly <paramref name="count"/> bytes from <paramref name="source"/> to <paramref name="dest"/>.
    /// </summary>
    private static async Task CopyExactAsync(Stream source, Stream dest, long count, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = count;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of pak blob while extracting.");

                await dest.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[PerfectWorldInstaller] Could not remove pak staging dir '{Path}': {Msg}", path, ex.Message);
        }
    }
}
