using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstueMpsReplacement
{
    public sealed class PollingEngine : IDisposable
    {
        private readonly ConfigNode _root;
        private readonly LogService _log;
        private readonly ValueCache _cache;
        private readonly ConfigNode _scope;
        private readonly PollingStatistics _statistics;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _tasks = new List<Task>();

        internal PollingEngine(ConfigNode root, ConfigNode scope, LogService log, ValueCache cache, PollingStatistics statistics)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _scope = scope ?? root;
            _log = log;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _statistics = statistics;
        }

        public void Start()
        {
            ConfigNode[] buses;
            if (_scope.Kind == ConfigNodeKind.Main)
                buses = ConfigTree.DescendantsAndSelf(_root)
                    .Where(n => n.Kind == ConfigNodeKind.Bus && n.EffectiveEnabled)
                    .ToArray();
            else
            {
                var scopedBus = _scope.Kind == ConfigNodeKind.Bus ? _scope : ConfigTree.AncestorOrSelf(_scope, ConfigNodeKind.Bus);
                buses = scopedBus == null ? new ConfigNode[0] : new[] { scopedBus };
            }
            foreach (var bus in buses)
            {
                var localBus = bus;
                _tasks.Add(Task.Factory.StartNew(() => RunBus(localBus, _cts.Token), _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            _log?.Info("Polling started: buses=" + buses.Length);
        }

        private void RunBus(ConfigNode bus, CancellationToken token)
        {
            SerialTransport transport = null;
            IDisposable busLogScope = null;
            try
            {
                busLogScope = _log?.BeginContext(bus, null);
                var typeNode = MppSettings.GetString(bus, "TypeNode", "COM");
                if (!typeNode.Equals("COM", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Warn(bus.Name + ": transport " + typeNode + " is not implemented in v0.2");
                    return;
                }

                var driver = CreateDriver(bus);
                if (driver == null)
                {
                    _log?.Warn(bus.Name + ": no driver for TypePlugin=" + MppSettings.GetString(bus, "TypePlugin", ""));
                    return;
                }

                var portName = MppSettings.GetComName(bus);
                var baud = MppSettings.GetInt(bus, "COMSpeed", 9600);
                var dataBits = MppSettings.GetInt(bus, "COMData", 8);
                var parity = MppSettings.GetParity(bus);
                var stopBits = MppSettings.GetStopBits(bus);

                transport = new SerialTransport(portName, baud, dataBits, parity, stopBits, _log, bus.Id, _statistics);
                _log?.Info(bus.Name + ": line worker " + portName + ", devices=" + DevicesForBus(bus).Length +
                           ", serial=" + baud + "/" + dataBits + "/" + parity + "/" + stopBits);

                var nextDue = new Dictionary<Guid, DateTime>();
                while (!token.IsCancellationRequested)
                {
                    if (!bus.EffectiveEnabled)
                    {
                        foreach (var dev in bus.Children.Where(x => x.Kind == ConfigNodeKind.Device))
                            MarkOutOfService(dev);
                        Thread.Sleep(250);
                        continue;
                    }

                    var devices = DevicesForBus(bus);
                    var now = DateTime.UtcNow;
                    foreach (var dev in devices)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!dev.EffectiveEnabled)
                        {
                            MarkOutOfService(dev);
                            continue;
                        }

                        MarkDisabledTags(dev);

                        DateTime due;
                        if (!nextDue.TryGetValue(dev.Id, out due) || now >= due)
                        {
                            try
                            {
                                using (_log?.BeginContext(bus, dev))
                                {
                                    driver.Poll(dev, transport, _cache.Publish, token);
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                using (_log?.BeginContext(bus, dev))
                                {
                                    _log?.ReportReadError(ex);
                                    _log?.Error(dev.OpcItemId + ": unhandled device poll error: " + ex.Message);
                                }
                                MarkBadCommunication(dev);
                            }
                            var seconds = Math.Max(1, MppSettings.GetInt(dev, "ReadingInterval", 60));
                            nextDue[dev.Id] = DateTime.UtcNow.AddSeconds(seconds);
                        }
                    }

                    Thread.Sleep(50);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log?.Error(bus.Name + ": polling thread: " + ex); }
            finally
            {
                if (transport != null) transport.Dispose();
                if (busLogScope != null) busLogScope.Dispose();
            }
        }


        private ConfigNode[] DevicesForBus(ConfigNode bus)
        {
            if (_scope.Kind == ConfigNodeKind.Main || _scope.Kind == ConfigNodeKind.Bus)
                return bus.Children.Where(x => x.Kind == ConfigNodeKind.Device).ToArray();

            var scopedDevice = _scope.Kind == ConfigNodeKind.Device ? _scope : ConfigTree.AncestorOrSelf(_scope, ConfigNodeKind.Device);
            if (scopedDevice != null && scopedDevice.Parent == bus) return new[] { scopedDevice };
            return new ConfigNode[0];
        }

        private IProtocolDriver CreateDriver(ConfigNode bus)
        {
            var plugin = MppSettings.GetString(bus, "TypePlugin", string.Empty);
            if (plugin.Equals("MERCURY", StringComparison.OrdinalIgnoreCase)) return new Mercury230Driver(_log);
            if (plugin.Equals("SET4", StringComparison.OrdinalIgnoreCase) ||
                plugin.IndexOf("SET4", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Set4Driver(_log);
            return null;
        }


        private void MarkBadCommunication(ConfigNode device)
        {
            foreach (var tag in ConfigTree.Tags(device))
                if (tag.EffectiveEnabled)
                    _cache.MarkQuality(tag, ValueQuality.BadCommunication, true);
        }

        private void MarkDisabledTags(ConfigNode device)
        {
            foreach (var tag in ConfigTree.Tags(device))
                if (!tag.EffectiveEnabled)
                    _cache.MarkQuality(tag, ValueQuality.BadOutOfService, true);
        }

        private void MarkOutOfService(ConfigNode device)
        {
            foreach (var tag in ConfigTree.Tags(device))
                _cache.MarkQuality(tag, ValueQuality.BadOutOfService, true);
        }

        public void Stop()
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            try { Task.WaitAll(_tasks.ToArray(), 3000); } catch { }
            _log?.Info("Polling stopped");
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
