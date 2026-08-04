using System.ComponentModel;
using System.Windows.Media;

namespace LDAssistant.Models
{
    /// <summary>页面缩略图项 — 一个文件中的某一页</summary>
    public class PageThumbItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        /// <summary>页码索引（0-based）</summary>
        public int PageIndex { get; set; }

        /// <summary>显示标签，如 "第 1 页"</summary>
        public string Label { get; set; } = "";

        private ImageSource _thumbnail;
        public ImageSource Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; Notify(nameof(Thumbnail)); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; Notify(nameof(IsActive)); }
        }
    }

    /// <summary>文件队列中的项（用于批量处理时记录多个文件）</summary>
    public class FileBatchItem
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = "";
    }
}
