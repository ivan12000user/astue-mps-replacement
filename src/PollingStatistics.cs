using System;
using System.Collections.Generic;
using System.Linq;

namespace AstueMpsReplacement
{
    internal sealed class DeviceErrorSnapshot
    {
        public int Total { get; set; }
        public int Timeout { get; set; }
        public int Crc { get; set; }
        public int Address { get; set; }
        public int Transport { get; set; }
        public int Protocol { get; set; }
        public int Other { get; set; }
        public int Startup { get; set; }
        public DateTime LastErrorLocal { get; set; }
        public string LastMessage { get; set; }
    }

    internal sealed class LineStatisticsSnapshot
    {
        public double Utilization5Min { get; set; }
        public double Utilization1Hour { get; set; }
        public double TimeoutOccupancy5Min { get; set; }
        public double TimeoutOccupancy1Hour { get; set; }
        public int Transactions1Hour { get; set; }
        public int NoResponse1Hour { get; set; }
        public int Errors1Hour { get; set; }
        public double AverageTransactionMs1Hour { get; set; }
        public double MaxTransactionMs1Hour { get; set; }
        public long TxBytes1Hour { get; set; }
        public long RxBytes1Hour { get; set; }
        public DateTime FirstSampleLocal { get; set; }
    }

    internal sealed class PollingStatistics
    {
        private sealed class ExchangeSample
        {
            public DateTime EndUtc;
            public double DurationMs;
            public bool NoResponse;
            public int TxBytes;
            public int RxBytes;
        }

        private sealed class ErrorSample
        {
            public DateTime TimestampUtc;
            public string Category;
            public string Message;
        }

        private sealed class LineState
        {
            public DateTime FirstSeenUtc;
            public bool HasTransportExchange;
            public readonly Queue<ExchangeSample> Exchanges = new Queue<ExchangeSample>();
            public readonly Queue<ErrorSample> Errors = new Queue<ErrorSample>();
            public readonly Queue<ErrorSample> StartupErrors = new Queue<ErrorSample>();
        }

        private sealed class DeviceState
        {
            public Guid BusId;
            public readonly Queue<ErrorSample> Errors = new Queue<ErrorSample>();
            public readonly Queue<ErrorSample> StartupErrors = new Queue<ErrorSample>();
        }

        private readonly object _sync = new object();
        private readonly Dictionary<Guid, LineState> _lines = new Dictionary<Guid, LineState>();
        private readonly Dictionary<Guid, DeviceState> _devices = new Dictionary<Guid, DeviceState>();

        public void RecordExchange(Guid busId, DateTime endUtc, TimeSpan duration, int txBytes, int rxBytes, bool noResponse, bool completed)
        {
            lock (_sync)
            {
                LineState state;
                if (!_lines.TryGetValue(busId, out state))
                {
                    state = new LineState { FirstSeenUtc = endUtc - duration };
                    _lines[busId] = state;
                }

                var wasOperational = state.HasTransportExchange;
                if (completed) state.HasTransportExchange = true;

                // Ошибки открытия порта до первой нормальной транзакции считаются
                // ошибками запуска/системы и не искажают рабочую загрузку линии.
                // После первой рабочей транзакции время транспортных сбоев учитывается.
                if (completed || wasOperational)
                {
                    state.Exchanges.Enqueue(new ExchangeSample
                    {
                        EndUtc = endUtc,
                        DurationMs = Math.Max(0.0, duration.TotalMilliseconds),
                        NoResponse = noResponse,
                        TxBytes = Math.Max(0, txBytes),
                        RxBytes = Math.Max(0, rxBytes)
                    });
                }
                Prune(state, endUtc);
            }
        }

        public void RecordReadError(ReadErrorEvent entry)
        {
            if (entry == null || !entry.BusId.HasValue) return;
            var sample = new ErrorSample
            {
                TimestampUtc = entry.TimestampUtc,
                Category = entry.Category ?? "Other",
                Message = entry.Message ?? string.Empty
            };

            lock (_sync)
            {
                LineState line;
                if (!_lines.TryGetValue(entry.BusId.Value, out line))
                {
                    line = new LineState { FirstSeenUtc = entry.TimestampUtc };
                    _lines[entry.BusId.Value] = line;
                }
                var startup = !line.HasTransportExchange &&
                              (sample.Category.Equals("Transport", StringComparison.OrdinalIgnoreCase) ||
                               sample.Category.Equals("Other", StringComparison.OrdinalIgnoreCase));
                if (startup) line.StartupErrors.Enqueue(sample);
                else line.Errors.Enqueue(sample);
                Prune(line, entry.TimestampUtc);

                if (entry.DeviceId.HasValue)
                {
                    DeviceState device;
                    if (!_devices.TryGetValue(entry.DeviceId.Value, out device))
                    {
                        device = new DeviceState { BusId = entry.BusId.Value };
                        _devices[entry.DeviceId.Value] = device;
                    }
                    device.BusId = entry.BusId.Value;
                    if (startup) device.StartupErrors.Enqueue(sample);
                    else device.Errors.Enqueue(sample);
                    Prune(device, entry.TimestampUtc);
                }
            }
        }

        public DeviceErrorSnapshot GetDeviceErrors(Guid deviceId)
        {
            lock (_sync)
            {
                DeviceState state;
                if (!_devices.TryGetValue(deviceId, out state)) return new DeviceErrorSnapshot();
                var now = DateTime.UtcNow;
                Prune(state, now);
                return BuildErrorSnapshot(state.Errors, state.StartupErrors);
            }
        }

        public DeviceErrorSnapshot GetLineErrors(Guid busId)
        {
            lock (_sync)
            {
                LineState state;
                if (!_lines.TryGetValue(busId, out state)) return new DeviceErrorSnapshot();
                var now = DateTime.UtcNow;
                Prune(state, now);
                return BuildErrorSnapshot(state.Errors, state.StartupErrors);
            }
        }

        public LineStatisticsSnapshot GetLine(Guid busId)
        {
            lock (_sync)
            {
                LineState state;
                if (!_lines.TryGetValue(busId, out state)) return new LineStatisticsSnapshot();

                var now = DateTime.UtcNow;
                Prune(state, now);
                var cutoff5 = now.AddMinutes(-5);
                var cutoff60 = now.AddHours(-1);
                var samples60 = state.Exchanges.Where(x => x.EndUtc >= cutoff60).ToArray();
                var samples5 = samples60.Where(x => x.EndUtc >= cutoff5).ToArray();

                return new LineStatisticsSnapshot
                {
                    Utilization5Min = UtilizationPercent(samples5, state.FirstSeenUtc, now, TimeSpan.FromMinutes(5), false),
                    Utilization1Hour = UtilizationPercent(samples60, state.FirstSeenUtc, now, TimeSpan.FromHours(1), false),
                    TimeoutOccupancy5Min = UtilizationPercent(samples5, state.FirstSeenUtc, now, TimeSpan.FromMinutes(5), true),
                    TimeoutOccupancy1Hour = UtilizationPercent(samples60, state.FirstSeenUtc, now, TimeSpan.FromHours(1), true),
                    Transactions1Hour = samples60.Length,
                    NoResponse1Hour = samples60.Count(x => x.NoResponse),
                    Errors1Hour = state.Errors.Count,
                    AverageTransactionMs1Hour = samples60.Length == 0 ? 0.0 : samples60.Average(x => x.DurationMs),
                    MaxTransactionMs1Hour = samples60.Length == 0 ? 0.0 : samples60.Max(x => x.DurationMs),
                    TxBytes1Hour = samples60.Sum(x => (long)x.TxBytes),
                    RxBytes1Hour = samples60.Sum(x => (long)x.RxBytes),
                    FirstSampleLocal = state.FirstSeenUtc == DateTime.MinValue ? DateTime.MinValue : state.FirstSeenUtc.ToLocalTime()
                };
            }
        }

        public void ResetLine(Guid busId, IEnumerable<Guid> deviceIds)
        {
            lock (_sync)
            {
                _lines.Remove(busId);
                if (deviceIds != null)
                {
                    foreach (var id in deviceIds)
                        _devices.Remove(id);
                }
            }
        }

        private static double UtilizationPercent(IEnumerable<ExchangeSample> source, DateTime firstSeenUtc, DateTime nowUtc, TimeSpan window, bool onlyNoResponse)
        {
            var elapsedMs = Math.Min(window.TotalMilliseconds, Math.Max(1000.0, (nowUtc - firstSeenUtc).TotalMilliseconds));
            if (elapsedMs <= 0.0) return 0.0;
            var busyMs = source.Where(x => !onlyNoResponse || x.NoResponse).Sum(x => x.DurationMs);
            return Math.Max(0.0, Math.Min(100.0, busyMs * 100.0 / elapsedMs));
        }

        private static DeviceErrorSnapshot BuildErrorSnapshot(IEnumerable<ErrorSample> errors, IEnumerable<ErrorSample> startupErrors)
        {
            var result = new DeviceErrorSnapshot();
            result.Startup = startupErrors == null ? 0 : startupErrors.Count();
            ErrorSample last = null;
            foreach (var e in errors)
            {
                result.Total++;
                last = e;
                switch ((e.Category ?? string.Empty).ToUpperInvariant())
                {
                    case "TIMEOUT": result.Timeout++; break;
                    case "CRC": result.Crc++; break;
                    case "ADDRESS": result.Address++; break;
                    case "TRANSPORT": result.Transport++; break;
                    case "PROTOCOL": result.Protocol++; break;
                    default: result.Other++; break;
                }
            }
            if (last != null)
            {
                result.LastErrorLocal = last.TimestampUtc.ToLocalTime();
                result.LastMessage = last.Message;
            }
            return result;
        }

        private static void Prune(LineState state, DateTime nowUtc)
        {
            var cutoff = nowUtc.AddHours(-1);
            while (state.Exchanges.Count > 0 && state.Exchanges.Peek().EndUtc < cutoff)
                state.Exchanges.Dequeue();
            while (state.Errors.Count > 0 && state.Errors.Peek().TimestampUtc < cutoff)
                state.Errors.Dequeue();
            while (state.StartupErrors.Count > 0 && state.StartupErrors.Peek().TimestampUtc < cutoff)
                state.StartupErrors.Dequeue();
        }

        private static void Prune(DeviceState state, DateTime nowUtc)
        {
            var cutoff = nowUtc.AddHours(-1);
            while (state.Errors.Count > 0 && state.Errors.Peek().TimestampUtc < cutoff)
                state.Errors.Dequeue();
            while (state.StartupErrors.Count > 0 && state.StartupErrors.Peek().TimestampUtc < cutoff)
                state.StartupErrors.Dequeue();
        }
    }
}
