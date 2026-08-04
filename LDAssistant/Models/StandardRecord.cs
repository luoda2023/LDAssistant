namespace LDAssistant.Models
{
    /// <summary>标准数据库记录</summary>
    public class StandardRecord
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string ImplementDate { get; set; } = "";
        public string DetailUrl { get; set; } = "";
        public string ReplacementRaw { get; set; } = "";
        public string ReplacementParsed { get; set; } = "";
        public string SourceType { get; set; } = "";
    }
}
