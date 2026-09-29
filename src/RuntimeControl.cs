using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AstueMpsReplacement
{
    internal static class RuntimeControl
    {
        public static string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ASTUE_MPS");

        public static string ConfigurationLockPath => Path.Combine(DataDirectory, "configuration_mode.lock");
        public static string MainStatePath => Path.Combine(DataDirectory, "main_state.txt");
        public static string OpcStatePath => Path.Combine(DataDirectory, "opc_server_state.txt");
        public static string OpcDemandPath => Path.Combine(DataDirectory, "opc_demand.touch");
        public static string OpcAutoStartBlockPath => Path.Combine(DataDirectory, "opc_autostart.block");
        public static string OpcManualKeepAlivePath => Path.Combine(DataDirectory, "opc_manual.keepalive");
        public static string PollDemandDisabledPath => Path.Combine(DataDirectory, "poll_on_opc_demand.disabled");
        public static string PollManualHoldPath => Path.Combine(DataDirectory, "poll_manual.hold");
        public static string RuntimeProjectPathFile => Path.Combine(DataDirectory, "runtime_project.path");

        private static int CurrentPid => Process.GetCurrentProcess().Id;

        private static void EnsureDirectory()
        {
            Directory.CreateDirectory(DataDirectory);
        }

        public static void SetConfigurationMode(bool enabled)
        {
            try
            {
                EnsureDirectory();
                if (enabled)
                {
                    File.WriteAllText(ConfigurationLockPath,
                        CurrentPid.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
                }
                else
                {
                    DeleteIfOwnedByCurrentProcess(ConfigurationLockPath);
                }
            }
            catch { }
        }

        public static void ClearOwnedRuntimeFiles()
        {
            try { DeleteIfOwnedByCurrentProcess(ConfigurationLockPath); } catch { }
            try { DeleteIfOwnedByCurrentProcess(MainStatePath, tabSeparated: true); } catch { }
            try { DeleteIfOwnedByCurrentProcess(PollManualHoldPath); } catch { }
        }

        private static void DeleteIfOwnedByCurrentProcess(string path, bool tabSeparated = false)
        {
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path).Trim();
            var first = tabSeparated ? text.Split('\t')[0] : text;
            int pid;
            if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid == CurrentPid)
                File.Delete(path);
        }

        public static void TouchMainState(bool configurationMode, bool polling)
        {
            try
            {
                EnsureDirectory();
                var line = string.Join("\t", new[]
                {
                    CurrentPid.ToString(CultureInfo.InvariantCulture),
                    configurationMode ? "CONFIG" : "WORK",
                    polling ? "POLLING" : "IDLE",
                    DateTime.UtcNow.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture)
                });
                File.WriteAllText(MainStatePath, line, new UTF8Encoding(false));
            }
            catch { }
        }

        public static bool OpcDemandStartEnabled
        {
            get { try { return !File.Exists(OpcAutoStartBlockPath); } catch { return true; } }
        }

        public static void SetOpcDemandStartEnabled(bool enabled)
        {
            try
            {
                EnsureDirectory();
                if (enabled) File.Delete(OpcAutoStartBlockPath);
                else File.WriteAllText(OpcAutoStartBlockPath, "blocked", new UTF8Encoding(false));
            }
            catch { }
        }


        public static void SetOpcManualKeepAlive(bool enabled)
        {
            try
            {
                EnsureDirectory();
                if (enabled) File.WriteAllText(OpcManualKeepAlivePath, "manual", new UTF8Encoding(false));
                else File.Delete(OpcManualKeepAlivePath);
            }
            catch { }
        }

        public static bool PollOnOpcDemandEnabled
        {
            get { try { return !File.Exists(PollDemandDisabledPath); } catch { return true; } }
        }

        public static void SetPollOnOpcDemandEnabled(bool enabled)
        {
            try
            {
                EnsureDirectory();
                if (enabled)
                {
                    File.Delete(PollDemandDisabledPath);
                    File.Delete(PollManualHoldPath);
                }
                else File.WriteAllText(PollDemandDisabledPath, "disabled", new UTF8Encoding(false));
            }
            catch { }
        }

        public static bool PollManualHold
        {
            get
            {
                try
                {
                    if (!File.Exists(PollManualHoldPath)) return false;

                    // Ручной Stop опроса относится только к текущему экземпляру программы.
                    // Старый hold от уже закрытого/аварийно завершившегося процесса не должен
                    // блокировать следующий запуск по OPC demand.
                    var text = File.ReadAllText(PollManualHoldPath).Trim();
                    int ownerPid;
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerPid))
                    {
                        try { File.Delete(PollManualHoldPath); } catch { }
                        return false;
                    }

                    if (ownerPid == CurrentPid) return true;

                    try { File.Delete(PollManualHoldPath); } catch { }
                    return false;
                }
                catch { return false; }
            }
        }

        public static void SetPollManualHold(bool enabled)
        {
            try
            {
                EnsureDirectory();
                if (enabled)
                    File.WriteAllText(PollManualHoldPath,
                        CurrentPid.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
                else
                    File.Delete(PollManualHoldPath);
            }
            catch { }
        }

        public static bool IsOpcDemandFresh(TimeSpan maxAge)
        {
            try
            {
                if (!File.Exists(OpcDemandPath)) return false;
                return DateTime.UtcNow - File.GetLastWriteTimeUtc(OpcDemandPath) <= maxAge;
            }
            catch { return false; }
        }

        public static void SetRuntimeProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                EnsureDirectory();
                File.WriteAllText(RuntimeProjectPathFile, Path.GetFullPath(path), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string GetRuntimeProjectPath()
        {
            try
            {
                if (!File.Exists(RuntimeProjectPathFile)) return null;
                var path = File.ReadAllText(RuntimeProjectPathFile).Trim();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch { return null; }
        }

        public static IDictionary<string, string> ReadOpcState()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(OpcStatePath)) return result;
                foreach (var raw in File.ReadAllLines(OpcStatePath))
                {
                    var line = (raw ?? string.Empty).Trim();
                    var p = line.IndexOf('=');
                    if (p <= 0) continue;
                    result[line.Substring(0, p).Trim()] = line.Substring(p + 1).Trim();
                }
            }
            catch { }
            return result;
        }
    }
}
