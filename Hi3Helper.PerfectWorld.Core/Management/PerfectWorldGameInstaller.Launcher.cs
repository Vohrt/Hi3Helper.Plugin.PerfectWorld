using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.PerfectWorld.Core.Management.Api;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.PerfectWorld.Core.Management;

public partial class PerfectWorldGameInstaller
{
    /// <summary>Install-relative directory that holds the vendor launcher (self-update target of AllFiles.xml).</summary>
    private const string LauncherRootDirName = "NTELauncher";

    /// <summary>
    ///     The download+install plan for the vendor launcher: which files still need fetching plus the byte/count
    ///     totals (accounted by the downloaded <c>.zip</c> size, since launcher files are individually zip-compressed
    ///     on the CDN).
    /// </summary>
    internal sealed record LauncherPlan(
        PerfectWorldLauncherManifest Manifest,
        List<PerfectWorldLauncherFile> ToDownload,
        long TotalZipBytes,
        int TotalCount,
        long ExistingZipBytes,
        int ExistingCount)
    {
        public long ToDownloadZipBytes => ToDownload.Sum(f => f.ZipSize);
    }

    /// <summary>
    ///     Fetches and parses the vendor launcher self-update manifest (<c>Version.ini</c> → <c>AllFiles.xml</c>),
    ///     trying every configured launcher CDN root.
    /// </summary>
    private async Task<PerfectWorldLauncherManifest> GetLauncherManifestAsync(CancellationToken token)
    {
        PerfectWorldGameConfig config = Manager.Config;

        byte[] versionIniBytes = await DownloadBytesWithFallbackAsync(
            config.LauncherCdnUrls, config.BuildLauncherVersionIniUrl, token).ConfigureAwait(false);
        string versionIni = Encoding.UTF8.GetString(versionIniBytes);

        (string? fileListUrl, string? version, string? build) =
            PerfectWorldLauncherManifestParser.ParseVersionIni(versionIni);
        if (string.IsNullOrEmpty(fileListUrl))
            throw new IOException("Launcher Version.ini did not contain a FileListURL.");

        string? versionDir = PerfectWorldLauncherManifestParser.ExtractVersionDir(fileListUrl);
        if (string.IsNullOrEmpty(versionDir))
            throw new IOException($"Could not derive launcher version directory from '{fileListUrl}'.");

        byte[] allFilesBytes = await DownloadBytesWithFallbackAsync(
            config.LauncherCdnUrls, cdn => config.BuildLauncherAllFilesUrl(cdn, versionDir), token)
            .ConfigureAwait(false);
        string allFilesXml = Encoding.UTF8.GetString(allFilesBytes);

        PerfectWorldLauncherManifest manifest = PerfectWorldLauncherManifestParser.ParseAllFiles(allFilesXml, versionDir);
        if (manifest.Files.Count == 0)
            throw new IOException("Launcher AllFiles.xml contained no file entries.");

        SharedStatic.InstanceLogger.LogInformation(
            "[PerfectWorldInstaller] Launcher manifest {Version} (build {Build}, {Ignored}): {Count} files.",
            manifest.ProductVersion, build ?? "?", version ?? "?", manifest.Files.Count);

        return manifest;
    }

    /// <summary>
    ///     Classifies every launcher file as already-present (correct size, and correct MD5 when
    ///     <paramref name="verifyHash"/> is set) or needing download, returning the resulting <see cref="LauncherPlan"/>.
    /// </summary>
    private async Task<LauncherPlan> PrepareLauncherPlanAsync(string installPath, bool verifyHash,
        CancellationToken token)
    {
        PerfectWorldLauncherManifest manifest = await GetLauncherManifestAsync(token).ConfigureAwait(false);
        string launcherRoot = Path.Combine(installPath, LauncherRootDirName);

        var toDownload = new List<PerfectWorldLauncherFile>();
        long totalZip = 0, existingZip = 0;
        int existingCount = 0;

        foreach (PerfectWorldLauncherFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            totalZip += file.ZipSize;

            string destPath = Path.Combine(launcherRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));

            bool ok;
            if (!File.Exists(destPath))
                ok = false;
            else if (new FileInfo(destPath).Length != file.Size)
                ok = false;
            else if (verifyHash && file.Size != 0) // zero-size entries have an unreliable manifest Checksum
                ok = await CheckMd5Async(destPath, file.Md5, token).ConfigureAwait(false);
            else
                ok = true; // trust a size match on a fresh install (or for verified-empty files)

            if (ok)
            {
                existingZip += file.ZipSize;
                existingCount++;
            }
            else
            {
                toDownload.Add(file);
            }
        }

        return new LauncherPlan(manifest, toDownload, totalZip, manifest.Files.Count, existingZip, existingCount);
    }

    /// <summary>
    ///     Downloads and installs every launcher file in <paramref name="plan"/> that is still missing.
    ///     <paramref name="onBytes"/> is invoked with download deltas (zip bytes); <paramref name="onFileDone"/> is
    ///     invoked once per completed file.
    /// </summary>
    private async Task DownloadLauncherPlanAsync(LauncherPlan plan, string installPath,
        Action<long> onBytes, Action<int> onFileDone, CancellationToken token)
    {
        if (plan.ToDownload.Count == 0) return;

        string launcherRoot = Path.Combine(installPath, LauncherRootDirName);
        Directory.CreateDirectory(launcherRoot);

        await Parallel.ForEachAsync(plan.ToDownload,
            new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = token },
            async (file, ct) =>
            {
                await DownloadAndInflateLauncherFileAsync(plan.Manifest, file, launcherRoot, ct, onBytes)
                    .ConfigureAwait(false);
                onFileDone(1);
            }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Downloads a single launcher file's <c>.zip</c> blob (resumable, CDN fallback), verifies its zip MD5,
    ///     inflates it, verifies the inflated MD5 and atomically moves it into <c>NTELauncher\{path}</c>.
    /// </summary>
    private async Task DownloadAndInflateLauncherFileAsync(PerfectWorldLauncherManifest manifest, PerfectWorldLauncherFile file,
        string launcherRoot, CancellationToken token, Action<long> onBytes)
    {
        string destPath = Path.Combine(launcherRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string zipTemp = destPath + ".zip.tmp";
        string inflateTemp = destPath + ".tmp";

        try
        {
            await DownloadLauncherZipAsync(manifest.VersionDir, file, zipTemp, token, onBytes).ConfigureAwait(false);

            if (!await CheckMd5Async(zipTemp, file.ZipMd5, token).ConfigureAwait(false))
                throw new IOException($"Launcher zip MD5 mismatch for {file.Path}.");

            InflateSingleEntryZip(zipTemp, inflateTemp);

            // Verify the inflated payload. Zero-size entries carry an unreliable placeholder Checksum in the manifest
            // (a manifest-generator quirk), so validate them by emptiness instead — the blob itself was already
            // integrity-checked against ZipChecksum above.
            if (file.Size == 0)
            {
                if (new FileInfo(inflateTemp).Length != 0)
                    throw new IOException($"Launcher file expected to be empty but was not: {file.Path}.");
            }
            else if (!await CheckMd5Async(inflateTemp, file.Md5, token).ConfigureAwait(false))
            {
                throw new IOException($"Launcher file MD5 mismatch for {file.Path}.");
            }

            ForceDeleteFile(destPath);
            File.Move(inflateTemp, destPath);
        }
        finally
        {
            ForceDeleteFile(zipTemp);
            ForceDeleteFile(inflateTemp);
        }
    }

    /// <summary>
    ///     Downloads a launcher file's zip blob, trying every configured launcher CDN root and resuming per CDN.
    /// </summary>
    private async Task DownloadLauncherZipAsync(string versionDir, PerfectWorldLauncherFile file, string tempPath,
        CancellationToken token, Action<long> onBytes)
    {
        PerfectWorldGameConfig config = Manager.Config;
        Exception? lastError = null;

        for (int cdnIndex = 0; cdnIndex < config.LauncherCdnUrls.Length; cdnIndex++)
        {
            string url = config.BuildLauncherFileZipUrl(config.LauncherCdnUrls[cdnIndex], versionDir, file.Path);
            try
            {
                await DownloadFileAsync(url, tempPath, file.ZipSize, token, onBytes).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    "[PerfectWorldInstaller] Launcher CDN #{Index} failed for {Path}: {Msg}", cdnIndex, file.Path, ex.Message);
            }
        }

        throw new IOException($"All CDNs failed for launcher file {file.Path}.", lastError);
    }

    /// <summary>Inflates the single entry of a launcher <c>.zip</c> blob to <paramref name="destPath"/>.</summary>
    private static void InflateSingleEntryZip(string zipPath, string destPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);

        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
        if (entry == null)
            throw new InvalidDataException($"Launcher zip '{zipPath}' contains no file entry.");

        ForceDeleteFile(destPath);
        entry.ExtractToFile(destPath, overwrite: true);
    }
}
