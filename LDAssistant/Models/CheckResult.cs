using System.ComponentModel;

namespace LDAssistant.Models
{
    /// <summary>规范编号检查结果</summary>
    public class CheckResult : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public int No { get; set; }

        private string _code = "";
        public string Code
        {
            get => _code;
            set { _code = value; Notify(nameof(Code)); }
        }

        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; Notify(nameof(Name)); }
        }

        private string _status = "未检查";
        public string Status
        {
            get => _status;
            set { _status = value; Notify(nameof(Status)); }
        }

        private string _replacement = "";
        public string Replacement
        {
            get => _replacement;
            set { _replacement = value; Notify(nameof(Replacement)); }
        }

        private string _publisher = "";
        public string Publisher
        {
            get => _publisher;
            set { _publisher = value; Notify(nameof(Publisher)); }
        }

        private string _source = "";
        public string Source
        {
            get => _source;
            set { _source = value; Notify(nameof(Source)); }
        }

        /// <summary>状态颜色：现行=绿色, 作废=红色, 被替代=橙色, 未检查=灰色</summary>
        public string StatusColor => Status switch
        {
            "现行" => "#4CAF50",
            "作废" => "#F44336",
            "被代替" => "#FF9800",
            "废止" => "#F44336",
            _ => "#9E9E9E"
        };
    }
}
