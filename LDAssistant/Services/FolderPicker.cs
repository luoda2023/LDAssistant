using System;
using System.Runtime.InteropServices;

namespace LDAssistant.Services
{
    /// <summary>
    /// Windows Vista+ 文件夹选择对话框（无需 WinForms 依赖）
    /// </summary>
    public class FolderPicker
    {
        public string Description { get; set; } = "";
        public string SelectedPath { get; private set; } = "";

        public bool ShowDialog()
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                if (!string.IsNullOrEmpty(Description))
                {
                    dialog.SetTitle(Description);
                }

                // 设置选项：只选文件夹
                dialog.GetOptions(out uint opts);
                dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);

                var hr = dialog.Show(IntPtr.Zero);
                if (hr != 0) return false; // 用户取消

                dialog.GetResult(out var item);
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                SelectedPath = path;
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        // COM 接口定义
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] uint Show(IntPtr parent);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void GetOptions(out uint pfos);
            void SetOptions(uint fos);
            void GetResult(out IShellItem ppsi);
        }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), CoClass(typeof(FileOpenDialogRCW))]
        private interface FileOpenDialog : IFileOpenDialog { }

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void GetDisplayName([In] SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        }

        private const uint FOS_PICKFOLDERS = 0x20;
        private const uint FOS_FORCEFILESYSTEM = 0x40;

        private enum SIGDN : uint
        {
            SIGDN_FILESYSPATH = 0x80058000,
        }
    }
}
