using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoBox
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\AutoBox.BackgroundKeyLoop", out created))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (!created)
                {
                    MessageBox.Show("AutoBox 已在运行，请查看任务栏或托盘图标。", "AutoBox");
                    return;
                }
                try { Application.Run(new MainForm()); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
