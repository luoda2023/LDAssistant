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

        /// <summary>来源代码 → 友好名称（用于分类展示与筛选）</summary>
        public static string SourceLabel(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "未知来源";
            return source.ToLowerInvariant() switch
            {
                "csres" or "csres_sort" => "工标网",
                "openstd" or "openstd_dedup" => "全国标准信息公共服务平台",
                "samr" => "国家市场监督管理总局",
                "dbba" => "地方标准信息服务平台",
                "hbba" => "行业标准信息服务平台",
                "std_gov" => "国家标准化管理委员会",
                "std_hangye" => "行业标准",
                "biaozhun" => "标准网",
                "ccsn" => "国家工程建设标准化信息网",
                "zjw" => "住房和城乡建设部",
                "cecs" => "中国工程建设标准化协会",
                _ => source
            };
        }

        /// <summary>状态归一化：作废/废止→作废；被代替/被替代→被替代（用于筛选分组）</summary>
        public static string NormStatus(string status) => status switch
        {
            "作废" or "废止" => "作废",
            "被代替" or "被替代" => "被替代",
            _ => string.IsNullOrEmpty(status) ? "未找到" : status
        };
    }
}
