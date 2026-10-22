using System.Runtime.CompilerServices;
using System.Text;

namespace Ods.Core.Chat;

/// <summary>Reads server-sent events (data: lines, blank-line terminated) from a stream.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                if (data.Length > 0) yield return data.ToString();
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var value = line.AsSpan(5);
            if (value.Length > 0 && value[0] == ' ') value = value[1..];
            if (data.Length > 0) data.Append('\n');
            data.Append(value);
        }
    }
}
