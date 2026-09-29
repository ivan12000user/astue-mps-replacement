using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AstueMpsReplacement
{
    public static class ProjectSerializer
    {
        private const string BinaryEntryName = "project.bin";
        private const string LegacyJsonEntryName = "project.json";
        private const string BinaryMagic = "ASTUEBIN1";

        public static void Save(string path, ConfigNode root)
        {
            if (root == null) throw new ArgumentNullException("root");
            root.RebindParents();

            if (path.EndsWith(".astue", StringComparison.OrdinalIgnoreCase))
            {
                SavePackageBinary(path, root);
                return;
            }

            SaveJson(path, root);
        }

        public static ConfigNode Load(string path)
        {
            ConfigNode root;
            if (path.EndsWith(".astue", StringComparison.OrdinalIgnoreCase))
                root = LoadPackage(path);
            else
                root = LoadJson(path);

            if (root == null) throw new InvalidDataException("Пустой файл проекта.");
            root.RebindParents();
            return root;
        }

        public static string DescribeFormat(string path)
        {
            if (!path.EndsWith(".astue", StringComparison.OrdinalIgnoreCase))
                return "legacy JSON";

            try
            {
                if (SimpleZip.HasEntry(path, BinaryEntryName)) return "ASTUE binary v1";
                if (SimpleZip.HasEntry(path, LegacyJsonEntryName)) return "ASTUE JSON package";
            }
            catch { }
            return "ASTUE package";
        }

        private static void SaveJson(string path, ConfigNode root)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false), 64 * 1024))
            using (var jw = new JsonTextWriter(sw))
            {
                jw.Formatting = Formatting.None;
                CreateSerializer().Serialize(jw, root);
            }
        }

        private static ConfigNode LoadJson(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true, 64 * 1024))
            using (var jr = new JsonTextReader(sr))
            {
                return CreateSerializer().Deserialize<ConfigNode>(jr);
            }
        }

        private static void SavePackageBinary(string path, ConfigNode root)
        {
            byte[] data;
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms, new UTF8Encoding(false)))
                {
                    bw.Write(BinaryMagic);
                    WriteNode(bw, root);
                    bw.Flush();
                    data = ms.ToArray();
                }
            }
            SimpleZip.WriteArchive(path, new[] { new SimpleZip.Entry(BinaryEntryName, data) });
        }

        private static ConfigNode LoadPackage(string path)
        {
            var binary = SimpleZip.ReadEntry(path, BinaryEntryName);
            if (binary != null)
            {
                using (var ms = new MemoryStream(binary, false))
                using (var br = new BinaryReader(ms, Encoding.UTF8))
                {
                    var magic = br.ReadString();
                    if (!string.Equals(magic, BinaryMagic, StringComparison.Ordinal))
                        throw new InvalidDataException("Неизвестная версия бинарного ASTUE-проекта: " + magic);
                    return ReadNode(br, 0);
                }
            }

            var json = SimpleZip.ReadEntry(path, LegacyJsonEntryName);
            if (json == null)
                throw new InvalidDataException("В ASTUE-пакете отсутствуют project.bin и project.json.");

            using (var ms = new MemoryStream(json, false))
            using (var sr = new StreamReader(ms, Encoding.UTF8, true, 64 * 1024))
            using (var jr = new JsonTextReader(sr))
                return CreateSerializer().Deserialize<ConfigNode>(jr);
        }

        private static void WriteNode(BinaryWriter bw, ConfigNode node)
        {
            bw.Write(node.Id.ToByteArray());
            bw.Write((byte)node.Kind);
            bw.Write(node.Name ?? string.Empty);
            bw.Write(node.ConfiguredEnabled);

            var props = node.Properties ?? new Dictionary<string, string>();
            bw.Write(props.Count);
            foreach (var kv in props)
            {
                bw.Write(kv.Key ?? string.Empty);
                bw.Write(kv.Value ?? string.Empty);
            }

            var children = node.Children ?? new List<ConfigNode>();
            bw.Write(children.Count);
            foreach (var child in children)
                WriteNode(bw, child);
        }

        private static ConfigNode ReadNode(BinaryReader br, int depth)
        {
            if (depth > 128)
                throw new InvalidDataException("Слишком большая глубина дерева ASTUE.");

            var idBytes = br.ReadBytes(16);
            if (idBytes.Length != 16)
                throw new EndOfStreamException("Неожиданный конец ASTUE при чтении GUID.");

            var node = new ConfigNode
            {
                Id = new Guid(idBytes),
                Kind = (ConfigNodeKind)br.ReadByte(),
                Name = br.ReadString(),
                ConfiguredEnabled = br.ReadBoolean(),
                Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Children = new List<ConfigNode>()
            };

            var propertyCount = br.ReadInt32();
            if (propertyCount < 0 || propertyCount > 100000)
                throw new InvalidDataException("Некорректное число свойств узла ASTUE: " + propertyCount);

            for (var i = 0; i < propertyCount; i++)
            {
                var key = br.ReadString();
                var value = br.ReadString();
                node.Properties[key] = value;
            }

            var childCount = br.ReadInt32();
            if (childCount < 0 || childCount > 100000)
                throw new InvalidDataException("Некорректное число дочерних узлов ASTUE: " + childCount);

            for (var i = 0; i < childCount; i++)
                node.Children.Add(ReadNode(br, depth + 1));

            return node;
        }

        private static JsonSerializer CreateSerializer()
        {
            return JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include,
                MissingMemberHandling = MissingMemberHandling.Ignore
            });
        }
    }
}
