using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace AstueMpsReplacement
{
    internal static class MeterTemplateCatalog
    {
        private const string ResourceName = "AstueMpsReplacement.device_templates.json";
        private static readonly object Sync = new object();
        private static List<DeviceTemplateInfo> _templates;
        private static string _sourceMppSha256;

        public static string SourceMppSha256
        {
            get { EnsureLoaded(); return _sourceMppSha256 ?? string.Empty; }
        }

        public static List<DeviceTemplateInfo> GetTemplates(string protocolFilter = null)
        {
            EnsureLoaded();
            IEnumerable<DeviceTemplateInfo> q = _templates;
            if (!string.IsNullOrWhiteSpace(protocolFilter))
                q = q.Where(x => string.Equals(x.Protocol, protocolFilter, StringComparison.OrdinalIgnoreCase));
            return q.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static DeviceTemplateInfo Find(string modelId)
        {
            EnsureLoaded();
            return _templates.FirstOrDefault(x => string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        }

        private static void EnsureLoaded()
        {
            if (_templates != null) return;
            lock (Sync)
            {
                if (_templates != null) return;
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                        throw new InvalidOperationException("Не найден встроенный каталог шаблонов счётчиков: " + ResourceName);
                    using (var sr = new StreamReader(stream))
                    {
                        var root = JObject.Parse(sr.ReadToEnd());
                        _sourceMppSha256 = Convert.ToString(root["SourceMppSha256"], CultureInfo.InvariantCulture);
                        var list = new List<DeviceTemplateInfo>();
                        foreach (var item in (JArray)root["Models"])
                        {
                            var node = ReadNode((JObject)item["Template"]);
                            node.RebindParents();
                            list.Add(new DeviceTemplateInfo
                            {
                                ModelId = Convert.ToString(item["ModelId"], CultureInfo.InvariantCulture),
                                DisplayName = Convert.ToString(item["DisplayName"], CultureInfo.InvariantCulture),
                                Protocol = Convert.ToString(item["Protocol"], CultureInfo.InvariantCulture),
                                Template = node,
                                TemplateSource = "Встроенный эталон из исходного MPP",
                                SourcePath = Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture),
                                BranchCount = ToInt(item["Branches"]),
                                TagCount = ToInt(item["Tags"]),
                                ObservedInstances = ToInt(item["ObservedInstances"]),
                                ObservedStructuralVariants = ToInt(item["ObservedStructuralVariants"])
                            });
                        }
                        _templates = list;
                    }
                }
            }
        }

        private static int ToInt(JToken token)
        {
            int v;
            return token != null && int.TryParse(Convert.ToString(token, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static ConfigNode ReadNode(JObject o)
        {
            var n = new ConfigNode
            {
                Id = Guid.NewGuid(),
                Kind = (ConfigNodeKind)ToInt(o["Kind"]),
                Name = Convert.ToString(o["Name"], CultureInfo.InvariantCulture) ?? string.Empty,
                ConfiguredEnabled = o["ConfiguredEnabled"] == null || o["ConfiguredEnabled"].Value<bool>(),
                Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Children = new List<ConfigNode>()
            };
            var props = o["Properties"] as JObject;
            if (props != null)
            {
                foreach (var p in props.Properties())
                    n.Properties[p.Name] = Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            var children = o["Children"] as JArray;
            if (children != null)
            {
                foreach (var c in children.OfType<JObject>())
                {
                    var child = ReadNode(c);
                    child.Parent = n;
                    n.Children.Add(child);
                }
            }
            return n;
        }
    }
}
