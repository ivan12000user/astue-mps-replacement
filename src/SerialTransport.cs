using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace AstueMpsReplacement
{
    public sealed class SerialTransport : IDisposable
    {
        private readonly object _sync = new object();
        private readonly SerialPort _port;
        private readonly LogService _log;
        private readonly Guid _busId;
        private readonly PollingStatistics _statistics;

        internal SerialTransport(string portName, int baudRate, int dataBits, Parity parity, StopBits stopBits,
            LogService log, Guid busId, PollingStatistics statistics)
        {
            _log = log;
            _busId = busId;
            _statistics = statistics;
            _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false
            };
        }

        public string PortName => _port.PortName;
        public bool IsOpen => _port.IsOpen;
        public void Open() { if (!_port.IsOpen) _port.Open(); }
        public void Close() { if (_port.IsOpen) _port.Close(); }

        public byte[] Exchange(byte[] request, int responseSilenceMs, int overallTimeoutMs)
        {
            lock (_sync)
            {
                var sw = Stopwatch.StartNew();
                var responseBytes = 0;
                var noResponse = false;
                var completed = false;
                try
                {
                    Open();
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                    _log?.Tx(_port.PortName, request);
                    _port.Write(request, 0, request.Length);

                    using (var ms = new MemoryStream())
                    {
                        var start = Environment.TickCount;
                        var lastByte = Environment.TickCount;
                        while (unchecked(Environment.TickCount - start) < overallTimeoutMs)
                        {
                            var available = _port.BytesToRead;
                            if (available > 0)
                            {
                                var buf = new byte[available];
                                var n = _port.Read(buf, 0, buf.Length);
                                ms.Write(buf, 0, n);
                                lastByte = Environment.TickCount;
                            }
                            else if (ms.Length > 0 && unchecked(Environment.TickCount - lastByte) >= responseSilenceMs)
                            {
                                break;
                            }
                            Thread.Sleep(2);
                        }
                        var response = ms.ToArray();
                        responseBytes = response.Length;
                        noResponse = responseBytes == 0;
                        _log?.Rx(_port.PortName, response);
                        completed = true;
                        return response;
                    }
                }
                finally
                {
                    sw.Stop();
                    _statistics?.RecordExchange(
                        _busId,
                        DateTime.UtcNow,
                        sw.Elapsed,
                        request == null ? 0 : request.Length,
                        responseBytes,
                        noResponse,
                        completed);
                }
            }
        }

        public void Dispose() => _port.Dispose();
    }
}
