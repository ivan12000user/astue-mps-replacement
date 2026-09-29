using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AstueMpsReplacement
{
    internal sealed class DeviceTemplateInfo
    {
        public string ModelId { get; set; }
        public string DisplayName { get; set; }
        public ConfigNode Template { get; set; }
        public string Protocol { get; set; }
        public string TemplateSource { get; set; }
        public string SourcePath { get; set; }
        public int BranchCount { get; set; }
        public int TagCount { get; set; }
        public int ObservedInstances { get; set; }
        public int ObservedStructuralVariants { get; set; }

        public override string ToString() => DisplayName;
    }

    internal static class ConfigNodeTools
    {
        public static ConfigNode DeepCloneFresh(ConfigNode source)
        {
            if (source == null) return null;

            var clone = new ConfigNode
            {
                Id = Guid.NewGuid(),
                Kind = source.Kind,
                Name = source.Name,
                ConfiguredEnabled = source.ConfiguredEnabled,
                Properties = new Dictionary<string, string>(source.Properties, StringComparer.OrdinalIgnoreCase),
                Children = new List<ConfigNode>()
            };

            foreach (var child in source.Children)
            {
                var cc = DeepCloneFresh(child);
                cc.Parent = clone;
                clone.Children.Add(cc);
            }

            return clone;
        }

        public static void RefreshNodeIds(ConfigNode node)
        {
            if (node == null) return;
            node.RebindParents(node.Parent);
            foreach (var n in ConfigTree.DescendantsAndSelf(node))
            {
                if (n.Properties.ContainsKey("NodeId") || n.Kind == ConfigNodeKind.Tag)
                    n.Properties["NodeId"] = n.OpcItemId;
                n.Properties["NameInTree"] = n.Name ?? string.Empty;
            }
        }

        public static List<DeviceTemplateInfo> GetDeviceTemplates(ConfigNode root, string protocolFilter = null)
        {
            // Начиная с v0.3.2.0 шаблоны не зависят от того, какие устройства сейчас
            // остались в проекте. Это позволяет удалить последнюю ветку/счётчик модели
            // и затем создать её заново с теми же OPC ItemID.
            return MeterTemplateCatalog.GetTemplates(protocolFilter);
        }

        public static ConfigNode CreateDeviceFromTemplate(
            DeviceTemplateInfo template,
            string name,
            int address,
            int timeoutMs,
            int retries,
            int readingInterval)
        {
            if (template == null || template.Template == null)
                throw new InvalidOperationException("Не выбран шаблон модели устройства.");

            var device = DeepCloneFresh(template.Template);
            device.Name = name;
            device.ConfiguredEnabled = true;
            device.Properties["NameInTree"] = name;
            device.Properties["Enabled"] = "true";
            device.Properties["AnswerTimeOut"] = Math.Max(100, timeoutMs).ToString(CultureInfo.InvariantCulture);
            device.Properties["RepeatCount"] = Math.Max(0, retries).ToString(CultureInfo.InvariantCulture);
            device.Properties["ReadingInterval"] = Math.Max(1, readingInterval).ToString(CultureInfo.InvariantCulture);

            if (!MppSettings.SetDeviceAddress(device, address))
                device.Properties["DeviceAddress"] = address.ToString(CultureInfo.InvariantCulture);

            return device;
        }

        public static int CountKind(ConfigNode node, ConfigNodeKind kind)
        {
            return node == null ? 0 : ConfigTree.DescendantsAndSelf(node).Count(x => x.Kind == kind);
        }
    }
}
