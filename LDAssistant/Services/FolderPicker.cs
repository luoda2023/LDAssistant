using System;
using System.Runtime.InteropServices;

namespace LDAssistant.Services
{
    /// <summary>
    /// 文件夹选择对话框 — 用 SHBrowseForFolder（传统 Win32 API，稳定不崩溃）
    /// </summary>
    public class FolderPicker
    {
        public string Description { get; set; } = "";
        public string SelectedPath { get; private set; } = "";

        // 最大路径长度
        private const int MAX_PATH = 260;

        [StructLayout(LayoutKind.Sequential)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public int lParam;
            public int iImage;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] out string pszPath);

        [DllImport("shell32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        // BROWSEINFO flags
        private const uint BIF_RETURNONLYFSDIRS = 0x0001;
        private const uint BIF_USENEWUI = 0x0050;
        private const uint BIF_NONEWFOLDERBUTTON = 0x0200;

        public bool ShowDialog()
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = IntPtr.Zero,
                pidlRoot = IntPtr.Zero,
                pszDisplayName = Marshal.AllocCoTaskMem(MAX_PATH * 2),
                lpszTitle = Description,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
            };

            try
            {
                IntPtr pidl = SHBrowseForFolder(ref bi);

                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        bool ok = SHGetPathFromIDList(pidl, out string path);
                        if (ok && !string.IsNullOrEmpty(path))
                        {
                            SelectedPath = path;
                            return true;
                        }
                        return false;
                    }
                    finally
                    {
                        CoTaskMemFree(pidl);
                    }
                }
                return false;
            }
            finally
            {
                if (bi.pszDisplayName != IntPtr.Zero)
                    CoTaskMemFree(bi.pszDisplayName);
            }
        }
    }
}
