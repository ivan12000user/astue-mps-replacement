using System;
using System.IO;

namespace AstueMpsReplacement
{
    public sealed class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; }
        public string Text { get; set; }
        public Guid? BusId { get; set; }
        public Guid? DeviceId { get; set; }

        public string Formatted =>
            $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level,-5} {Text}";
    }

    public sealed class ReadErrorEvent
    {
        public DateTime TimestampUtc { get; set; }
        public Guid? BusId { get; set; }
        public Guid? DeviceId { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
    }

    public sealed class LogService
    {
        private sealed class ContextState
        {
            public Guid? BusId;
            public Guid? DeviceId;
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _dispose;
            private bool _done;
            public Scope(Action dispose) { _dispose = dispose; }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _dispose?.Invoke();
            }
        }

        [ThreadStatic]
        private static ContextState _context;

        public event Action<LogEntry> Entry;
        public event Action<ReadErrorEvent> ReadError;

        public IDisposable BeginContext(ConfigNode bus, ConfigNode device)
        {
            var previous = _context;
            _context = new ContextState
            {
                BusId = bus?.Id,
                DeviceId = device?.Id
            };
            return new Scope(() => _context = previous);
        }

        public void Info(string text) => Write("INFO", text);
        public void Warn(string text) => Write("WARN", text);
        public void Error(string text) => Write("ERROR", text);

        public void Tx(string channel, byte[] data) =>
            Write("TX", channel + "  " + BitConverter.ToString(data ?? new byte[0]).Replace("-", " "));

        public void Rx(string channel, byte[] data) =>
            Write("RX", channel + "  " + BitConverter.ToString(data ?? new byte[0]).Replace("-", " "));

        public void ReportReadError(Exception ex)
        {
            if (ex == null) return;
            var c = _context;
            ReadError?.Invoke(new ReadErrorEvent
            {
                TimestampUtc = DateTime.UtcNow,
                BusId = c?.BusId,
                DeviceId = c?.DeviceId,
                Category = ClassifyReadError(ex),
                Message = ex.Message ?? ex.GetType().Name
            });
        }

        private static string ClassifyReadError(Exception ex)
        {
            if (ex is TimeoutException) return "Timeout";
            if (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException &&
                ((ex.Message ?? string.Empty).IndexOf("COM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (ex.Message ?? string.Empty).IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0))
                return "Transport";

            var text = ex.Message ?? string.Empty;
            if (text.IndexOf("CRC", StringComparison.OrdinalIgnoreCase) >= 0) return "CRC";
            if (text.IndexOf("wrong address", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("неверный адрес", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Address";
            if (text.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("ответ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("response", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Protocol";
            return "Other";
        }

        private void Write(string level, string text)
        {
            var c = _context;
            Entry?.Invoke(new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Text = text ?? string.Empty,
                BusId = c?.BusId,
                DeviceId = c?.DeviceId
            });
        }
    }
}
