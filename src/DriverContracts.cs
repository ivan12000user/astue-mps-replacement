using System;
using System.Threading;

namespace AstueMpsReplacement
{
    public enum ValueQuality
    {
        Good,
        BadCommunication,
        BadOutOfService,
        Uncertain
    }

    public sealed class TagValue
    {
        public object Value { get; set; }
        public ValueQuality Quality { get; set; }
        public DateTime Timestamp { get; set; }

        // Quality-only transitions (communication error / out of service) must not
        // pretend that a new measurement was acquired.
        public bool PreserveValueAndTimestamp { get; set; }
    }

    public interface IProtocolDriver
    {
        string Name { get; }
        void Poll(ConfigNode device, SerialTransport transport, Action<ConfigNode, TagValue> publish, CancellationToken token);
    }
}
