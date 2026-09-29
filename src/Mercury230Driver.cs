using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace AstueMpsReplacement
{
    public sealed class Mercury230Driver : IProtocolDriver
    {
        private readonly LogService _log;
        private readonly Dictionary<Guid, DateTime> _lastTimeCorrection = new Dictionary<Guid, DateTime>();
        private readonly Dictionary<Guid, DateTime> _lastTimeNotice = new Dictionary<Guid, DateTime>();
        private readonly HashSet<Guid> _modelLogged = new HashSet<Guid>();

        public Mercury230Driver(LogService log) { _log = log; }
        public string Name => "MERCURY 230";

        private sealed class SpDef
        {
            public string Suffix;
            public byte Bwri;
            public double Scale;
            public SignKind Sign;
            public ScaleKind TransformerScale;
        }

        private sealed class MeterVariant
        {
            public int Constant;
            public bool HasPowerProfile;
            public int Phases;
            public int Directions;
        }

        private sealed class EnergyPeriod
        {
            public string Suffix;
            public byte ArrayNumber;
            public byte Month;
        }

        private sealed class EnergyDirection
        {
            public string Name;
            public int Offset;
        }

        private enum SignKind { None, Active, Reactive }
        private enum ScaleKind { None, Voltage, Current, Power }

        private static readonly SpDef[] Sp =
        {
            D("SP.P.Sum",0x00,0.01,SignKind.Active,ScaleKind.Power),
            D("SP.P.A",0x01,0.01,SignKind.Active,ScaleKind.Power),
            D("SP.P.B",0x02,0.01,SignKind.Active,ScaleKind.Power),
            D("SP.P.C",0x03,0.01,SignKind.Active,ScaleKind.Power),
            D("SP.Q.Sum",0x04,0.01,SignKind.Reactive,ScaleKind.Power),
            D("SP.Q.A",0x05,0.01,SignKind.Reactive,ScaleKind.Power),
            D("SP.Q.B",0x06,0.01,SignKind.Reactive,ScaleKind.Power),
            D("SP.Q.C",0x07,0.01,SignKind.Reactive,ScaleKind.Power),
            D("SP.S.Sum",0x08,0.01,SignKind.None,ScaleKind.Power),
            D("SP.S.A",0x09,0.01,SignKind.None,ScaleKind.Power),
            D("SP.S.B",0x0A,0.01,SignKind.None,ScaleKind.Power),
            D("SP.S.C",0x0B,0.01,SignKind.None,ScaleKind.Power),
            D("SP.U.A",0x11,0.01,SignKind.None,ScaleKind.Voltage),
            D("SP.U.B",0x12,0.01,SignKind.None,ScaleKind.Voltage),
            D("SP.U.C",0x13,0.01,SignKind.None,ScaleKind.Voltage),
            D("SP.I.A",0x21,0.001,SignKind.None,ScaleKind.Current),
            D("SP.I.B",0x22,0.001,SignKind.None,ScaleKind.Current),
            D("SP.I.C",0x23,0.001,SignKind.None,ScaleKind.Current),
            D("SP.COS_F.Sum",0x30,0.001,SignKind.Active,ScaleKind.None),
            D("SP.COS_F.A",0x31,0.001,SignKind.Active,ScaleKind.None),
            D("SP.COS_F.B",0x32,0.001,SignKind.Active,ScaleKind.None),
            D("SP.COS_F.C",0x33,0.001,SignKind.Active,ScaleKind.None),
            D("SP.Frequency",0x40,0.01,SignKind.None,ScaleKind.None),
            D("SP.ANGLE.U_1_2",0x51,0.01,SignKind.None,ScaleKind.None),
            D("SP.ANGLE.U_1_3",0x52,0.01,SignKind.None,ScaleKind.None),
            D("SP.ANGLE.U_2_3",0x53,0.01,SignKind.None,ScaleKind.None)
        };

        private static readonly EnergyPeriod[] EnergyPeriods =
        {
            new EnergyPeriod { Suffix = "Eres",         ArrayNumber = 0x00, Month = 0 },
            new EnergyPeriod { Suffix = "Eyear",        ArrayNumber = 0x01, Month = 0 },
            new EnergyPeriod { Suffix = "Eyear_before", ArrayNumber = 0x02, Month = 0 },
            new EnergyPeriod { Suffix = "Eday",         ArrayNumber = 0x04, Month = 0 },
            new EnergyPeriod { Suffix = "Eday_before",  ArrayNumber = 0x05, Month = 0 }
        };

        private static readonly EnergyDirection[] ForwardEnergyDirections =
        {
            new EnergyDirection { Name = "A+", Offset = 1 },
            new EnergyDirection { Name = "R+", Offset = 9 }
        };

        private static readonly EnergyDirection[] AllEnergyDirections =
        {
            new EnergyDirection { Name = "A+", Offset = 1 },
            new EnergyDirection { Name = "A-", Offset = 5 },
            new EnergyDirection { Name = "R+", Offset = 9 },
            new EnergyDirection { Name = "R-", Offset = 13 }
        };

        private static SpDef D(string suffix, byte bwri, double scale, SignKind sign, ScaleKind transformerScale)
        {
            return new SpDef { Suffix = suffix, Bwri = bwri, Scale = scale, Sign = sign, TransformerScale = transformerScale };
        }

        public void Poll(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (publish == null) throw new ArgumentNullException(nameof(publish));
            if (!device.EffectiveEnabled) return;

            var addressInt = MppSettings.GetMercuryAddress(device);
            if (addressInt < 0 || addressInt > 240) throw new InvalidOperationException("Некорректный адрес Mercury: " + addressInt);
            var address = (byte)addressInt;
            var bus = device.Parent;
            var answerTimeout = Math.Max(300, MppSettings.GetInt(device, "AnswerTimeOut", 1000));
            var repeats = Math.Max(0, MppSettings.GetInt(device, "RepeatCount", 2));
            var silence = Math.Max(20, Math.Min(500, MppSettings.GetInt(bus, "CharacterInterval", 90)));

            try
            {
                token.ThrowIfCancellationRequested();
                ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x00), address, answerTimeout, silence, repeats, "CHANNEL TEST");

                var level = MppSettings.GetPollingAccessLevel(device);
                var password = MppSettings.GetMercuryPassword(device, level);
                var openBody = new List<byte> { 0x01, (byte)level };
                openBody.AddRange(password);
                var open = ExchangeChecked(transport, MercuryProtocol.Frame(address, openBody.ToArray()), address, answerTimeout, silence, repeats, "OPEN CHANNEL L" + level);
                EnsureStatusOk(open, "OPEN CHANNEL");

                try
                {
                    var model = MercuryModelProfile.Detect(device);
                    LogModelOnce(device, model);
                    PublishAddress(device, publish, address);

                    TryFeatureTags(device, "IP identity", new[] { "IP.SerialNumber", "IP.ReleaseDate" }, publish, () =>
                        PublishIdentity(device, transport, publish, token, address, answerTimeout, silence, repeats));

                    if (model.HasClock)
                    {
                        TryFeatureTags(device, "IP clock", new[] { "IP.CurrentTime" }, publish, () =>
                            PublishClockAndMaybeCorrect(device, transport, publish, token, address, answerTimeout, silence, repeats));
                    }

                    MeterVariant variant = null;
                    TryFeatureTags(device, "IP variant",
                        new[] { "IP.DeviceConstant", "IP.PowerProfile", "IP.DirectionsNumber", "IP.PhasesNumber", "IP.Nominal_U", "IP.Nominal_I" },
                        publish, () =>
                        {
                            variant = ReadAndPublishVariant(device, transport, publish, token, address, answerTimeout, silence, repeats);
                        });

                    if (model.HasSp)
                    {
                        TryFeature(device, "SP", "SP", publish, () =>
                            PublishSp(device, transport, publish, token, address, answerTimeout, silence, repeats));
                    }

                    if (model.HasEnergy)
                    {
                        TryFeature(device, "Energy", "Energy", publish, () =>
                            PublishEnergy(device, model, transport, publish, token, address, answerTimeout, silence, repeats));
                    }

                    if (model.HasPowerProfile)
                    {
                        TryFeature(device, "PowerProfile", "PowerProfile", publish, () =>
                            PublishPowerProfile(device, transport, publish, token, address, answerTimeout, silence, repeats, variant));
                    }

                    if (model.HasEventsLog)
                    {
                        TryFeature(device, "EventsLog", "EventsLog", publish, () =>
                            PublishEvents(device, transport, publish, token, address, answerTimeout, silence, repeats));
                    }

                    PublishStateTags(device, publish, true, false, ValueQuality.Good);
                }
                finally
                {
                    try
                    {
                        var close = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x02), address, answerTimeout, silence, 0, "CLOSE CHANNEL");
                        EnsureStatusOk(close, "CLOSE CHANNEL");
                    }
                    catch (Exception ex) { _log?.Warn(device.OpcItemId + ": close channel: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Error(device.OpcItemId + ": " + ex.Message);
                PublishBadCommunication(device, publish);
            }
        }

        private void TryFeature(ConfigNode device, string label, string suffixPrefix, Action<ConfigNode, TagValue> publish, Action action)
        {
            if (!HasEnabledTag(device, suffixPrefix)) return;
            try { action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Warn(device.OpcItemId + " " + label + ": " + ex.Message);
                MarkSuffixBad(device, suffixPrefix, publish);
            }
        }

        private void TryFeatureTags(ConfigNode device, string label, string[] suffixes,
            Action<ConfigNode, TagValue> publish, Action action)
        {
            if (suffixes == null || suffixes.Length == 0) return;
            if (!suffixes.Any(s => HasEnabledTag(device, s))) return;

            try { action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Warn(device.OpcItemId + " " + label + ": " + ex.Message);
                foreach (var suffix in suffixes)
                    MarkSuffixBad(device, suffix, publish);
            }
        }

        private void LogModelOnce(ConfigNode device, MercuryModelProfile model)
        {
            if (device == null || model == null) return;
            if (!_modelLogged.Add(device.Id)) return;
            _log?.Info(device.OpcItemId + ": model=" + model.DisplayName +
                       "; capabilities=[" + model.CapabilitiesText + "]");
        }

        private static bool HasEnabledTag(ConfigNode device, string suffixPrefix)
        {
            var marker = "." + suffixPrefix;
            foreach (var tag in ConfigTree.Tags(device))
                if (tag.EffectiveEnabled && (tag.OpcItemId.EndsWith(marker, StringComparison.OrdinalIgnoreCase) ||
                                             tag.OpcItemId.IndexOf(marker + ".", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            return false;
        }

        private static void MarkSuffixBad(ConfigNode device, string suffixPrefix, Action<ConfigNode, TagValue> publish)
        {
            var marker = "." + suffixPrefix;
            foreach (var tag in ConfigTree.Tags(device))
            {
                if (!tag.EffectiveEnabled) continue;
                if (!tag.OpcItemId.EndsWith(marker, StringComparison.OrdinalIgnoreCase) &&
                    tag.OpcItemId.IndexOf(marker + ".", StringComparison.OrdinalIgnoreCase) < 0) continue;
                publish(tag, new TagValue
                {
                    Quality = ValueQuality.BadCommunication,
                    Timestamp = DateTime.MinValue,
                    PreserveValueAndTimestamp = true
                });
            }
        }

        private static void PublishAddress(ConfigNode device, Action<ConfigNode, TagValue> publish, byte address)
        {
            var tag = ConfigTree.FindBySuffix(device, "IP.Address");
            if (tag != null && tag.EffectiveEnabled) Publish(tag, (int)address, ValueQuality.Good, publish);
        }

        private void PublishIdentity(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats)
        {
            var serialTag = ConfigTree.FindBySuffix(device, "IP.SerialNumber");
            var releaseTag = ConfigTree.FindBySuffix(device, "IP.ReleaseDate");
            if ((serialTag == null || !serialTag.EffectiveEnabled) && (releaseTag == null || !releaseTag.EffectiveEnabled)) return;

            token.ThrowIfCancellationRequested();
            var r = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x08, 0x00), address, timeout, silence, repeats, "READ SERIAL/DATE");
            if (r.Length == 4) { EnsureStatusOk(r, "READ SERIAL/DATE"); throw new InvalidOperationException("serial/date: status-only response"); }
            if (r.Length < 10) throw new InvalidOperationException("Короткий ответ serial/date: " + Hex(r));

            var serial = (uint)(r[1] * 1000000u + r[2] * 10000u + r[3] * 100u + r[4]);
            if (serialTag != null && serialTag.EffectiveEnabled) Publish(serialTag, serial, ValueQuality.Good, publish);

            var date = string.Format(CultureInfo.InvariantCulture, "{0:00}.{1:00}.{2:0000}", r[5], r[6], 2000 + r[7]);
            if (releaseTag != null && releaseTag.EffectiveEnabled) Publish(releaseTag, date, ValueQuality.Good, publish);
        }

        private MeterVariant ReadAndPublishVariant(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats)
        {
            token.ThrowIfCancellationRequested();
            // Legacy 08 12 is supported by Mercury 230ART/ART-P and is enough for the pulse constant.
            var r = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x08, 0x12), address, timeout, silence, repeats, "READ VARIANT");
            if (r.Length == 4) { EnsureStatusOk(r, "READ VARIANT"); throw new InvalidOperationException("variant: status-only response"); }
            if (r.Length < 9) throw new InvalidOperationException("Короткий ответ variant: " + Hex(r));

            var b1 = r[1];
            var b2 = r[2];
            var constant = ConstantFromCode(b2 & 0x0F);
            var info = new MeterVariant
            {
                Constant = constant,
                Directions = (b2 & 0x80) != 0 ? 1 : 2,
                HasPowerProfile = (b2 & 0x20) != 0,
                Phases = (b2 & 0x10) != 0 ? 1 : 3
            };

            PublishIfEnabled(device, "IP.DeviceConstant", constant, publish);
            PublishIfEnabled(device, "IP.PowerProfile", info.HasPowerProfile, publish);
            PublishIfEnabled(device, "IP.DirectionsNumber", info.Directions, publish);
            PublishIfEnabled(device, "IP.PhasesNumber", info.Phases, publish);

            // First variant byte: bit 3 selects 57.7/230 V; bits 1..0 select nominal current.
            var nominalU = (b1 & 0x08) != 0 ? 230.0f : 57.7f;
            var currentCode = b1 & 0x03;
            float nominalI = currentCode == 1 ? 1.0f : (currentCode == 2 ? 10.0f : 5.0f);
            PublishIfEnabled(device, "IP.Nominal_U", nominalU, publish);
            PublishIfEnabled(device, "IP.Nominal_I", nominalI, publish);

            var configuredModel = MercuryModelProfile.Detect(device);
            if (configuredModel.Kind != MercuryModelKind.Unknown)
            {
                if (configuredModel.HasPowerProfile != info.HasPowerProfile)
                    _log?.Warn(device.OpcItemId + ": вариант исполнения сообщает PowerProfile=" +
                               info.HasPowerProfile + ", шаблон " + configuredModel.Id +
                               " ожидает " + configuredModel.HasPowerProfile + ".");
                if (configuredModel.HasReverseEnergy && info.Directions < 2)
                    _log?.Warn(device.OpcItemId + ": шаблон " + configuredModel.Id +
                               " содержит обратные направления A-/R-, но вариант исполнения сообщает Directions=" + info.Directions + ".");
            }
            return info;
        }

        private void PublishClockAndMaybeCorrect(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats)
        {
            token.ThrowIfCancellationRequested();
            var r = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x04, 0x00), address, timeout, silence, repeats, "READ CLOCK");
            if (r.Length == 4) { EnsureStatusOk(r, "READ CLOCK"); throw new InvalidOperationException("clock: status-only response"); }
            if (r.Length < 11) throw new InvalidOperationException("Короткий ответ clock: " + Hex(r));

            DateTime meterTime;
            if (!TryParseClock8(r, 1, out meterTime)) throw new InvalidOperationException("Некорректное время счетчика: " + Hex(r));
            PublishIfEnabled(device, "IP.CurrentTime", meterTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture), publish);

            if (!MppSettings.GetTimeCorrection(device)) return;

            var target = MppSettings.GetUseTimeOffset(device)
                ? DateTime.UtcNow.AddHours(MppSettings.GetTimeOffsetHours(device))
                : DateTime.Now;
            var deltaSeconds = (target - meterTime).TotalSeconds;
            var threshold = MppSettings.GetTimeSyncThresholdSeconds(device);
            if (Math.Abs(deltaSeconds) <= threshold) return;

            if (Math.Abs(deltaSeconds) > 240.0)
            {
                LogTimeNotice(device, "рассинхронизация часов " + deltaSeconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) +
                    " с > 240 с; автоматическая коррекция 0Dh не выполняется.");
                return;
            }

            if (!MppSettings.GetTimeSyncWriteEnabled(device))
            {
                LogTimeNotice(device, "TimeCorrection=true, разница " + deltaSeconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) +
                    " с, но TimeSyncWriteEnabled=false — запись времени заблокирована настройкой безопасности.");
                return;
            }

            DateTime last;
            if (_lastTimeCorrection.TryGetValue(device.Id, out last) && last.Date == DateTime.Today) return;

            token.ThrowIfCancellationRequested();
            var correction = ExchangeChecked(transport,
                MercuryProtocol.Frame(address, 0x03, 0x0D, ToBcd(target.Second), ToBcd(target.Minute), ToBcd(target.Hour)),
                address, timeout, silence, repeats, "TIME CORRECTION 0Dh");
            EnsureStatusOk(correction, "TIME CORRECTION 0Dh");
            _lastTimeCorrection[device.Id] = DateTime.Now;
            _log?.Info(device.OpcItemId + ": коррекция времени 0Dh выполнена; до коррекции Δt=" +
                       deltaSeconds.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " с.");
        }

        private void LogTimeNotice(ConfigNode device, string message)
        {
            DateTime last;
            var now = DateTime.Now;
            if (_lastTimeNotice.TryGetValue(device.Id, out last) && (now - last).TotalMinutes < 30) return;
            _lastTimeNotice[device.Id] = now;
            _log?.Warn(device.OpcItemId + ": " + message);
        }

        private void PublishSp(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats)
        {
            var useKt = MppSettings.GetUseKt(device);
            var ktu = useKt ? MppSettings.GetKtu(device) : 1.0;
            var kti = useKt ? MppSettings.GetKti(device) : 1.0;

            foreach (var d in Sp)
            {
                token.ThrowIfCancellationRequested();
                var tag = ConfigTree.FindBySuffix(device, d.Suffix);
                if (tag == null || !tag.EffectiveEnabled) continue;

                var r = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x08, 0x11, d.Bwri), address, timeout, silence, repeats, "READ " + d.Suffix);
                if (r.Length == 4) { EnsureStatusOk(r, d.Suffix); throw new InvalidOperationException(d.Suffix + ": status-only response"); }
                if (r.Length < 6) throw new InvalidOperationException("Короткий ответ " + d.Suffix + ": " + Hex(r));

                var d1 = r[1] & 0x3F;
                long raw = ((long)d1 << 16) | ((long)r[3] << 8) | r[2];
                var value = raw * d.Scale;

                if (d.Sign == SignKind.Active && (r[1] & 0x80) != 0) value = -value;
                if (d.Sign == SignKind.Reactive && (r[1] & 0x40) != 0) value = -value;
                if (d.TransformerScale == ScaleKind.Voltage) value *= ktu;
                else if (d.TransformerScale == ScaleKind.Current) value *= kti;
                else if (d.TransformerScale == ScaleKind.Power) value *= ktu * kti;

                Publish(tag, (float)value, ValueQuality.Good, publish);
            }
        }

        private void PublishEnergy(ConfigNode device, MercuryModelProfile model, SerialTransport transport,
            Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats)
        {
            var transformer = MppSettings.GetUseKt(device) ? MppSettings.GetKtu(device) * MppSettings.GetKti(device) : 1.0;

            // M230AR in the MPS template is single-tariff and has paths Energy.A+.Eres / Energy.R+.Eres.
            // M230ART / M230ARTP use T1..T4 + sum, forward A+/R+.
            // M230ART2P additionally has A-/R- and Emonth_1..Emonth_12.
            var tariffs = model.HasTariffs
                ? new[]
                {
                    new { Prefix = "T1.", Name = "T1", Number = (byte)1 },
                    new { Prefix = "T2.", Name = "T2", Number = (byte)2 },
                    new { Prefix = "T3.", Name = "T3", Number = (byte)3 },
                    new { Prefix = "T4.", Name = "T4", Number = (byte)4 },
                    new { Prefix = "T1+T2+T3+T4.", Name = "SUM", Number = (byte)0 }
                }
                : new[]
                {
                    new { Prefix = "", Name = "single", Number = (byte)0 }
                };

            var periods = new List<EnergyPeriod>(EnergyPeriods);
            if (model.HasMonthlyEnergy)
            {
                for (byte month = 1; month <= 12; month++)
                    periods.Add(new EnergyPeriod
                    {
                        Suffix = "Emonth_" + month.ToString(CultureInfo.InvariantCulture),
                        ArrayNumber = 0x03,
                        Month = month
                    });
            }

            var directions = model.HasReverseEnergy ? AllEnergyDirections : ForwardEnergyDirections;

            foreach (var tariff in tariffs)
            {
                foreach (var period in periods)
                {
                    var requested = new List<Tuple<ConfigNode, EnergyDirection>>();
                    foreach (var direction in directions)
                    {
                        var suffix = "Energy." + tariff.Prefix + direction.Name + "." + period.Suffix;
                        var tag = ConfigTree.FindBySuffix(device, suffix);
                        if (tag != null && tag.EffectiveEnabled)
                            requested.Add(Tuple.Create(tag, direction));
                    }
                    if (requested.Count == 0) continue;

                    token.ThrowIfCancellationRequested();

                    // 05h: high nibble = array, low nibble = month for array 3h.
                    var arrayByte = (byte)((period.ArrayNumber << 4) | (period.Month & 0x0F));
                    var response = ExchangeChecked(transport,
                        MercuryProtocol.Frame(address, 0x05, arrayByte, tariff.Number),
                        address, timeout, silence, repeats,
                        "ENERGY " + tariff.Name + " " + period.Suffix);

                    if (response.Length == 4)
                    {
                        EnsureStatusOk(response, "ENERGY");
                        throw new InvalidOperationException("ENERGY: status-only response");
                    }
                    if (response.Length < 19)
                        throw new InvalidOperationException("Короткий ответ Energy: " + Hex(response));

                    foreach (var pair in requested)
                        PublishEnergyValue(pair.Item1, ReadMercuryEnergy32(response, pair.Item2.Offset), transformer, publish);
                }
            }
        }

        private static void PublishEnergyValue(ConfigNode tag, uint rawWh, double transformer, Action<ConfigNode, TagValue> publish)
        {
            if (rawWh == uint.MaxValue)
            {
                publish(tag, new TagValue { Value = null, Quality = ValueQuality.Uncertain, Timestamp = DateTime.Now });
                return;
            }
            var value = rawWh / 1000.0 * transformer;
            Publish(tag, (float)value, ValueQuality.Good, publish);
        }

        private void PublishPowerProfile(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats, MeterVariant variant)
        {
            token.ThrowIfCancellationRequested();
            var last = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x08, 0x13), address, timeout, silence, repeats, "PROFILE LAST POINTER");
            if (last.Length == 4) { EnsureStatusOk(last, "PROFILE LAST POINTER"); throw new InvalidOperationException("profile pointer: status-only response"); }
            if (last.Length < 12) throw new InvalidOperationException("Короткий ответ profile pointer: " + Hex(last));

            // Mercury returns the profile slot number; each physical record occupies 0x10 bytes.
            var slot = ((int)last[1] << 8) | last[2];
            var physicalAddress = slot << 4;
            if (physicalAddress > 0x1FFFF) physicalAddress &= 0x1FFFF;

            var memorySelector = (byte)(0x03 | ((physicalAddress & 0x10000) != 0 ? 0x80 : 0x00));
            var ah = (byte)((physicalAddress >> 8) & 0xFF);
            var al = (byte)(physicalAddress & 0xFF);

            token.ThrowIfCancellationRequested();
            var r = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x06, memorySelector, ah, al, 0x0F),
                address, timeout, silence, repeats, "PROFILE READ @0x" + physicalAddress.ToString("X5"));
            if (r.Length == 4) { EnsureStatusOk(r, "PROFILE READ"); throw new InvalidOperationException("profile: status-only response"); }
            if (r.Length < 18) throw new InvalidOperationException("Короткий ответ profile: " + Hex(r));

            DateTime profileTime;
            if (!TryParseProfileTime(r, 2, out profileTime)) throw new InvalidOperationException("Некорректное время profile: " + Hex(r));
            var period = r[7];
            if (period <= 0) period = last[9];
            var constant = variant == null ? 0 : variant.Constant;
            if (constant <= 0) throw new InvalidOperationException("Не определена постоянная счетчика для профиля мощности.");

            PublishIfEnabled(device, "PowerProfile.Date", profileTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), publish);
            PublishIfEnabled(device, "PowerProfile.Time", profileTime.ToString("HH:mm", CultureInfo.InvariantCulture), publish);
            PublishIfEnabled(device, "PowerProfile.Period", (int)period, publish);
            PublishIfEnabled(device, "PowerProfile.Flag", (int)r[1], publish);

            var transformer = MppSettings.GetUseKt(device) ? MppSettings.GetKtu(device) * MppSettings.GetKti(device) : 1.0;
            PublishProfilePower(device, "PowerProfile.A+", ReadLe16(r, 8), period, constant, transformer, publish);
            PublishProfilePower(device, "PowerProfile.A-", ReadLe16(r, 10), period, constant, transformer, publish);
            PublishProfilePower(device, "PowerProfile.R+", ReadLe16(r, 12), period, constant, transformer, publish);
            PublishProfilePower(device, "PowerProfile.R-", ReadLe16(r, 14), period, constant, transformer, publish);
        }

        private static void PublishProfilePower(ConfigNode device, string suffix, ushort raw, int period, int constant,
            double transformer, Action<ConfigNode, TagValue> publish)
        {
            var tag = ConfigTree.FindBySuffix(device, suffix);
            if (tag == null || !tag.EffectiveEnabled) return;
            if (raw == 0xFFFF || period <= 0 || constant <= 0)
            {
                publish(tag, new TagValue { Value = null, Quality = ValueQuality.Uncertain, Timestamp = DateTime.Now });
                return;
            }
            var kw = raw * (60.0 / period) / (2.0 * constant) * transformer;
            Publish(tag, (float)kw, ValueQuality.Good, publish);
        }

        private void PublishEvents(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token,
            byte address, int timeout, int silence, int repeats)
        {
            var journal = (byte)MppSettings.GetEventLogNumber(device);
            PublishIfEnabled(device, "EventsLog.AssignedNumber", (int)journal, publish);
            PublishIfEnabled(device, "EventsLog.Description", JournalDescription(journal), publish);

            // FFh asks the meter for the last record and appends its physical record number.
            try
            {
                var latest = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x04, journal, 0xFF), address, timeout, silence, repeats,
                    "EVENTS CURRENT " + journal.ToString("X2"));
                if (latest.Length >= 10)
                {
                    var dataCount = latest.Length - 3;
                    if (dataCount == 7 || dataCount == 13)
                        PublishIfEnabled(device, "EventsLog.CurrentNumber", (int)latest[latest.Length - 3], publish);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn(device.OpcItemId + ": current event record: " + ex.Message);
            }

            for (var i = 0; i < 10; i++)
            {
                token.ThrowIfCancellationRequested();
                var tag = ConfigTree.FindBySuffix(device, "EventsLog.Record_" + (i + 1).ToString("00", CultureInfo.InvariantCulture));
                if (tag == null || !tag.EffectiveEnabled) continue;

                var r = ExchangeChecked(transport, MercuryProtocol.Frame(address, 0x04, journal, (byte)i), address, timeout, silence, repeats,
                    "EVENT " + journal.ToString("X2") + "/" + i);
                if (r.Length == 4)
                {
                    EnsureStatusOk(r, "EVENT");
                    Publish(tag, string.Empty, ValueQuality.Uncertain, publish);
                    continue;
                }
                var dataCount = r.Length - 3;
                if (dataCount != 6 && dataCount != 12)
                {
                    Publish(tag, Hex(r.Skip(1).Take(Math.Max(0, dataCount)).ToArray()), ValueQuality.Uncertain, publish);
                    continue;
                }
                Publish(tag, FormatEventRecord(journal, r, 1, dataCount), ValueQuality.Good, publish);
            }
        }

        private static string FormatEventRecord(byte journal, byte[] frame, int offset, int count)
        {
            DateTime t1;
            if (!TryParseDateTime6(frame, offset, out t1)) return Hex(frame.Skip(offset).Take(count).ToArray());
            var first = t1.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

            if (journal == 0x13 || journal == 0x14)
                return first + "  code=" + Hex(frame.Skip(offset + 6).Take(count - 6).ToArray());

            if (count == 12 && IsPairJournal(journal))
            {
                DateTime t2;
                if (TryParseDateTime6(frame, offset + 6, out t2))
                    return first + " -> " + t2.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return first;
        }

        private static bool IsPairJournal(byte j)
        {
            return j == 0x01 || j == 0x02 || (j >= 0x03 && j <= 0x06) || j == 0x12 ||
                   (j >= 0x17 && j <= 0x1A) || (j >= 0x20 && j <= 0x2F) || (j >= 0x80 && j <= 0x86);
        }

        private static string JournalDescription(byte j)
        {
            switch (j)
            {
                case 0x01: return "Включение/выключение счетчика";
                case 0x02: return "Коррекция часов";
                case 0x03: return "Включение/выключение напряжения фазы 1";
                case 0x04: return "Включение/выключение напряжения фазы 2";
                case 0x05: return "Включение/выключение напряжения фазы 3";
                case 0x06: return "Превышение лимита мощности";
                case 0x07: return "Коррекция тарифного расписания";
                case 0x08: return "Коррекция расписания праздничных дней";
                case 0x09: return "Сброс регистров учтенной энергии";
                case 0x0A: return "Инициализация массива средних мощностей";
                case 0x0B: return "Превышение лимита энергии, тариф 1";
                case 0x0C: return "Превышение лимита энергии, тариф 2";
                case 0x0D: return "Превышение лимита энергии, тариф 3";
                case 0x0E: return "Превышение лимита энергии, тариф 4";
                case 0x0F: return "Коррекция параметров лимита мощности";
                case 0x10: return "Коррекция параметров лимита энергии";
                case 0x11: return "Коррекция параметров учета потерь";
                case 0x12: return "Вскрытие/закрытие корпуса";
                case 0x13: return "Перепрограммирование счетчика";
                case 0x14: return "Слово состояния / самодиагностика";
                case 0x15: return "Коррекция расписания максимумов мощности";
                case 0x16: return "Сброс массива максимумов мощности";
                case 0x17: return "Включение/выключение тока фазы 1";
                case 0x18: return "Включение/выключение тока фазы 2";
                case 0x19: return "Включение/выключение тока фазы 3";
                case 0x1A: return "Магнитное воздействие";
                default: return "Журнал 0x" + j.ToString("X2");
            }
        }

        private static int ConstantFromCode(int code)
        {
            switch (code)
            {
                case 0: return 5000;
                case 1: return 25000;
                case 2: return 1250;
                case 3: return 500;
                case 4: return 1000;
                case 5: return 250;
                default: return 0;
            }
        }

        private static uint ReadMercuryEnergy32(byte[] frame, int offset)
        {
            if (frame == null || offset < 0 || offset + 3 >= frame.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            return ((uint)frame[offset + 1] << 24) |
                   ((uint)frame[offset] << 16) |
                   ((uint)frame[offset + 3] << 8) |
                    frame[offset + 2];
        }

        private static ushort ReadLe16(byte[] frame, int offset)
        {
            return (ushort)(frame[offset] | (frame[offset + 1] << 8));
        }

        private static bool TryParseClock8(byte[] b, int o, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (b == null || o < 0 || o + 7 >= b.Length) return false;
            return TryMakeDateTime(FromBcd(b[o + 6]), FromBcd(b[o + 5]), FromBcd(b[o + 4]),
                FromBcd(b[o + 2]), FromBcd(b[o + 1]), FromBcd(b[o]), out dt);
        }

        private static bool TryParseDateTime6(byte[] b, int o, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (b == null || o < 0 || o + 5 >= b.Length) return false;
            return TryMakeDateTime(FromBcd(b[o + 5]), FromBcd(b[o + 4]), FromBcd(b[o + 3]),
                FromBcd(b[o + 2]), FromBcd(b[o + 1]), FromBcd(b[o]), out dt);
        }

        private static bool TryParseProfileTime(byte[] b, int o, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (b == null || o < 0 || o + 4 >= b.Length) return false;
            return TryMakeDateTime(FromBcd(b[o + 4]), FromBcd(b[o + 3]), FromBcd(b[o + 2]),
                FromBcd(b[o]), FromBcd(b[o + 1]), 0, out dt);
        }

        private static bool TryMakeDateTime(int yy, int month, int day, int hour, int minute, int second, out DateTime dt)
        {
            dt = DateTime.MinValue;
            try
            {
                if (yy < 0 || yy > 99 || month < 1 || month > 12 || day < 1 || day > 31 ||
                    hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return false;
                dt = new DateTime(2000 + yy, month, day, hour, minute, second, DateTimeKind.Unspecified);
                return true;
            }
            catch { return false; }
        }

        private static int FromBcd(byte b)
        {
            var hi = (b >> 4) & 0x0F;
            var lo = b & 0x0F;
            if (hi > 9 || lo > 9) return -1;
            return hi * 10 + lo;
        }

        private static byte ToBcd(int n)
        {
            n = Math.Max(0, Math.Min(99, n));
            return (byte)(((n / 10) << 4) | (n % 10));
        }

        private static void PublishIfEnabled(ConfigNode device, string suffix, object value, Action<ConfigNode, TagValue> publish)
        {
            var tag = ConfigTree.FindBySuffix(device, suffix);
            if (tag != null && tag.EffectiveEnabled) Publish(tag, value, ValueQuality.Good, publish);
        }

        private byte[] ExchangeChecked(SerialTransport transport, byte[] request, byte address, int timeout, int silence, int repeats, string label)
        {
            Exception last = null;
            for (var attempt = 0; attempt <= repeats; attempt++)
            {
                try
                {
                    var r = transport.Exchange(request, silence, timeout);
                    if (r == null || r.Length == 0) throw new TimeoutException(label + ": timeout");
                    if (!MercuryProtocol.CheckCrc(r)) throw new InvalidOperationException(label + ": CRC error: " + Hex(r));
                    if (r[0] != address) throw new InvalidOperationException(label + ": wrong address: " + Hex(r));
                    return r;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < repeats) _log?.Warn(label + ": retry " + (attempt + 1) + ": " + ex.Message);
                }
            }
            if (last != null) _log?.ReportReadError(last);
            throw last ?? new InvalidOperationException(label + " failed");
        }

        private static void EnsureStatusOk(byte[] r, string label)
        {
            if (r == null || r.Length < 4) throw new InvalidOperationException(label + ": invalid status reply");
            if (r[1] != 0x00 && r[1] != 0x80)
                throw new InvalidOperationException(label + ": Mercury status 0x" + r[1].ToString("X2"));
        }

        private static void Publish(ConfigNode tag, object value, ValueQuality q, Action<ConfigNode, TagValue> publish)
        {
            publish(tag, new TagValue { Value = value, Quality = q, Timestamp = DateTime.Now });
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

        private static void PublishStateTags(ConfigNode device, Action<ConfigNode, TagValue> publish, bool available, bool failure, ValueQuality q)
        {
            var av = ConfigTree.FindBySuffix(device, "Available");
            var fl = ConfigTree.FindBySuffix(device, "Failure");
            if (av != null && av.EffectiveEnabled) Publish(av, available, q, publish);
            if (fl != null && fl.EffectiveEnabled) Publish(fl, failure, q, publish);
        }

        private static string Hex(byte[] b)
        {
            return b == null ? "<null>" : string.Join(" ", b.Select(x => x.ToString("X2")));
        }
    }
}
