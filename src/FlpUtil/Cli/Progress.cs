using System.Diagnostics;
using System.Globalization;

namespace FlpUtil.Cli;

/// <summary>A unit of long-running work whose progress can be reported.</summary>
public interface IProgressScope : IDisposable
{
    /// <summary>Advances the counter.</summary>
    void Tick(long increment = 1);

    /// <summary>Sets the counter to an absolute value.</summary>
    void Report(long done);

    /// <summary>Replaces the trailing detail text, e.g. the field currently being read.</summary>
    void Detail(string? detail);
}

public interface IProgressSink
{
    /// <summary>Starts a phase. Pass a negative total when the amount of work is not known ahead.</summary>
    IProgressScope Begin(string phase, long total = -1);
}

/// <summary>Discards progress — used for <c>--quiet</c> and in tests.</summary>
public sealed class NullProgress : IProgressSink
{
    public static readonly NullProgress Instance = new();

    private sealed class Scope : IProgressScope
    {
        public void Tick(long increment = 1) { }

        public void Report(long done) { }

        public void Detail(string? detail) { }

        public void Dispose() { }
    }

    public IProgressScope Begin(string phase, long total = -1) => new Scope();
}

/// <summary>
/// Writes progress to stderr, so redirecting stdout to a file still yields clean output.
///
/// On a console it rewrites a single line with a rate and an ETA; when stderr is redirected it
/// emits occasional plain milestone lines instead, since carriage returns in a log file are useless.
/// Updates are rate-limited so that reporting never becomes the bottleneck on a large index.
/// </summary>
public sealed class ConsoleProgress : IProgressSink
{
    private static readonly TimeSpan ConsoleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

    /// <summary>Log milestone for a phase with no known total, where a percentage is impossible.</summary>
    private const long CountlessMilestone = 1_000_000;

    private readonly bool _interactive = !Console.IsErrorRedirected;

    public IProgressScope Begin(string phase, long total = -1) => new Scope(this, phase, total);

    private sealed class Scope(ConsoleProgress owner, string phase, long total) : IProgressScope
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _interval = owner._interactive ? ConsoleInterval : LogInterval;
        private TimeSpan _lastPaint = TimeSpan.FromSeconds(-1);
        private int _lastWidth;
        private int _lastDecile = -1;
        private long _done;
        private string? _detail;

        public void Tick(long increment = 1)
        {
            _done += increment;
            Paint(force: false);
        }

        public void Report(long done)
        {
            _done = done;
            Paint(force: false);
        }

        public void Detail(string? detail)
        {
            _detail = detail;
            Paint(force: false);
        }

        private void Paint(bool force)
        {
            TimeSpan elapsed = _clock.Elapsed;

            // In a log, also emit on every 10% so a run that finishes inside one interval still
            // shows movement; on a console the time-based rate limit alone is what keeps it smooth.
            bool milestone = false;
            if (!owner._interactive)
            {
                // With a total, every 10%; without one, every whole million processed.
                int step = total > 0
                    ? (int)(10L * Math.Min(_done, total) / total)
                    : (int)(_done / CountlessMilestone);
                if (step > _lastDecile)
                {
                    _lastDecile = step;
                    milestone = step > 0;
                }
            }

            if (!force && !milestone && elapsed - _lastPaint < _interval)
                return;
            _lastPaint = elapsed;

            string text = Describe(elapsed);
            if (owner._interactive)
            {
                // Pad to erase whatever the previous, possibly longer, line left behind.
                Console.Error.Write('\r');
                Console.Error.Write(text.PadRight(_lastWidth));
                _lastWidth = text.Length;
            }
            else
            {
                Console.Error.WriteLine(text);
            }
        }

        private string Describe(TimeSpan elapsed)
        {
            var text = new System.Text.StringBuilder($"  {phase}: ");

            if (total > 0)
            {
                double fraction = Math.Min(1.0, (double)_done / total);
                text.Append(CultureInfo.InvariantCulture, $"{_done:N0}/{total:N0} ({fraction * 100:F1}%)");

                if (_done > 0 && fraction is > 0.001 and < 1.0)
                {
                    var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction - elapsed.TotalSeconds);
                    text.Append(CultureInfo.InvariantCulture, $" eta {Clock(remaining)}");
                }
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture, $"{_done:N0}");
            }

            if (elapsed.TotalSeconds >= 1)
                text.Append(CultureInfo.InvariantCulture, $" [{_done / elapsed.TotalSeconds:N0}/s]");

            if (!string.IsNullOrEmpty(_detail))
                text.Append(CultureInfo.InvariantCulture, $" {_detail}");

            return text.ToString();
        }

        private static string Clock(TimeSpan span) => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
            : $"{span.Minutes:D2}:{span.Seconds:D2}";

        /// <summary>Clears the line so the caller's own output starts clean.</summary>
        public void Dispose()
        {
            _clock.Stop();
            if (owner._interactive)
            {
                if (_lastWidth > 0)
                    Console.Error.Write('\r' + new string(' ', _lastWidth) + '\r');
            }
            else if (_done > 0)
            {
                Console.Error.WriteLine($"  {phase}: done, {_done:N0} in {Clock(_clock.Elapsed)}");
            }
        }
    }
}
