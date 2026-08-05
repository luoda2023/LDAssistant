using System.ComponentModel;
using System.Windows.Media;

namespace LDAssistant.Models
{
 /// <summary>页面缩略图项</summary>
 public class PageThumbItem : INotifyPropertyChanged
 {
 public event PropertyChangedEventHandler PropertyChanged;
 private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

 /// <summary>页码索引（0-based）</summary>
 public int PageIndex { get; set; }

 /// <summary>显示标签</summary>
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

 /// <summary>批量文件列表项</summary>
 public class FileBatchItem : INotifyPropertyChanged
 {
 public event PropertyChangedEventHandler PropertyChanged;
 private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

 public string FilePath { get; set; } = "";
 public string FileName { get; set; } = "";
 public string FileType { get; set; } = "";

 private bool _isActive;
 public bool IsActive
 {
 get => _isActive;
 set { _isActive = value; Notify(nameof(IsActive)); }
 }

 /// <summary>返回文件类型对应的图标</summary>
 public string Icon => FileType switch
 {
 "pdf" => "📄",
 "docx" => "📝",
 "txt" => "📃",
 "image" => "🖼",
 "cad" => "📐",
 _ => "📄",
 };
 }
}
