using System.Globalization;
using Ods.Core;
using Ods.Core.Downloads;

namespace Ods.Cli;

/// <summary>Progress output that redraws one line on a terminal and logs milestones when redirected (CI).</summary>
internal sealed class ConsoleProgress : IProgress<SetupStep>, IProgress<DownloadProgress>
{
    private readonly Lock _gate = new();
    private string _lastTitle = "";
    private int _lastBucket = -1;
    private bool _lineOpen;

    public void Report(SetupStep value)
    {
        if (value.Download is not null)
        {
            Report(value.Download);
            return;
        }
        lock (_gate)
        {
            if (value.Title == _lastTitle) return;
            _lastTitle = value.Title;
            CloseLine();
            Console.WriteLine($"  {value.Title}{(value.Detail is null ? "" : $": {value.Detail}")}");
        }
    }

    public void Report(DownloadProgress value)
    {
        lock (_gate)
        {
            var percent = value.Fraction is double f ? f * 100 : 0;
            var phase = value.Phase switch
            {
                DownloadPhase.CheckingPartial => "Checking partial download",
                DownloadPhase.Verifying => "Verifying",
                DownloadPhase.Resuming => $"Connection stalled; resuming (attempt {value.Attempt})",
                _ => "Downloading",
            };
            var text = string.Create(CultureInfo.InvariantCulture,
                $"  {phase} {value.Label}  {percent,5:0.0}%  {Mb(value.BytesDone)}/{(value.TotalBytes is long t ? Mb(t) : "?")} MB  {value.BytesPerSecond / 1048576,6:0.0} MB/s{Eta(value.Remaining)}");
            if (Console.IsOutputRedirected)
            {
                var bucket = (int)(percent / 10);
                if (bucket == _lastBucket && value.Phase == DownloadPhase.Downloading) return;
                _lastBucket = bucket;
                Console.WriteLine(text);
                return;
            }
            Console.Write("\r" + text.PadRight(Math.Max(Console.WindowWidth - 1, text.Length)));
            _lineOpen = true;
        }
    }

    public void CloseLine()
    {
        lock (_gate)
        {
            if (!_lineOpen) return;
            Console.WriteLine();
            _lineOpen = false;
        }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);

    private static string Eta(TimeSpan? remaining) =>
        remaining is TimeSpan r && r < TimeSpan.FromDays(1) ? $"  ETA {r:hh\\:mm\\:ss}" : "";
}
