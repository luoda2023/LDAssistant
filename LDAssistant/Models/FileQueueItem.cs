using System.ComponentModel;
using System.Windows.Media;

namespace LDAssistant.Models
{
    /// <summary>文件队列中的项</summary>
    public class FileQueueItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = ""; // pdf, image, docx, txt, cad

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
}
