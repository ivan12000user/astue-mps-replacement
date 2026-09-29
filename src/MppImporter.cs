using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AstueMpsReplacement
{
    public static class MppImporter
    {
        private static readonly HashSet<string> ChildMemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node", "device", "subdevice", "group", "tag"
        };

        public static ConfigNode Import(string mppPath, Action<string> log = null)
        {
            log?.Invoke("Открытие MPP: " + mppPath);
            var file1 = SimpleZip.ReadEntry(mppPath, "File1");
            if (file1 == null)
                throw new InvalidDataException("В MPP не найден File1.");

            using (var stream = new MemoryStream(file1, false))
            using (var sr = new StreamReader(stream, Encoding.GetEncoding(1251), false, 1024 * 128))
            using (var jr = new JsonTextReader(sr))
            {
                jr.DateParseHandling = DateParseHandling.None;
                jr.FloatParseHandling = FloatParseHandling.Decimal;
                if (!jr.Read() || jr.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("File1 не начинается с JSON-объекта.");

                var root = ReadNodeObject(jr);
                root.RebindParents();
                if (log != null) log(string.Format("Импорт завершён: линий={0}, устройств={1}, тегов={2}", Count(root, ConfigNodeKind.Bus), Count(root, ConfigNodeKind.Device), Count(root, ConfigNodeKind.Tag)));
                return root;
            }
        }

        private static ConfigNode ReadNodeObject(JsonTextReader jr)
        {
            var node = new ConfigNode();
            while (jr.Read())
            {
                if (jr.TokenType == JsonToken.EndObject)
                    break;
                if (jr.TokenType != JsonToken.PropertyName)
                    continue;

                var propertyName = Convert.ToString(jr.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!jr.Read())
                    throw new EndOfStreamException();

                if (jr.TokenType == JsonToken.StartObject)
                {
                    if (ChildMemberNames.Contains(propertyName))
                    {
                        var child = ReadNodeObject(jr);
                        node.Children.Add(child);
                    }
                    else
                    {
                        jr.Skip();
                    }
                    continue;
                }

                if (jr.TokenType == JsonToken.StartArray)
                {
                    jr.Skip();
                    continue;
                }

                if (jr.TokenType == JsonToken.String || jr.TokenType == JsonToken.Integer ||
                    jr.TokenType == JsonToken.Float || jr.TokenType == JsonToken.Boolean ||
                    jr.TokenType == JsonToken.Null)
                {
                    var value = jr.Value == null ? string.Empty : Convert.ToString(jr.Value, CultureInfo.InvariantCulture);
                    node.Properties[propertyName] = value;
                }
            }

            if (node.Properties.TryGetValue("Category", out var category))
                node.Kind = ParseKind(category);
            if (node.Properties.TryGetValue("NameInTree", out var name))
                node.Name = name;
            if (node.Properties.TryGetValue("Enabled", out var enabled))
                node.ConfiguredEnabled = ConfigNode.ParseBool(enabled, true);
            else if (node.Kind == ConfigNodeKind.Main)
                node.ConfiguredEnabled = true;

            return node;
        }

        private static ConfigNodeKind ParseKind(string category)
        {
            if (Enum.TryParse(category, true, out ConfigNodeKind kind)) return kind;
            if (category != null && category.Equals("Teg", StringComparison.OrdinalIgnoreCase)) return ConfigNodeKind.Tag;
            return ConfigNodeKind.Unknown;
        }

        public static int Count(ConfigNode root, ConfigNodeKind kind)
        {
            var n = root.Kind == kind ? 1 : 0;
            foreach (var child in root.Children) n += Count(child, kind);
            return n;
        }
    }
}
