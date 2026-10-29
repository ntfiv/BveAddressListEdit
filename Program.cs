using System;
using System.Windows.Forms;

namespace BveAddressListEdit
{
    internal static class Program
    {
        /// <summary>
        /// 引数に設定フォルダを渡すと、Documents\BveTs\Settings の代わりにそこを使います。
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string settingsDir = args.Length > 0 ? args[0] : PreferencesFile.DefaultSettingsDir;
            Application.Run(new MainForm(settingsDir));
        }
    }
}
