using System;
using System.Globalization;
using System.IO.Ports;
using Newtonsoft.Json.Linq;

namespace AstueMpsReplacement
{
    internal static class MppSettings
    {
        public static int GetInt(ConfigNode node, string key, int defaultValue)
        {
            string s;
            if (node != null && node.Properties.TryGetValue(key, out s))
            {
                int n;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out n)) return n;
            }
            return defaultValue;
        }

        public static double GetDouble(ConfigNode node, string key, double defaultValue)
        {
            string s;
            if (node != null && node.Properties.TryGetValue(key, out s))
            {
                double n;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return n;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out n)) return n;
            }
            return defaultValue;
        }

        public static string GetString(ConfigNode node, string key, string defaultValue)
        {
            string s;
            return node != null && node.Properties.TryGetValue(key, out s) ? s : defaultValue;
        }

        public static bool GetBool(ConfigNode node, string key, bool defaultValue)
        {
            string s;
            return node != null && node.Properties.TryGetValue(key, out s)
                ? ConfigNode.ParseBool(s, defaultValue)
                : defaultValue;
        }

        public static string GetComName(ConfigNode bus)
        {
            return "COM" + GetInt(bus, "COMPort", 1).ToString(CultureInfo.InvariantCulture);
        }

        public static Parity GetParity(ConfigNode bus)
        {
            var s = GetString(bus, "COMParitet", string.Empty).Trim().ToLowerInvariant();
            if (s.Contains("нечет") || s.Contains("нечёт") || s.Contains("odd")) return Parity.Odd;
            if (s.Contains("чет") || s.Contains("чёт") || s.Contains("even")) return Parity.Even;
            if (s.Contains("mark")) return Parity.Mark;
            if (s.Contains("space")) return Parity.Space;
            return Parity.None;
        }

        public static StopBits GetStopBits(ConfigNode bus)
        {
            return GetInt(bus, "COMStop", 1) == 2 ? StopBits.Two : StopBits.One;
        }

        public static JObject GetDevicePluginRoot(ConfigNode device)
        {
            string json;
            if (device == null || !device.Properties.TryGetValue("PluginProperties", out json) || string.IsNullOrWhiteSpace(json))
                return null;
            try { return JObject.Parse(json); }
            catch { return null; }
        }

        public static JToken GetDevicePropertyToken(ConfigNode device, string propertyName)
        {
            var root = GetDevicePluginRoot(device);
            if (root == null) return null;

            var nameDevice = GetString(device, "NameDevice", null);
            JToken deviceObj = null;
            if (!string.IsNullOrWhiteSpace(nameDevice)) deviceObj = root[nameDevice];
            if (deviceObj == null)
            {
                foreach (var p in root.Properties()) { deviceObj = p.Value; break; }
            }
            if (deviceObj == null) return null;
            return deviceObj["properties"]?[propertyName];
        }

        public static string GetDevicePluginInitial(ConfigNode device, string propertyName, string defaultValue)
        {
            var t = GetDevicePropertyToken(device, propertyName);
            var v = t?["inival"];
            return v == null ? defaultValue : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int GetMercuryAddress(ConfigNode device)
        {
            int n;
            var direct = GetString(device, "DeviceAddress", string.Empty);
            if (int.TryParse(direct, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            var s = GetDevicePluginInitial(device, "Address", "1");
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 1;
        }


        public static bool SetDevicePluginInitial(ConfigNode device, string propertyName, string value)
        {
            var root = GetDevicePluginRoot(device);
            if (root == null) return false;

            var nameDevice = GetString(device, "NameDevice", null);
            JToken deviceObj = null;
            if (!string.IsNullOrWhiteSpace(nameDevice)) deviceObj = root[nameDevice];
            if (deviceObj == null)
            {
                foreach (var p in root.Properties()) { deviceObj = p.Value; break; }
            }
            if (deviceObj == null) return false;

            var properties = deviceObj["properties"] as JObject;
            if (properties == null) return false;

            var property = properties[propertyName] as JObject;
            if (property == null) return false;

            property["inival"] = value ?? string.Empty;
            device.Properties["PluginProperties"] = root.ToString(Newtonsoft.Json.Formatting.None);
            return true;
        }

        public static bool SetMercuryAddress(ConfigNode device, int address)
        {
            if (address < 0 || address > 240 || device == null) return false;
            device.Properties["DeviceAddress"] = address.ToString(CultureInfo.InvariantCulture);
            SetDevicePluginInitial(device, "Address", address.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        public static int GetSet4Address(ConfigNode device)
        {
            int n;
            var overrideText = GetString(device, "DeviceAddress", string.Empty);
            if (int.TryParse(overrideText, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return n;

            var s = GetDevicePluginInitial(device, "Address", "4");
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 4;
        }

        public static string GetSet4Password(ConfigNode device)
        {
            var local = GetString(device, "SET4Password", string.Empty);
            if (!string.IsNullOrWhiteSpace(local)) return Set4Protocol.NormalizePassword(local);

            // Different MPS/SET4 plugin revisions used different property names.
            // Try the common variants, but keep the documented/default ASCII password 000000.
            var names = new[] { "Password", "Psw", "Psw2" };
            foreach (var name in names)
            {
                var value = GetDevicePluginInitial(device, name, string.Empty);
                if (!string.IsNullOrWhiteSpace(value)) return Set4Protocol.NormalizePassword(value);
            }
            return "000000";
        }

        public static bool SetDeviceAddress(ConfigNode device, int address)
        {
            if (address < 0 || address > 255 || device == null) return false;
            device.Properties["DeviceAddress"] = address.ToString(CultureInfo.InvariantCulture);
            SetDevicePluginInitial(device, "Address", address.ToString(CultureInfo.InvariantCulture));
            SetDevicePluginInitial(device, "address", address.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        public static bool GetUseKt(ConfigNode device)
        {
            return ConfigNode.ParseBool(GetString(device, "UseKT", GetDevicePluginInitial(device, "UseKT", "false")), false);
        }

        public static double GetKtu(ConfigNode device)
        {
            double n;
            var s = GetString(device, "KTU", GetDevicePluginInitial(device, "KTU", "1"));
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : 1.0;
        }

        public static double GetKti(ConfigNode device)
        {
            double n;
            var s = GetString(device, "KTI", GetDevicePluginInitial(device, "KTI", "1"));
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : 1.0;
        }

        public static int GetAccessLevel(ConfigNode device)
        {
            int n;
            var s = GetString(device, "PollingAccessLevel", GetDevicePluginInitial(device, "Level", "1"));
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 1;
        }



        public static bool GetTimeCorrection(ConfigNode device)
        {
            return ConfigNode.ParseBool(GetString(device, "TimeCorrection", GetDevicePluginInitial(device, "TimeCorrection", "false")), false);
        }

        public static bool GetUseTimeLocal(ConfigNode device)
        {
            return ConfigNode.ParseBool(GetString(device, "UseTimeLocal", GetDevicePluginInitial(device, "UseTimeLocal", "false")), false);
        }

        public static bool GetUseTimeOffset(ConfigNode device)
        {
            return ConfigNode.ParseBool(GetString(device, "UseTimeOffset", GetDevicePluginInitial(device, "UseTimeOffset", "false")), false);
        }

        public static int GetTimeOffsetHours(ConfigNode device)
        {
            int n;
            var s = GetString(device, "TimeOffset", GetDevicePluginInitial(device, "TimeOffset", "0"));
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        public static int GetPollingAccessLevel(ConfigNode device)
        {
            // If no replacement-specific override exists, preserve the MPS device setting
            // "Level" ("Используемый уровень доступа").
            var fallback = GetAccessLevel(device);
            var n = GetInt(device, "PollingAccessLevel", fallback);
            return n >= 2 ? 2 : 1;
        }

        public static bool GetTimeSyncWriteEnabled(ConfigNode device)
        {
            return GetBool(device, "TimeSyncWriteEnabled", false);
        }

        public static int GetTimeSyncThresholdSeconds(ConfigNode device)
        {
            var n = GetInt(device, "TimeSyncThresholdSec", 5);
            return Math.Max(1, Math.Min(240, n));
        }

        public static int GetEventLogNumber(ConfigNode device)
        {
            var s = GetString(device, "EventLogNumber", "1").Trim();
            int n;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
                    return Math.Max(0, Math.Min(255, n));
            }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return Math.Max(0, Math.Min(255, n));
            return 1;
        }

        public static byte[] GetMercuryPassword(ConfigNode device, int level)
        {
            var key = level >= 2 ? "Psw2" : "Psw1";
            var directKey = level >= 2 ? "MercuryPsw2" : "MercuryPsw1";
            var text = GetString(device, directKey, GetDevicePluginInitial(device, key, level >= 2 ? "{X}222222" : "{X}111111"));
            if (text == null) text = string.Empty;

            var bytes = new byte[6];
            if (text.StartsWith("{X}", StringComparison.OrdinalIgnoreCase))
            {
                var body = text.Substring(3);
                for (var i = 0; i < bytes.Length; i++)
                {
                    var c = i < body.Length ? body[i] : '0';
                    bytes[i] = byte.TryParse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
                        ? b
                        : (byte)0;
                }
                return bytes;
            }

            if (text.StartsWith("{A}", StringComparison.OrdinalIgnoreCase)) text = text.Substring(3);
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = i < text.Length ? (byte)text[i] : (byte)'0';
            return bytes;
        }
    }
}
