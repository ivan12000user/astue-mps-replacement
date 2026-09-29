using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AstueMpsReplacement
{
    public enum ConfigNodeKind
    {
        Main,
        Bus,
        Device,
        SubDevice,
        Group,
        Tag,
        Unknown
    }

    public sealed class ConfigNode
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ConfigNodeKind Kind { get; set; } = ConfigNodeKind.Unknown;
        public string Name { get; set; } = "NewNode";
        public bool ConfiguredEnabled { get; set; } = true;
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<ConfigNode> Children { get; set; } = new List<ConfigNode>();

        [JsonIgnore]
        public ConfigNode Parent { get; set; }

        [JsonIgnore]
        public bool EffectiveEnabled => ConfiguredEnabled && (Parent == null || Parent.EffectiveEnabled);

        [JsonIgnore]
        public string OpcItemId
        {
            get
            {
                var parts = new Stack<string>();
                var p = this;
                while (p != null)
                {
                    if (p.Kind != ConfigNodeKind.Main && !string.IsNullOrWhiteSpace(p.Name))
                        parts.Push(p.Name);
                    p = p.Parent;
                }
                return string.Join(".", parts);
            }
        }

        public void RebindParents(ConfigNode parent = null)
        {
            Parent = parent;
            foreach (var child in Children)
                child.RebindParents(this);
        }

        public void SetProperty(string name, string value)
        {
            Properties[name] = value ?? string.Empty;
            if (name.Equals("NameInTree", StringComparison.OrdinalIgnoreCase))
                Name = value ?? string.Empty;
            else if (name.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                ConfiguredEnabled = ParseBool(value, true);
        }

        public static bool ParseBool(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            if (bool.TryParse(value, out var b)) return b;
            if (value == "1") return true;
            if (value == "0") return false;
            return defaultValue;
        }
    }
}
