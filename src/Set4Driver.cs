using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AstueMpsReplacement
{
    public sealed class Set4Driver : IProtocolDriver
    {
        private sealed class FloatRead
        {
            public string Suffix;
            public byte Rwri;
            public FloatRead(string suffix, byte rwri) { Suffix = suffix; Rwri = rwri; }
        }

        private static readonly FloatRead[] InstantaneousReads =
        {
            new FloatRead("Power.P.Sum",   0x00),
            new FloatRead("Power.P.A",     0x01),
            new FloatRead("Power.P.B",     0x02),
            new FloatRead("Power.P.C",     0x03),

            new FloatRead("Power.Q.Sum",   0x04),
            new FloatRead("Power.Q.A",     0x05),
            new FloatRead("Power.Q.B",     0x06),
            new FloatRead("Power.Q.C",     0x07),

            new FloatRead("Power.S.Sum",   0x08),
            new FloatRead("Power.S.A",     0x09),
            new FloatRead("Power.S.B",     0x0A),
            new FloatRead("Power.S.C",     0x0B),

            new FloatRead("Measure.U.A",   0x11),
            new FloatRead("Measure.U.B",   0x12),
            new FloatRead("Measure.U.C",   0x13),
            new FloatRead("Measure.U.AB",  0x14),
            new FloatRead("Measure.U.BC",  0x15),
            new FloatRead("Measure.U.CA",  0x16),

            new FloatRead("Measure.I.A",   0x21),
            new FloatRead("Measure.I.B",   0x22),
            new FloatRead("Measure.I.C",   0x23),

            new FloatRead("Power.Cos.Sum", 0x30),
            new FloatRead("Power.Cos.A",   0x31),
            new FloatRead("Power.Cos.B",   0x32),
            new FloatRead("Power.Cos.C",   0x33),

            new FloatRead("Measure.F",     0x40)
        };

        private readonly LogService _log;
        private readonly HashSet<Guid> _noticeLogged = new HashSet<Guid>();

        public Set4Driver(LogService log) { _log = log; }
        public string Name => "SET-4TM";

        public void Poll(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (publish == null) throw new ArgumentNullException(nameof(publish));
            if (!device.EffectiveEnabled) return;

            var addressInt = MppSettings.GetSet4Address(device);
            if (addressInt < 0 || addressInt > 255)
                throw new InvalidOperationException("Некорректный адрес SET4: " + addressInt);

            var address = (byte)addressInt;
            var timeout = Math.Max(300, MppSettings.GetInt(device, "AnswerTimeOut", 1000));
            var repeats = Math.Max(0, MppSettings.GetInt(device, "RepeatCount", 3));
            const int responseSilenceMs = 10;

            try
            {
                token.ThrowIfCancellationRequested();
                LogModeOnce(device, address);

                var open = ExchangeChecked(
                    transport,
                    Set4Protocol.OpenChannel(address, MppSettings.GetSet4Password(device)),
                    address, timeout, responseSilenceMs, repeats, "SET4 OPEN");
                Set4Protocol.ValidateOpenResponse(open, address);

                PublishStateTags(device, publish, true, false, ValueQuality.Good);
                MarkNotImplementedTagsUncertain(device, publish);

                foreach (var read in InstantaneousReads)
                {
                    token.ThrowIfCancellationRequested();
                    var tag = ConfigTree.FindBySuffix(device, read.Suffix);
                    if (tag == null || !tag.EffectiveEnabled) continue;

                    try
                    {
                        var response = ExchangeChecked(
                            transport,
                            Set4Protocol.ReadFloat(address, 0x00, read.Rwri),
                            address, timeout, responseSilenceMs, repeats,
                            "SET4 READ " + read.Suffix);

                        var value = Set4Protocol.ParseFloatResponse(response, address);
                        Publish(tag, (double)value, ValueQuality.Good, publish);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log?.Warn(device.OpcItemId + " " + read.Suffix + ": " + ex.Message);
                        publish(tag, new TagValue
                        {
                            Quality = ValueQuality.Uncertain,
                            Timestamp = DateTime.MinValue,
                            PreserveValueAndTimestamp = true
                        });
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Error(device.OpcItemId + ": " + ex.Message);
                PublishBadCommunication(device, publish);
            }
        }

        private void LogModeOnce(ConfigNode device, byte address)
        {
            if (!_noticeLogged.Add(device.Id)) return;
            _log?.Info(device.OpcItemId +
                ": SET4 read-only; address=" + address +
                "; implemented=instantaneous P/Q/S, U, I, cos(phi), F; " +
                "energy/archive tags remain Uncertain pending wire-format validation");
        }

        private byte[] ExchangeChecked(SerialTransport transport, byte[] request, byte address,
            int timeout, int silence, int repeats, string label)
        {
            Exception last = null;
            for (var attempt = 0; attempt <= repeats; attempt++)
            {
                try
                {
                    var response = transport.Exchange(request, silence, timeout);
                    if (response == null || response.Length == 0) throw new TimeoutException(label + ": timeout");
                    if (!Set4Protocol.CheckCrc(response))
                        throw new InvalidOperationException(label + ": CRC error: " + Hex(response));
                    if (response[0] != address)
                        throw new InvalidOperationException(label + ": wrong address: " + Hex(response));
                    return response;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < repeats)
                        _log?.Warn(label + ": retry " + (attempt + 1) + ": " + ex.Message);
                }
            }
            if (last != null) _log?.ReportReadError(last);
            throw last ?? new InvalidOperationException(label + " failed");
        }

        private static bool IsInstantaneousImplemented(ConfigNode tag)
        {
            if (tag == null) return false;
            var id = tag.OpcItemId ?? string.Empty;
            return InstantaneousReads.Any(r =>
                id.EndsWith(r.Suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static void MarkNotImplementedTagsUncertain(ConfigNode device, Action<ConfigNode, TagValue> publish)
        {
            foreach (var tag in ConfigTree.Tags(device))
            {
                if (!tag.EffectiveEnabled || IsImplementedOrStateTag(tag)) continue;
                publish(tag, new TagValue
                {
                    Quality = ValueQuality.Uncertain,
                    Timestamp = DateTime.MinValue,
                    PreserveValueAndTimestamp = true
                });
            }
        }

        private static bool IsImplementedOrStateTag(ConfigNode tag)
        {
            var id = tag?.OpcItemId ?? string.Empty;
            return IsInstantaneousImplemented(tag) ||
                   id.EndsWith(".Available", StringComparison.OrdinalIgnoreCase) ||
                   id.EndsWith(".Failure", StringComparison.OrdinalIgnoreCase) ||
                   id.EndsWith(".UnscheduledRequest", StringComparison.OrdinalIgnoreCase);
        }

        private static void PublishBadCommunication(ConfigNode device, Action<ConfigNode, TagValue> publish)
        {
            foreach (var tag in ConfigTree.Tags(device))
            {
                if (!tag.EffectiveEnabled) continue;
                publish(tag, new TagValue
                {
                    Quality = ValueQuality.BadCommunication,
                    Timestamp = DateTime.MinValue,
                    PreserveValueAndTimestamp = true
                });
            }
            PublishStateTags(device, publish, false, true, ValueQuality.Good);
        }

        private static void PublishStateTags(ConfigNode device, Action<ConfigNode, TagValue> publish,
            bool available, bool failure, ValueQuality quality)
        {
            var availableTag = ConfigTree.FindBySuffix(device, "Available");
            var failureTag = ConfigTree.FindBySuffix(device, "Failure");
            if (availableTag != null && availableTag.EffectiveEnabled) Publish(availableTag, available, quality, publish);
            if (failureTag != null && failureTag.EffectiveEnabled) Publish(failureTag, failure, quality, publish);
        }

        private static void Publish(ConfigNode tag, object value, ValueQuality quality, Action<ConfigNode, TagValue> publish)
        {
            publish(tag, new TagValue { Value = value, Quality = quality, Timestamp = DateTime.Now });
        }

        private static string Hex(byte[] b)
        {
            return b == null ? "<null>" : string.Join(" ", b.Select(x => x.ToString("X2")));
        }
    }
}
