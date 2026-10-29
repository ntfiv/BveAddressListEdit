using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BveAddressListEdit
{
    /// <summary>
    /// 検索で選んだ設定ファイルの場所を覚えておきます。
    /// 保存先: %APPDATA%\BveAddressListEdit\paths.txt
    /// </summary>
    internal static class PathMemory
    {
        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveAddressListEdit", "paths.txt");

        private static string Key(BveVersion v) => v == BveVersion.Bve5 ? "BVE5" : "BVE6";

        public static string Get(BveVersion v)
        {
            Read().TryGetValue(Key(v), out string path);
            return path;
        }

        public static void Set(BveVersion v, string path)
        {
            try
            {
                Dictionary<string, string> all = Read();
                all[Key(v)] = path;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, all.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static Dictionary<string, string> Read()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return result;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) result[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (Exception) { }
            return result;
        }
    }
}
