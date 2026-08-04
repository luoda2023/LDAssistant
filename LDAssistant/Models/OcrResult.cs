using System.Collections.Generic;

namespace LDAssistant.Models
{
    /// <summary>OCR 单行识别结果</summary>
    public class OcrItem
    {
        public string Text { get; set; } = "";
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    /// <summary>OCR 整页结果</summary>
    public class OcrResult
    {
        public string FullText { get; set; } = "";
        public List<OcrItem> Items { get; set; } = new();
        public bool Success => !FullText.StartsWith("OCR_ERROR");
    }
}
