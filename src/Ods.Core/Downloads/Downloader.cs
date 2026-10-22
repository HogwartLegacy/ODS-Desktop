using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;

namespace Ods.Core.Downloads;

public sealed record DownloadRequest(Uri Url, string Destination, string? Sha256, long? ExpectedSize, string Label);

public enum DownloadPhase
{
    CheckingPartial,
    Downloading,
    Resuming,
    Verifying,
}

public sealed record DownloadProgress(string Label, DownloadPhase Phase, long BytesDone, long? TotalBytes, double BytesPerSecond, int Attempt = 1)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesDone / TotalBytes.Value, 0, 1) : null;

    public TimeSpan? Remaining => TotalBytes is long total && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((total - BytesDone) / BytesPerSecond)
        : null;
}

/// <summary>
/// A download failed in a way the user can act on; the message says how.
/// <see cref="Resumable"/> marks a stall or dropped connection whose partial
/// file is intact, so continuing from it is safe.
/// </summary>
public sealed class DownloadException(string message, Exception? inner = null, bool resumable = false) : Exception(message, inner)
{
    public bool Resumable { get; } = resumable;
}

/// <summary>
/// Verified, resumable HTTPS downloads. Bytes land in "&lt;destination&gt;.part"
/// and are hashed while they stream; a resume re-hashes the partial file and
/// continues with an HTTP Range request. The file is published only when its
/// size and SHA-256 match the pin, otherwise it is discarded.
/// A stalled or dropped connection resumes automatically while each attempt
/// makes progress; <see cref="ResumeAttempts"/> consecutive attempts that gain
/// nothing end the download with an error. Every resume is reported and
/// logged. A wrong hash, a 4xx response or a full disk fails at once.
/// </summary>
public sealed class Downloader(HttpClient http)
{
    private const int MaxRedirects = 10;
    private const long FreeSpaceMarginBytes = 512L * 1024 * 1024;
    private const long ProgressThresholdBytes = 1024 * 1024;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a connection may deliver nothing before ODS reconnects.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Consecutive resumes without progress before giving up (1 disables resuming).</summary>
    public int ResumeAttempts { get; init; } = 4;

    /// <summary>Hosts a download may start from; null allows any HTTPS host.</summary>
    public IReadOnlySet<string>? AllowedInitialHosts { get; init; }

    /// <summary>Test hook: permit http:// on loopback.</summary>
    public bool AllowInsecureLoopback { get; init; }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(Downloader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ODS-Desktop/{version.Split('+')[0]}");
        return client;
    }

    public async Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var partial = new PartialHash();
        var withoutProgress = 0;
        var resumes = 0;
        while (true)
        {
            var before = PartialLength(request);
            try
            {
                await DownloadOnceAsync(request, partial, progress, resumes + 1, cancellationToken);
                return;
            }
            catch (DownloadException error) when (error.Resumable)
            {
                var after = PartialLength(request);
                withoutProgress = after - before >= ProgressThresholdBytes ? 0 : withoutProgress + 1;
                if (withoutProgress >= ResumeAttempts) throw;
                resumes++;
                Diagnostics.AppLog.Warn($"{error.Message} Resuming automatically from {after:N0} bytes (resume {resumes}; {withoutProgress} in a row without progress).");
                progress?.Report(new DownloadProgress(request.Label, DownloadPhase.Resuming, after, request.ExpectedSize, 0, resumes + 1));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 + 3 * withoutProgress, 15)), cancellationToken);
            }
        }
    }

    private static long PartialLength(DownloadRequest request)
    {
        var part = request.Destination + ".part";
        return File.Exists(part) ? new FileInfo(part).Length : 0;
    }

    /// <summary>
    /// The running SHA-256 of a .part file and how many of its bytes it covers,
    /// kept across resumes so a stall does not mean re-reading gigabytes.
    /// </summary>
    private sealed class PartialHash : IDisposable
    {
        public IncrementalHash Hash { get; private set; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Bytes { get; set; }

        public void Reset()
        {
            Hash.Dispose();
            Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Bytes = 0;
        }

        public void Dispose() => Hash.Dispose();
    }

    private async Task DownloadOnceAsync(
        DownloadRequest request, PartialHash partial, IProgress<DownloadProgress>? progress, int attempt, CancellationToken cancellationToken)
    {
        EnsureAllowed(request.Url, initial: true);
        var directory = Path.GetDirectoryName(Path.GetFullPath(request.Destination))
            ?? throw new ArgumentException("The download destination needs a directory.", nameof(request));
        Directory.CreateDirectory(directory);
        var partPath = request.Destination + ".part";

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (request.ExpectedSize is long expectedSize && existing > expectedSize)
        {
            File.Delete(partPath);
            existing = 0;
        }
        if (existing != partial.Bytes)
        {
            // A partial file from an earlier session (or one this attempt cannot vouch for): hash it again.
            partial.Reset();
            if (existing > 0) await HashFileAsync(partPath, partial.Hash, request, existing, DownloadPhase.CheckingPartial, progress, cancellationToken);
            partial.Bytes = existing;
        }
        var hash = partial.Hash;

        using var response = await SendAsync(request, existing, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            if (request.ExpectedSize == existing || response.Content.Headers.ContentRange?.Length == existing)
            {
                Publish(request, partPath, existing, hash, progress);
                return;
            }
            File.Delete(partPath);
            throw new DownloadException($"The server would not resume {request.Label}, so the partial file was discarded. Try again to download it from the start.");
        }
        if (!response.IsSuccessStatusCode)
        {
            // A 5xx from a CDN is usually transient; a 4xx (missing, forbidden) is not.
            throw new DownloadException(
                $"Downloading {request.Label} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {response.RequestMessage?.RequestUri?.Host}.",
                resumable: (int)response.StatusCode >= 500);
        }

        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && !append)
        {
            // The server ignored the Range header and is sending the whole file.
            partial.Reset();
            hash = partial.Hash;
            existing = 0;
        }
        if (append && response.Content.Headers.ContentRange?.From != existing)
        {
            throw new DownloadException($"The server resumed {request.Label} at the wrong offset. Try again.");
        }

        long? total = response.Content.Headers.ContentLength is long length ? existing + length : request.ExpectedSize;
        if (request.ExpectedSize is long pinned && total is long announced && announced != pinned)
        {
            throw new DownloadException($"{request.Label} is {announced:N0} bytes on the server but ODS pins {pinned:N0} bytes; it was not downloaded.");
        }
        EnsureFreeSpace(request.Destination, (total ?? existing) - existing, request.Label);

        var done = await StreamAsync(response, partPath, append, partial, request, existing, total, attempt, progress, cancellationToken);
        Publish(request, partPath, done, partial.Hash, progress);
    }

    private async Task<long> StreamAsync(
        HttpResponseMessage response, string partPath, bool append, PartialHash partial, DownloadRequest request,
        long existing, long? total, int attempt, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var hash = partial.Hash;
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        var done = existing;
        var meter = new SpeedMeter(existing);
        var lastReport = Stopwatch.StartNew();
        try
        {
            await using var file = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(), stall.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DownloadException(
                        $"The download of {request.Label} stalled for {StallTimeout.TotalSeconds:0} seconds. Resume to continue where it stopped.",
                        resumable: true);
                }
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                // Hash after the write: hashed bytes never exceed the bytes on disk.
                hash.AppendData(buffer, 0, read);
                partial.Bytes += read;
                done += read;
                meter.Add(done);
                if (lastReport.Elapsed >= ReportInterval)
                {
                    progress?.Report(new DownloadProgress(request.Label, DownloadPhase.Downloading, done, total, meter.BytesPerSecond, attempt));
                    lastReport.Restart();
                }
            }
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            throw new DownloadException(
                $"The connection dropped while downloading {request.Label} ({error.Message}). Resume to continue where it stopped.",
                error,
                resumable: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        progress?.Report(new DownloadProgress(request.Label, DownloadPhase.Downloading, done, total, meter.BytesPerSecond, attempt));
        return done;
    }

    private static void Publish(DownloadRequest request, string partPath, long size, IncrementalHash hash, IProgress<DownloadProgress>? progress)
    {
        progress?.Report(new DownloadProgress(request.Label, DownloadPhase.Verifying, size, size, 0));
        if (request.ExpectedSize is long expected && size != expected)
        {
            File.Delete(partPath);
            throw new DownloadException($"{request.Label} downloaded as {size:N0} bytes but ODS pins {expected:N0} bytes; it was discarded. Try again.");
        }
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (request.Sha256 is { Length: > 0 } pinned && !string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new DownloadException($"{request.Label} does not match its pinned SHA-256 (got {actual}, expected {pinned}); it was discarded.");
        }
        File.Move(partPath, request.Destination, overwrite: true);
    }

    private static async Task HashFileAsync(
        string path, IncrementalHash hash, DownloadRequest request, long length, DownloadPhase phase,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long done = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                done += read;
                if (lastReport.Elapsed >= ReportInterval)
                {
                    progress?.Report(new DownloadProgress(request.Label, phase, done, length, 0));
                    lastReport.Restart();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(DownloadRequest request, long rangeStart, CancellationToken cancellationToken)
    {
        var current = request.Url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureAllowed(current, initial: hop == 0);
            var message = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeStart > 0) message.Headers.Range = new RangeHeaderValue(rangeStart, null);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException error)
            {
                throw new DownloadException($"Could not reach {current.Host} to download {request.Label}: {error.Message}", error, resumable: true);
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is Uri location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new DownloadException($"Downloading {request.Label} was redirected more than {MaxRedirects} times.");
    }

    private void EnsureAllowed(Uri url, bool initial)
    {
        var loopbackHttp = AllowInsecureLoopback && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
        if (url.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
        {
            throw new DownloadException($"Refusing to download from {url.GetLeftPart(UriPartial.Authority)}: ODS downloads only over HTTPS, including redirects.");
        }
        if (initial && AllowedInitialHosts is not null && !loopbackHttp && !AllowedInitialHosts.Contains(url.Host))
        {
            throw new DownloadException($"Refusing to download from an unexpected location: {url}");
        }
    }

    private static void EnsureFreeSpace(string destination, long remaining, string label)
    {
        if (remaining <= 0) return;
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < remaining + FreeSpaceMarginBytes)
        {
            throw new DownloadException(string.Create(CultureInfo.InvariantCulture,
                $"Not enough free space on {root} for {label}: it needs {Gib(remaining + FreeSpaceMarginBytes)} GB and {Gib(free)} GB is free. Free up space or choose another models folder in Settings."));
        }
    }

    internal static string Gib(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Smoothed transfer rate over a sliding two-second window.</summary>
    private sealed class SpeedMeter(long start)
    {
        private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new([(TimeSpan.Zero, start)]);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public double BytesPerSecond { get; private set; }

        public void Add(long bytes)
        {
            var now = _clock.Elapsed;
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().Time > TimeSpan.FromSeconds(2)) _samples.Dequeue();
            var (time, firstBytes) = _samples.Peek();
            var span = (now - time).TotalSeconds;
            if (span > 0.25) BytesPerSecond = (bytes - firstBytes) / span;
        }
    }
}
