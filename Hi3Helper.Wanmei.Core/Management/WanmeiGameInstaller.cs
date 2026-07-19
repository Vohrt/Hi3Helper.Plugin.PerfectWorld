using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Wanmei.Core.Management.Api;
using Hi3Helper.Wanmei.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Wanmei.Core.Management;

/// <summary>
///     <see cref="IGameInstaller"/> implementation for Perfect World (Wanmei) pw_sdk titles. Downloads the
///     content-addressed game resources described by the decrypted <c>ResList</c> manifest.
/// </summary>
[GeneratedComClass]
public partial class WanmeiGameInstaller : GameInstallerBase
{
    private const int DownloadParallelism = 4;
    private const int VerifyParallelism = 8;
    private const int BufferSize = 1 << 20; // 1 MiB

    private readonly HttpClient _downloadHttpClient;

    public WanmeiGameInstaller(IGameManager? gameManager) : base(gameManager)
    {
        _downloadHttpClient = new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowRedirections()
            .AllowUntrustedCert()
            .AllowCookies()
            .Create();
    }

    private WanmeiGameManager Manager =>
        GameManager as WanmeiGameManager ??
        throw new InvalidOperationException("GameManager is not a WanmeiGameManager.");

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        return await Manager.InitAsyncInner(true, token).ConfigureAwait(false);
    }

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        WanmeiRemoteConfig? remote = await Manager.GetRemoteConfigAsync(false, token).ConfigureAwait(false);
        return remote?.ResSize ?? 0L;
    }

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind,
        CancellationToken token)
    {
        string installPath = EnsureAndGetGamePath();
        var manifest = await GetManifestAsync(token).ConfigureAwait(false);

        long downloaded = 0;
        foreach (WanmeiResEntry entry in manifest)
        {
            string localPath = Path.Combine(installPath, entry.Filename.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.FileSize)
                downloaded += entry.FileSize;
        }

        return downloaded;
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        return ReconcileToManifestAsync(progressDelegate, progressStateDelegate, verifyHash: false, token);
    }

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // Prefer HDiffPatch block-level deltas (download a small binary patch and apply it locally); any file that
        // has no usable patch — or whose patch fails — transparently falls back to a full content-addressed download.
        return DeltaUpdateAsync(progressDelegate, progressStateDelegate, token);
    }

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
    {
        // No dedicated preload channel for pw_sdk titles.
        progressStateDelegate?.Invoke(InstallProgressState.Completed);
        return Task.CompletedTask;
    }

    protected override Task UninstallAsyncInner(CancellationToken token)
    {
        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath)) return Task.CompletedTask;

        try
        {
            string clientDir = Path.Combine(installPath, "Client");
            if (Directory.Exists(clientDir)) Directory.Delete(clientDir, true);

            string stateFile = Path.Combine(installPath, WanmeiGameManager.StateFileName);
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogError("[WanmeiInstaller] Uninstall failed: {Msg}", ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Downloads a content-addressed file, trying every configured CDN root and resuming/retrying per CDN.
    /// </summary>
    private async Task DownloadContentFileAsync(string md5, long expectedSize, string tempPath,
        CancellationToken token, Action<long> onProgress)
    {
        WanmeiGameConfig config = Manager.Config;
        Exception? lastError = null;

        for (int cdnIndex = 0; cdnIndex < config.GameResCdnUrls.Length; cdnIndex++)
        {
            string url = config.BuildContentUrl(config.GameResCdnUrls[cdnIndex], md5, expectedSize);
            try
            {
                await DownloadFileAsync(url, tempPath, expectedSize, token, onProgress).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    "[WanmeiInstaller] CDN #{Index} failed for {Md5}: {Msg}", cdnIndex, md5, ex.Message);
            }
        }

        throw new IOException($"All CDNs failed for content {md5}.", lastError);
    }

    /// <summary>
    ///     Downloads a single URL to <paramref name="tempPath"/> with HTTP range resume and retry.
    /// </summary>
    private async Task DownloadFileAsync(string url, string tempPath, long expectedSize, CancellationToken token,
        Action<long> onProgress)
    {
        const int maxRetries = 3;
        long totalReported = 0;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                long existingLength = 0;
                if (File.Exists(tempPath))
                {
                    existingLength = new FileInfo(tempPath).Length;
                    if (existingLength > expectedSize)
                    {
                        ForceDeleteFile(tempPath);
                        existingLength = 0;
                    }
                }

                long diff = existingLength - totalReported;
                if (diff != 0)
                {
                    onProgress(diff);
                    totalReported += diff;
                }

                if (existingLength == expectedSize) return;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingLength > 0)
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);

                using HttpResponseMessage response = await _downloadHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    ForceDeleteFile(tempPath);
                    if (totalReported > 0)
                    {
                        onProgress(-totalReported);
                        totalReported = 0;
                    }
                    existingLength = 0;
                }

                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var fs = new FileStream(tempPath,
                    existingLength > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, BufferSize, true);

                var buffer = new byte[BufferSize];
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    onProgress(read);
                    totalReported += read;
                }

                await fs.FlushAsync(token).ConfigureAwait(false);

                if (fs.Length != expectedSize)
                    throw new IOException($"Size mismatch. Expected {expectedSize}, got {fs.Length}.");

                return;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<bool> CheckMd5Async(string filePath, string expectedMd5, CancellationToken token)
    {
        if (!File.Exists(filePath)) return false;

        using var md5 = MD5.Create();
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash).Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string> ComputeMd5Async(string filePath, CancellationToken token)
    {
        using var md5 = MD5.Create();
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    internal static void ForceDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try { File.SetAttributes(filePath, FileAttributes.Normal); }
        catch { /* ignored */ }

        File.Delete(filePath);
    }

    public override void Dispose()
    {
        _downloadHttpClient.Dispose();
        _patchApplyLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
