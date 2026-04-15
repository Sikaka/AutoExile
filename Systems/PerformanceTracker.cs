using System.Diagnostics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Per-tick performance and failure instrumentation for the wave farm loop.
    ///
    /// Wrap major tick sections in <c>using var _ = perf.Section("name")</c> to record
    /// their duration. Sample storage is a fixed-size ring buffer per section
    /// (~128 samples) — stats computed on demand via <see cref="GetStats"/>.
    ///
    /// Failure counters accumulate until <see cref="Reset"/> (called on mode/map entry).
    /// Categories are free-form strings: "loot", "interact", "explore".
    /// </summary>
    public class PerformanceTracker
    {
        private const int BufferSize = 128;

        private readonly Dictionary<string, RingBuffer> _sections = new();
        private readonly Dictionary<string, Dictionary<string, int>> _failures = new();

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        public Section SectionScope(string name) => new Section(this, name);

        /// <summary>Disposable struct — use in a <c>using</c> to time a code block.</summary>
        public readonly struct Section : IDisposable
        {
            private readonly PerformanceTracker _tracker;
            private readonly string _name;
            private readonly long _startTicks;

            internal Section(PerformanceTracker tracker, string name)
            {
                _tracker = tracker;
                _name = name;
                _startTicks = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                var elapsed = (Stopwatch.GetTimestamp() - _startTicks) * TicksToMs;
                _tracker.RecordSample(_name, elapsed);
            }
        }

        private void RecordSample(string name, double ms)
        {
            if (!_sections.TryGetValue(name, out var buf))
            {
                buf = new RingBuffer(BufferSize);
                _sections[name] = buf;
            }
            buf.Add(ms);
        }

        /// <summary>Increment a failure counter. Category groups related reasons.</summary>
        public void RecordFailure(string category, string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "(unknown)";
            if (!_failures.TryGetValue(category, out var dict))
            {
                dict = new Dictionary<string, int>();
                _failures[category] = dict;
            }
            dict[reason] = dict.TryGetValue(reason, out var n) ? n + 1 : 1;
        }

        public SectionStats GetStats(string name)
        {
            if (!_sections.TryGetValue(name, out var buf) || buf.Count == 0)
                return default;
            return buf.ComputeStats();
        }

        /// <summary>Sections sorted by avg ms descending. Used by overlay.</summary>
        public IEnumerable<(string Name, SectionStats Stats)> TopSections(int count)
        {
            var list = new List<(string, SectionStats)>(_sections.Count);
            foreach (var kv in _sections)
                list.Add((kv.Key, kv.Value.ComputeStats()));
            list.Sort((a, b) => b.Item2.AvgMs.CompareTo(a.Item2.AvgMs));
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }

        /// <summary>Failure reasons for a category, sorted by count descending.</summary>
        public IEnumerable<(string Reason, int Count)> TopFailures(string category, int count)
        {
            if (!_failures.TryGetValue(category, out var dict) || dict.Count == 0)
                yield break;
            var list = new List<KeyValuePair<string, int>>(dict);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            int n = Math.Min(count, list.Count);
            for (int i = 0; i < n; i++)
                yield return (list[i].Key, list[i].Value);
        }

        public int FailureTotal(string category)
        {
            if (!_failures.TryGetValue(category, out var dict)) return 0;
            int total = 0;
            foreach (var v in dict.Values) total += v;
            return total;
        }

        /// <summary>Clear all samples and failure counters. Call on map/mode entry.</summary>
        public void Reset()
        {
            _sections.Clear();
            _failures.Clear();
        }

        private sealed class RingBuffer
        {
            private readonly double[] _samples;
            private int _next;
            public int Count { get; private set; }

            public RingBuffer(int size) { _samples = new double[size]; }

            public void Add(double v)
            {
                _samples[_next] = v;
                _next = (_next + 1) % _samples.Length;
                if (Count < _samples.Length) Count++;
            }

            public SectionStats ComputeStats()
            {
                if (Count == 0) return default;
                double sum = 0, max = 0;
                var sorted = new double[Count];
                for (int i = 0; i < Count; i++)
                {
                    var v = _samples[i];
                    sum += v;
                    if (v > max) max = v;
                    sorted[i] = v;
                }
                Array.Sort(sorted);
                double p95 = sorted[(int)Math.Min(Count - 1, Count * 0.95)];
                return new SectionStats
                {
                    Count = Count,
                    AvgMs = sum / Count,
                    MaxMs = max,
                    P95Ms = p95,
                };
            }
        }
    }

    public struct SectionStats
    {
        public int Count;
        public double AvgMs;
        public double MaxMs;
        public double P95Ms;
    }
}
