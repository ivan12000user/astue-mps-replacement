using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AstueMpsReplacement
{
    internal static class Program
    {
        private const string MainMutexName = @"Global\ASTUE_MPS_MAIN_SINGLE_INSTANCE";

        [STAThread]
        private static void Main(string[] args)
        {
            Mutex singleInstance = null;
            bool createdNew = false;
            try
            {
                try
                {
                    singleInstance = new Mutex(true, MainMutexName, out createdNew);
                }
                catch
                {
                    singleInstance = new Mutex(true, @"ASTUE_MPS_MAIN_SINGLE_INSTANCE", out createdNew);
                }

                if (!createdNew)
                {
                    if (!StartupOptions.Parse(args).StartedByOpc)
                    {
                        MessageBox.Show(
                            "ASTUE MPS Replacement уже запущен. Используйте существующее окно программы.",
                            "ASTUE MPS Replacement",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(StartupOptions.Parse(args)));
            }
            catch (Exception ex)
            {
                var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                           Environment.NewLine + ex + Environment.NewLine;
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log"),
                        text);
                }
                catch { }

                MessageBox.Show(
                    text,
                    "ASTUE MPS Replacement — ошибка запуска",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                RuntimeControl.ClearOwnedRuntimeFiles();
                if (singleInstance != null)
                {
                    try { if (createdNew) singleInstance.ReleaseMutex(); } catch { }
                    singleInstance.Dispose();
                }
            }
        }
    }
}
