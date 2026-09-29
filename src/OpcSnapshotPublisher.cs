using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AstueMpsReplacement
{
    internal sealed class OpcSnapshotPublisher : IDisposable
    {
        private readonly Func<ConfigNode> _rootProvider;
        private readonly ValueCache _cache;
        private readonly LogService _log;
        private readonly object _sync = new object();
        private Timer _timer;
        private int _busy;
        private string _lastError;

        public static string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ASTUE_MPS");

        public static string SnapshotPath => Path.Combine(DataDirectory, "opc_snapshot.tsv");

        public DateTime LastPublishTime { get; private set; }
        public int LastTagCount { get; private set; }
        public string LastError => _lastError;
        public bool IsRunning
        {
            get { lock (_sync) return _timer != null; }
        }

        public OpcSnapshotPublisher(Func<ConfigNode> rootProvider, ValueCache cache, LogService log)
        {
            _rootProvider = rootProvider;
            _cache = cache;
            _log = log;
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_timer != null) return;
                Directory.CreateDirectory(DataDirectory);
                _timer = new Timer(_ => PublishSafe(), null, 250, 1000);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                var timer = _timer;
                _timer = null;
                if (timer != null) timer.Dispose();
            }
        }

        public void PublishNow() => PublishSafe();

        private void PublishSafe()
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;
            try
            {
                PublishCore();
                _lastError = null;
            }
            catch (Exception ex)
            {
                var message = ex.GetType().Name + ": " + ex.Message;
                if (!string.Equals(_lastError, message, StringComparison.Ordinal))
                    _log?.Warn("OPC snapshot: " + message);
                _lastError = message;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private void PublishCore()
        {
            var root = _rootProvider == null ? null : _rootProvider();
            var tags = root == null ? new ConfigNode[0] : ConfigTree.Tags(root).ToArray();
            var generated = DateTime.UtcNow;
            var generatedFileTime = generated.ToFileTimeUtc();

            Directory.CreateDirectory(DataDirectory);
            var temp = SnapshotPath + ".tmp";

            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write("ASTUE_OPC_SNAPSHOT_V1\t");
                writer.Write(generatedFileTime.ToString(CultureInfo.InvariantCulture));
                writer.Write("\t");
                writer.WriteLine(tags.Length.ToString(CultureInfo.InvariantCulture));

                foreach (var tag in tags)
                {
                    var live = _cache.Get(tag);
                    var quality = ToOpcQuality(tag, live);
                    var timestamp = live == null || live.Timestamp == DateTime.MinValue
                        ? 0L
                        : live.Timestamp.ToUniversalTime().ToFileTimeUtc();
                    var type = NormalizeType(MppSettings.GetString(tag, "Type", string.Empty), live?.Value);
                    var valueText = live == null || live.Value == null ? string.Empty : FormatValue(live.Value, type);

                    writer.Write(ToBase64(tag.OpcItemId));
                    writer.Write('\t');
                    writer.Write(type);
                    writer.Write('\t');
                    writer.Write(quality.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(timestamp.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(ToBase64(valueText));
                    writer.WriteLine();
                }
            }

            if (File.Exists(SnapshotPath))
            {
                try { File.Replace(temp, SnapshotPath, null, true); }
                catch
                {
                    File.Delete(SnapshotPath);
                    File.Move(temp, SnapshotPath);
                }
            }
            else
            {
                File.Move(temp, SnapshotPath);
            }

            LastPublishTime = DateTime.Now;
            LastTagCount = tags.Length;
        }

        private static int ToOpcQuality(ConfigNode tag, TagValue live)
        {
            if (tag == null || !tag.EffectiveEnabled) return 0x1C; // BAD / OUT OF SERVICE
            if (live == null) return 0x20;                         // BAD / WAITING FOR INITIAL DATA
            switch (live.Quality)
            {
                case ValueQuality.Good: return 0xC0;
                case ValueQuality.BadCommunication: return 0x18;
                case ValueQuality.BadOutOfService: return 0x1C;
                case ValueQuality.Uncertain: return 0x40;
                default: return 0x00;
            }
        }

        private static string NormalizeType(string type, object value)
        {
            var s = (type ?? string.Empty).Trim().ToLowerInvariant();
            switch (s)
            {
                case "bool":
                case "int32":
                case "uint32":
                case "float":
                case "double":
                case "string":
                    return s;
            }

            if (value is bool) return "bool";
            if (value is uint || value is ushort || value is byte) return "uint32";
            if (value is int || value is short || value is sbyte) return "int32";
            if (value is float) return "float";
            if (value is double || value is decimal) return "double";
            return "string";
        }

        private static string FormatValue(object value, string type)
        {
            if (value == null) return string.Empty;
            if (type == "bool") return Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "1" : "0";
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string ToBase64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
