using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace BveAddressListEdit
{
    internal sealed class FoundFile
    {
        public string Path;
        public BveVersion Version;
        public int Count;
        public DateTime Modified;
    }

    /// <summary>
    /// PC 内から BVE の設定ファイルを探します。
    /// よく使われる場所を先に調べてから、各ドライブを並行して全体検索します。
    /// </summary>
    internal sealed class FileSearcher
    {
        private static readonly string[] FileNames =
        {
            PreferencesFile.FileName(BveVersion.Bve5),
            PreferencesFile.FileName(BveVersion.Bve6),
        };

        // 中身を見る必要がないフォルダ
        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi",
            "$SysReset", "$Windows.~BT", "$Windows.~WS", "node_modules", ".git",
        };

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003; // ジャンクション
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        private readonly CancellationToken token;
        private readonly Action<FoundFile> onFound;
        private readonly ConcurrentDictionary<string, byte> seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        private long dirsScanned;

        public FileSearcher(CancellationToken token, Action<FoundFile> onFound)
        {
            this.token = token;
            this.onFound = onFound;
        }

        public long DirsScanned => Interlocked.Read(ref dirsScanned);
        public volatile string Phase = "";
        public volatile string CurrentDir = "";

        public void Run()
        {
            Phase = "よく使われる場所を確認中";
            foreach (string dir in LikelyDirectories())
            {
                if (token.IsCancellationRequested) return;
                CurrentDir = dir;
                foreach (string name in FileNames)
                {
                    string path = Path.Combine(dir, name);
                    if (File.Exists(path)) Consider(path);
                }
            }

            var roots = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if ((d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) && d.IsReady)
                        roots.Add(d.RootDirectory.FullName.TrimEnd('\\'));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (roots.Count == 0 || token.IsCancellationRequested) return;

            Phase = "ドライブ全体を検索中（" + string.Join(" ", roots) + "）";
            // ドライブごとに並行して調べる（別々のディスクなら速くなる）
            Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = roots.Count }, ScanTree);
        }

        /// <summary>BVE が設定を置く場所の候補（全ユーザーの ドキュメント\BveTs\Settings など）。</summary>
        private static IEnumerable<string> LikelyDirectories()
        {
            var dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BveTs", "Settings"),
            };
            string usersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            foreach (string user in SafeGetDirectories(usersRoot, "*"))
            {
                dirs.Add(Path.Combine(user, "Documents", "BveTs", "Settings"));
                foreach (string oneDrive in SafeGetDirectories(user, "OneDrive*"))
                {
                    dirs.Add(Path.Combine(oneDrive, "Documents", "BveTs", "Settings"));
                    dirs.Add(Path.Combine(oneDrive, "ドキュメント", "BveTs", "Settings"));
                }
            }
            return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SafeGetDirectories(string dir, string pattern)
        {
            try { return string.IsNullOrEmpty(dir) ? new string[0] : Directory.GetDirectories(dir, pattern); }
            catch (Exception) { return new string[0]; }
        }

        private void ScanTree(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested) return;
                string dir = stack.Pop();
                Interlocked.Increment(ref dirsScanned);
                CurrentDir = dir;

                using (SafeFindHandle h = FindFirstFileExW(@"\\?\" + dir + @"\*", FindExInfoBasic, out WIN32_FIND_DATA data,
                                                           0, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH))
                {
                    if (h.IsInvalid) continue;
                    do
                    {
                        string name = data.cFileName;
                        if (name == "." || name == "..") continue;

                        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            // ジャンクション・シンボリックリンクはたどらない（同じ場所を何度も回るのを防ぐ）。
                            // OneDrive のフォルダも「特殊フォルダ」だが、別の種類なのでたどる。
                            if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
                                (data.dwReserved0 == IO_REPARSE_TAG_MOUNT_POINT || data.dwReserved0 == IO_REPARSE_TAG_SYMLINK))
                                continue;
                            if (SkipNames.Contains(name)) continue;
                            string child = dir + "\\" + name;
                            if (string.Equals(child, windowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                            stack.Push(child);
                        }
                        else if (FileNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            Consider(dir + "\\" + name);
                        }
                    }
                    while (FindNextFileW(h, out data));
                }
            }
        }

        private void Consider(string path)
        {
            if (!seen.TryAdd(path, 0)) return;
            if (!PreferencesFile.TryInspect(path, out BveVersion version, out int count)) return;
            DateTime modified;
            try { modified = File.GetLastWriteTime(path); } catch { modified = DateTime.MinValue; }
            onFound(new FoundFile { Path = path, Version = version, Count = count, Modified = modified });
        }

        // ---- Win32 ----

        private const int FindExInfoBasic = 1;
        private const int FIND_FIRST_EX_LARGE_FETCH = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;   // 特殊フォルダの種類（reparse tag）
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeFindHandle() : base(true) { }
            protected override bool ReleaseHandle() => FindClose(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFindHandle FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATA lpFindFileData,
                                                              int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);
    }
}
