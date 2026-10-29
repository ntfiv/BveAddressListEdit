using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BveAddressListEdit
{
    /// <summary>
    /// エクスプローラーと同じ見た目のフォルダ選択ダイアログ（複数選択可）。
    /// 使えない環境では従来の FolderBrowserDialog にフォールバックします。
    /// </summary>
    internal static class FolderPicker
    {
        public static List<string> Show(IWin32Window owner, string title, string initialFolder)
        {
            try
            {
                return ShowModern(owner, title, initialFolder);
            }
            catch (COMException)
            {
                return ShowLegacy(owner, title, initialFolder);
            }
            catch (InvalidCastException)
            {
                return ShowLegacy(owner, title, initialFolder);
            }
        }

        private static List<string> ShowLegacy(IWin32Window owner, string title, string initialFolder)
        {
            using (var dlg = new FolderBrowserDialog { Description = title, ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(initialFolder))
                    dlg.SelectedPath = initialFolder;
                return dlg.ShowDialog(owner) == DialogResult.OK
                    ? new List<string> { dlg.SelectedPath }
                    : new List<string>();
            }
        }

        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;
        private const uint FOS_ALLOWMULTISELECT = 0x200;
        private const uint FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        private static List<string> ShowModern(IWin32Window owner, string title, string initialFolder)
        {
            var result = new List<string>();
            var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_ALLOWMULTISELECT | FOS_PATHMUSTEXIST);
                dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder))
                {
                    Guid iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out IShellItem folder) == 0)
                        dialog.SetFolder(folder);
                }

                int hr = dialog.Show(owner?.Handle ?? IntPtr.Zero);
                if (hr == ERROR_CANCELLED)
                    return result;
                Marshal.ThrowExceptionForHR(hr);

                dialog.GetResults(out IShellItemArray items);
                items.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    items.GetItemAt(i, out IShellItem item);
                    item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                    if (!string.IsNullOrEmpty(path))
                        result.Add(path);
                }
                return result;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IShellItemArray ppenum);
            void GetSelectedItems(out IShellItemArray ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            void GetCount(out uint pdwNumItems);
            void GetItemAt(uint dwIndex, out IShellItem ppsi);
            void EnumItems(out IntPtr ppenumShellItems);
        }
    }
}
