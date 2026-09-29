using System;
using System.Collections.Generic;

namespace AstueMpsReplacement
{
    internal static class ConfigTree
    {
        public static IEnumerable<ConfigNode> DescendantsAndSelf(ConfigNode node)
        {
            if (node == null) yield break;
            yield return node;
            foreach (var c in node.Children)
                foreach (var d in DescendantsAndSelf(c))
                    yield return d;
        }

        public static IEnumerable<ConfigNode> Tags(ConfigNode node)
        {
            foreach (var n in DescendantsAndSelf(node))
                if (n.Kind == ConfigNodeKind.Tag) yield return n;
        }

        public static ConfigNode AncestorOrSelf(ConfigNode node, ConfigNodeKind kind)
        {
            var p = node;
            while (p != null)
            {
                if (p.Kind == kind) return p;
                p = p.Parent;
            }
            return null;
        }

        public static ConfigNode FindBySuffix(ConfigNode node, string suffix)
        {
            if (node == null || string.IsNullOrEmpty(suffix)) return null;
            foreach (var n in Tags(node))
                if (n.OpcItemId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }
    }
}
