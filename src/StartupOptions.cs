using System;

namespace AstueMpsReplacement
{
    internal sealed class StartupOptions
    {
        public bool RuntimeMode { get; private set; }
        public bool StartedByOpc { get; private set; }
        public bool ConfigurationMode { get; private set; }
        public bool StartMinimized { get; private set; }

        public static StartupOptions Parse(string[] args)
        {
            var o = new StartupOptions();
            foreach (var raw in args ?? new string[0])
            {
                var a = (raw ?? string.Empty).Trim();
                if (a.Equals("--runtime", StringComparison.OrdinalIgnoreCase)) o.RuntimeMode = true;
                else if (a.Equals("--opc-demand", StringComparison.OrdinalIgnoreCase))
                {
                    o.RuntimeMode = true;
                    o.StartedByOpc = true;
                    // OPC demand is a real runtime start. Keep the GUI visible so the
                    // operator can see that the application was raised by COM/DCOM.
                    o.StartMinimized = false;
                }
                else if (a.Equals("--config", StringComparison.OrdinalIgnoreCase) ||
                         a.Equals("--configuration", StringComparison.OrdinalIgnoreCase))
                {
                    o.ConfigurationMode = true;
                    o.StartMinimized = false;
                }
                else if (a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) o.StartMinimized = true;
            }
            return o;
        }
    }
}
