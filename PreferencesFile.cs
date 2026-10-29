using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BveAddressListEdit
{
    public enum BveVersion
    {
        Bve5,
        Bve6,
    }

    /// <summary>
    /// BVE の設定ファイル (Preferences.xml / BveTs6.Preferences.xml) の
    /// ScenarioForm/Address だけを読み書きします。それ以外の内容と書式はそのまま残します。
    /// </summary>
    public static class PreferencesFile
    {
        public static string DefaultSettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BveTs", "Settings");

        public static string FileName(BveVersion version) =>
            version == BveVersion.Bve5 ? "Preferences.xml" : "BveTs6.Preferences.xml";

        /// <summary>
        /// ファイル名と中身から BVE の設定ファイルかどうかを判定します
        /// （Preferences.xml はほかのソフトでも使われる名前なので中身も確認する）。
        /// </summary>
        public static bool TryInspect(string path, out BveVersion version, out int count)
        {
            version = BveVersion.Bve5;
            count = 0;
            string name = Path.GetFileName(path);
            if (string.Equals(name, FileName(BveVersion.Bve6), StringComparison.OrdinalIgnoreCase))
                version = BveVersion.Bve6;
            else if (!string.Equals(name, FileName(BveVersion.Bve5), StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) return false;
                XElement root = XDocument.Load(path).Root;
                if (root == null || root.Name.LocalName != "Preferences") return false;
                XElement form = root.Element("ScenarioForm");
                if (form == null && root.Element("KeyAssignment") == null) return false;
                count = form?.Element("Address")?.Elements("string").Count() ?? 0;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static List<string> ReadAddresses(string path)
        {
            XDocument doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            XElement address = doc.Root?.Element("ScenarioForm")?.Element("Address");
            if (address == null)
                return new List<string>();
            return address.Elements("string").Select(e => e.Value).ToList();
        }

        public static void WriteAddresses(string path, IList<string> addresses)
        {
            XDocument doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            XElement root = doc.Root ?? throw new InvalidDataException("設定ファイルの中身が空です。");

            XElement form = root.Element("ScenarioForm");
            if (form == null)
            {
                form = new XElement("ScenarioForm");
                AppendIndented(root, form);
            }
            XElement address = form.Element("Address");
            if (address == null)
            {
                address = new XElement("Address");
                AppendIndented(form, address);
            }

            string indent = IndentOf(address);
            address.RemoveNodes();
            foreach (string a in addresses)
                address.Add(new XText("\n" + indent + "  "), new XElement("string", a));
            if (addresses.Count > 0)
                address.Add(new XText("\n" + indent));

            Save(doc, path);
        }

        /// <summary>BVE は終了時に設定を書き戻すので、起動中かどうかを調べます。</summary>
        public static bool IsBveRunning()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.ProcessName.StartsWith("BveTs", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (InvalidOperationException) { }
                finally { p.Dispose(); }
            }
            return false;
        }

        private static void Save(XDocument doc, string path)
        {
            Encoding encoding = new UTF8Encoding(false);
            if (!string.IsNullOrEmpty(doc.Declaration?.Encoding))
            {
                try { encoding = Encoding.GetEncoding(doc.Declaration.Encoding); }
                catch (ArgumentException) { }
                if (encoding is UTF8Encoding)
                    encoding = new UTF8Encoding(false);
            }

            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Encoding = encoding,
                Indent = false,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = "\r\n",
            };

            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                string declaration = doc.Declaration?.ToString() ?? "<?xml version=\"1.0\"?>";
                byte[] head = encoding.GetBytes(declaration + "\r\n");
                fs.Write(head, 0, head.Length);
                using (XmlWriter w = XmlWriter.Create(fs, settings))
                    doc.Root.WriteTo(w);
            }

            // 直前の状態を .bak に残してから差し替える
            string backup = path + ".bak";
            try
            {
                File.Replace(tmp, path, backup);
            }
            catch (IOException)
            {
                File.Copy(path, backup, true);
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
        }

        private static string IndentOf(XElement e)
        {
            if (e.PreviousNode is XText t)
            {
                int i = t.Value.LastIndexOf('\n');
                return i >= 0 ? t.Value.Substring(i + 1) : t.Value;
            }
            return "";
        }

        private static void AppendIndented(XElement parent, XElement child)
        {
            string parentIndent = IndentOf(parent);
            if (parent.LastNode is XText last && string.IsNullOrWhiteSpace(last.Value))
                last.AddBeforeSelf(new XText("\n" + parentIndent + "  "), child);
            else
                parent.Add(new XText("\n" + parentIndent + "  "), child, new XText("\n" + parentIndent));
        }
    }
}
