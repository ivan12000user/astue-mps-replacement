using System;
using System.Collections.Concurrent;

namespace AstueMpsReplacement
{
    public sealed class ValueCache
    {
        private readonly ConcurrentDictionary<Guid, TagValue> _values = new ConcurrentDictionary<Guid, TagValue>();

        public event Action<ConfigNode, TagValue> ValueChanged;

        public void Publish(ConfigNode node, TagValue value)
        {
            if (node == null || value == null) return;

            TagValue published = value;
            if (value.PreserveValueAndTimestamp)
            {
                TagValue previous;
                if (_values.TryGetValue(node.Id, out previous) && previous != null)
                {
                    published = new TagValue
                    {
                        Value = previous.Value,
                        Quality = value.Quality,
                        Timestamp = previous.Timestamp
                    };
                }
                else
                {
                    published = new TagValue
                    {
                        Value = null,
                        Quality = value.Quality,
                        Timestamp = DateTime.MinValue
                    };
                }
            }

            _values[node.Id] = published;
            ValueChanged?.Invoke(node, published);
        }

        public void MarkQuality(ConfigNode node, ValueQuality quality, bool preserveValueAndTimestamp, bool notify = true)
        {
            if (node == null) return;

            TagValue previous;
            TagValue next;
            if (preserveValueAndTimestamp && _values.TryGetValue(node.Id, out previous) && previous != null)
            {
                next = new TagValue
                {
                    Value = previous.Value,
                    Quality = quality,
                    Timestamp = previous.Timestamp
                };
            }
            else
            {
                next = new TagValue
                {
                    Value = null,
                    Quality = quality,
                    Timestamp = DateTime.MinValue
                };
            }

            _values[node.Id] = next;
            if (notify) ValueChanged?.Invoke(node, next);
        }

        public TagValue Get(ConfigNode node)
        {
            if (node == null) return null;
            TagValue value;
            return _values.TryGetValue(node.Id, out value) ? value : null;
        }


        public void Remove(ConfigNode node)
        {
            if (node == null) return;
            foreach (var n in ConfigTree.DescendantsAndSelf(node))
            {
                TagValue ignored;
                _values.TryRemove(n.Id, out ignored);
            }
        }

        public void Clear()
        {
            _values.Clear();
        }
    }
}
