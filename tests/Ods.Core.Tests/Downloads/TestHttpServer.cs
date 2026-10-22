using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ods.Core.Tests.Downloads;

/// <summary>
/// A minimal HTTP/1.1 file server on loopback whose behaviour each test
/// scripts: honour or ignore Range, stall part-way through a body, or answer
/// with an error. Real sockets, so the downloader's own HTTP stack is tested.
/// </summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public TestHttpServer(byte[] body)
    {
        Body = body;
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public byte[] Body { get; }

    public bool HonorRange { get; set; } = true;

    /// <summary>Stall (send nothing more) after this many body bytes, on the first N requests.</summary>
    public long? StallAfterBytes { get; set; }

    public int StallRequests { get; set; } = 1;

    public int? StatusOverride { get; set; }

    public List<string?> RangeHeaders { get; } = [];

    public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/model.gguf");

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var request = await ReadHeadersAsync(stream);
            var range = request.FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
            int requestNumber;
            lock (RangeHeaders)
            {
                RangeHeaders.Add(range);
                requestNumber = RangeHeaders.Count;
            }

            if (StatusOverride is int status)
            {
                await WriteAsync(stream, $"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            long start = 0;
            if (HonorRange && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                start = long.Parse(range[6..].TrimEnd('-'));
            }
            var length = Body.Length - start;
            var head = start > 0
                ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{Body.Length - 1}/{Body.Length}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"
                : $"HTTP/1.1 200 OK\r\nContent-Length: {Body.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n";
            await WriteAsync(stream, head);

            var stallAt = StallAfterBytes is long stall && requestNumber <= StallRequests ? stall : long.MaxValue;
            var sent = 0L;
            for (var offset = start; offset < Body.Length; offset += 8192)
            {
                var count = (int)Math.Min(8192, Body.Length - offset);
                if (sent + count > stallAt)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { });
                    return;
                }
                await stream.WriteAsync(Body.AsMemory((int)offset, count));
                sent += count;
            }
        }
    }

    private static async Task<List<string>> ReadHeadersAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!(buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0) break;
            buffer.Add(one[0]);
        }
        return Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
    }
}
