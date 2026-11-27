using System.Diagnostics;
using System.Globalization;

namespace NimboMount.Drive;

public readonly record struct UploadSample(string Id, string Name, long Sent, long Total, bool Active);

public readonly record struct UploadView(string Name, long Sent, long Total, double BytesPerSecond);

public sealed class UploadMeter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Func<long> _now;

    public UploadMeter(Func<long>? now = null)
    {
        _now = now ?? Stopwatch.GetTimestamp;
    }

    public void Apply(UploadSample sample)
    {
        lock (_gate)
        {
            if (!sample.Active)
            {
                _slots.Remove(sample.Id);
                return;
            }

            if (!_slots.TryGetValue(sample.Id, out var slot))
            {
                slot = new Slot();
                _slots[sample.Id] = slot;
            }

            slot.Name = sample.Name;
            slot.Note(sample.Sent, sample.Total, _now());
        }
    }

    public UploadView[] Snapshot()
    {
        lock (_gate)
        {
            return _slots.Values
                .Select(slot => slot.View())
                .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private sealed class Slot
    {
        private readonly Queue<(long Time, long Sent)> _marks = new();

        public string Name { get; set; } = "";

        public long Sent { get; private set; }

        public long Total { get; private set; }

        public void Note(long sent, long total, long now)
        {
            Sent = sent;
            Total = total;
            _marks.Enqueue((now, sent));
            var cutoff = now - (Stopwatch.Frequency * 2);
            while (_marks.Count > 1 && _marks.Peek().Time < cutoff)
            {
                _marks.Dequeue();
            }
        }

        public UploadView View()
        {
            double speed = double.NaN;
            if (_marks.Count >= 2)
            {
                var first = _marks.Peek();
                var last = _marks.Last();
                var seconds = (last.Time - first.Time) / (double)Stopwatch.Frequency;
                var bytes = last.Sent - first.Sent;
                if (seconds >= 0.4 && bytes >= 0)
                {
                    speed = bytes / seconds;
                }
            }

            return new UploadView(Name, Sent, Total, speed);
        }
    }
}

public static class UploadCaption
{
    public static string Detail(long sent, long total, double bytesPerSecond)
    {
        var percent = total <= 0
            ? 0
            : (int)Math.Clamp(Math.Round(sent * 100d / total), 0, 100);
        var rate = Rate(bytesPerSecond);
        return rate.Length == 0 ? percent.ToString(CultureInfo.InvariantCulture) + "%" : percent.ToString(CultureInfo.InvariantCulture) + "% · " + rate;
    }

    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 1)
        {
            return "";
        }

        if (bytesPerSecond >= 1024d * 1024 * 1024)
        {
            return (bytesPerSecond / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB/s";
        }

        if (bytesPerSecond >= 1024d * 1024)
        {
            return (bytesPerSecond / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
        }

        if (bytesPerSecond >= 1024d)
        {
            var kilobytes = bytesPerSecond / 1024d;
            var format = kilobytes < 10 ? "0.0" : "0";
            return kilobytes.ToString(format, CultureInfo.InvariantCulture) + " KB/s";
        }

        return bytesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " B/s";
    }

    public static string Title(IReadOnlyList<UploadView> views)
    {
        if (views.Count == 0)
        {
            return "NimboMount";
        }

        if (views.Count == 1)
        {
            var view = views[0];
            return "NimboMount — " + OneLine(view.Name) + "  " + Detail(view.Sent, view.Total, view.BytesPerSecond);
        }

        return "NimboMount — " + views.Count.ToString(CultureInfo.InvariantCulture) + " files  " + Overall(views);
    }

    public static string Tray(IReadOnlyList<UploadView> views)
    {
        if (views.Count == 0)
        {
            return "NimboMount";
        }

        var line = views.Count == 1
            ? OneLine(views[0].Name) + "  " + Detail(views[0].Sent, views[0].Total, views[0].BytesPerSecond)
            : views.Count.ToString(CultureInfo.InvariantCulture) + " files  " + Overall(views);
        var text = "NimboMount\n" + line;
        return text.Length <= 127 ? text : text[..127];
    }

    private static string Overall(IReadOnlyList<UploadView> views)
    {
        long sent = 0;
        long total = 0;
        double speed = 0;
        var anySpeed = false;
        foreach (var view in views)
        {
            sent += view.Sent;
            total += view.Total;
            if (!double.IsNaN(view.BytesPerSecond) && view.BytesPerSecond > 0)
            {
                speed += view.BytesPerSecond;
                anySpeed = true;
            }
        }

        return Detail(sent, total, anySpeed ? speed : double.NaN);
    }

    private static string OneLine(string name) => name.Replace("\r", " ").Replace("\n", " ");
}
