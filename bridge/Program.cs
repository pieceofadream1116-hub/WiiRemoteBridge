using System;
using System.Windows.Forms;

namespace WiiRemoteBridge
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.Message, "Wii Remote Bridge");
            Application.Run(new BridgeForm());
        }
    }
}
