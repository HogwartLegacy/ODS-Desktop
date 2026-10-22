using System.Security.Cryptography;
using Ods.Core.Downloads;

namespace Ods.Core.Tests.Downloads;

public sealed class DownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ods-download-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _body = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
    private readonly HttpClient _http = Downloader.CreateHttpClient();

    public DownloaderTests() => Directory.CreateDirectory(_directory);

    private string Destination => Path.Combine(_directory, "model.gguf");

    private string Sha256 => Convert.ToHexStringLower(SHA256.HashData(_body));

    private Downloader NewDownloader(TimeSpan? stall = null) => new(_http)
    {
        AllowInsecureLoopback = true,
        StallTimeout = stall ?? TimeSpan.FromSeconds(10),
    };

    private DownloadRequest Request(TestHttpServer server, string? sha256 = null, long? size = null) =>
        new(server.Url, Destination, sha256 ?? Sha256, size ?? _body.Length, "test model");

    [Fact]
    public async Task Downloads_verifies_and_publishes()
    {
        await using var server = new TestHttpServer(_body);

        await NewDownloader().DownloadAsync(Request(server), null, TestContext.Current.CancellationToken);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Destination + ".part"));
    }

    [Fact]
    public async Task Resumes_a_partial_file_with_a_range_request()
    {
        await using var server = new TestHttpServer(_body);
        await File.WriteAllBytesAsync(Destination + ".part", _body[..1_000_000], TestContext.Current.CancellationToken);

        await NewDownloader().DownloadAsync(Request(server), null, TestContext.Current.CancellationToken);

        Assert.Equal("bytes=1000000-", server.RangeHeaders.Single());
        Assert.Equal(_body, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restarts_cleanly_when_the_server_ignores_range()
    {
        await using var server = new TestHttpServer(_body) { HonorRange = false };
        await File.WriteAllBytesAsync(Destination + ".part", _body[..500_000], TestContext.Current.CancellationToken);

        await NewDownloader().DownloadAsync(Request(server), null, TestContext.Current.CancellationToken);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Discards_a_file_that_does_not_match_its_pin()
    {
        await using var server = new TestHttpServer(_body);

        var error = await Assert.ThrowsAsync<DownloadException>(() =>
            NewDownloader().DownloadAsync(Request(server, sha256: new string('0', 64)), null, TestContext.Current.CancellationToken));

        Assert.Contains("does not match its pinned SHA-256", error.Message);
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(Destination + ".part"));
    }

    [Fact]
    public async Task Refuses_a_size_that_differs_from_the_pin()
    {
        await using var server = new TestHttpServer(_body);

        var error = await Assert.ThrowsAsync<DownloadException>(() =>
            NewDownloader().DownloadAsync(Request(server, size: _body.Length + 1), null, TestContext.Current.CancellationToken));

        Assert.Contains("ODS pins", error.Message);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task Resumes_automatically_after_a_stall_without_rehashing()
    {
        await using var server = new TestHttpServer(_body) { StallAfterBytes = 1_200_000, StallRequests = 2 };
        var phases = new List<DownloadPhase>();
        var progress = new SynchronousProgress<DownloadProgress>(p => phases.Add(p.Phase));

        await NewDownloader(stall: TimeSpan.FromMilliseconds(700)).DownloadAsync(Request(server), progress, TestContext.Current.CancellationToken);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
        Assert.Equal(3, server.RangeHeaders.Count);
        Assert.Null(server.RangeHeaders[0]);
        Assert.StartsWith("bytes=", server.RangeHeaders[1]);
        Assert.Contains(DownloadPhase.Resuming, phases);
        // The hash state carried over, so the partial file was never read back.
        Assert.DoesNotContain(DownloadPhase.CheckingPartial, phases);
    }

    [Fact]
    public async Task Gives_up_after_consecutive_resumes_without_progress()
    {
        await using var server = new TestHttpServer(_body) { StallAfterBytes = 0, StallRequests = int.MaxValue };
        var downloader = new Downloader(_http) { AllowInsecureLoopback = true, StallTimeout = TimeSpan.FromMilliseconds(300), ResumeAttempts = 2 };

        var error = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(Request(server), null, TestContext.Current.CancellationToken));

        Assert.True(error.Resumable);
        Assert.Equal(2, server.RangeHeaders.Count);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        await using var server = new TestHttpServer(_body) { StatusOverride = 404 };

        var error = await Assert.ThrowsAsync<DownloadException>(() =>
            NewDownloader().DownloadAsync(Request(server), null, TestContext.Current.CancellationToken));

        Assert.False(error.Resumable);
        Assert.Single(server.RangeHeaders);
    }

    [Fact]
    public async Task Refuses_plain_http_outside_loopback_tests()
    {
        var downloader = new Downloader(_http);
        var request = new DownloadRequest(new Uri("http://example.com/model.gguf"), Destination, null, null, "test model");

        var error = await Assert.ThrowsAsync<DownloadException>(() => downloader.DownloadAsync(request, null, TestContext.Current.CancellationToken));

        Assert.Contains("only over HTTPS", error.Message);
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need reports in order.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
