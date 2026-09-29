using System;
using System.Linq;

namespace AstueMpsReplacement
{
    internal enum MercuryModelKind
    {
        Unknown,
        M230AR,
        M230ART,
        M230ARTP,
        M230ART2P
    }

    internal sealed class MercuryModelProfile
    {
        public MercuryModelKind Kind { get; private set; }
        public string Id { get; private set; }
        public string DisplayName { get; private set; }

        public bool HasSp { get; private set; }
        public bool HasEnergy { get; private set; }
        public bool HasTariffs { get; private set; }
        public bool HasReverseEnergy { get; private set; }
        public bool HasMonthlyEnergy { get; private set; }
        public bool HasPowerProfile { get; private set; }
        public bool HasEventsLog { get; private set; }
        public bool HasClock { get; private set; }

        public string CapabilitiesText =>
            string.Join(", ", new[]
            {
                HasSp ? "SP" : null,
                HasEnergy ? (HasTariffs ? "Energy T1-T4" : "Energy") : null,
                HasReverseEnergy ? "A-/R-" : null,
                HasMonthlyEnergy ? "12 months" : null,
                HasPowerProfile ? "PowerProfile" : null,
                HasEventsLog ? "EventsLog" : null,
                HasClock ? "Clock" : null
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

        public static MercuryModelProfile Detect(ConfigNode device)
        {
            var id = MppSettings.GetString(device, "NameDevice",
                     MppSettings.GetString(device, "DeviceID", string.Empty)).Trim();

            switch (id.ToUpperInvariant())
            {
                case "M230AR":
                    return New(MercuryModelKind.M230AR, "M230AR", "Mercury 230AR",
                        sp: true, energy: true, tariffs: false, reverse: false, months: false,
                        profile: false, eventsLog: false, clock: false);

                case "M230ART":
                    return New(MercuryModelKind.M230ART, "M230ART", "Mercury 230ART",
                        sp: true, energy: true, tariffs: true, reverse: false, months: false,
                        profile: false, eventsLog: true, clock: true);

                case "M230ARTP":
                    return New(MercuryModelKind.M230ARTP, "M230ARTP", "Mercury 230ART-P",
                        sp: true, energy: true, tariffs: true, reverse: false, months: false,
                        profile: true, eventsLog: true, clock: true);

                case "M230ART2P":
                    return New(MercuryModelKind.M230ART2P, "M230ART2P", "Mercury 230ART2P",
                        sp: true, energy: true, tariffs: true, reverse: true, months: true,
                        profile: true, eventsLog: true, clock: true);

                default:
                    // Для неизвестного шаблона не гадаем по названию:
                    // возможности выводятся из реально импортированного дерева MPS.
                    return InferFromTree(device, id);
            }
        }

        private static MercuryModelProfile InferFromTree(ConfigNode device, string id)
        {
            var tags = ConfigTree.Tags(device).Select(t => t.OpcItemId).ToArray();
            Func<string, bool> contains = marker =>
                tags.Any(x => x.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);

            return New(MercuryModelKind.Unknown,
                string.IsNullOrWhiteSpace(id) ? "UNKNOWN" : id,
                string.IsNullOrWhiteSpace(id) ? "Mercury 230 (unknown template)" : id,
                sp: contains(".SP."),
                energy: contains(".Energy."),
                tariffs: contains(".Energy.T1."),
                reverse: contains(".Energy.T1.A-.") || contains(".Energy.T1.R-."),
                months: contains(".Emonth_1"),
                profile: contains(".PowerProfile."),
                eventsLog: contains(".EventsLog."),
                clock: contains(".IP.CurrentTime"));
        }

        private static MercuryModelProfile New(
            MercuryModelKind kind, string id, string displayName,
            bool sp, bool energy, bool tariffs, bool reverse, bool months,
            bool profile, bool eventsLog, bool clock)
        {
            return new MercuryModelProfile
            {
                Kind = kind,
                Id = id,
                DisplayName = displayName,
                HasSp = sp,
                HasEnergy = energy,
                HasTariffs = tariffs,
                HasReverseEnergy = reverse,
                HasMonthlyEnergy = months,
                HasPowerProfile = profile,
                HasEventsLog = eventsLog,
                HasClock = clock
            };
        }
    }
}
