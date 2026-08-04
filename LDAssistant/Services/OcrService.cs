using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using LDAssistant.Models;

namespace LDAssistant.Services
{
    /// <summary>OCR 服务 - 调用 PaddleOCR-json.exe</summary>
    public class OcrService
    {
        private readonly string _ocrExe;
        private readonly string _ocrDir;

        public OcrService(string ocrExe, string ocrDir)
        {
            _ocrExe = ocrExe;
            _ocrDir = ocrDir;
        }

        /// <summary>查找 PaddleOCR-json.exe 路径</summary>
        public static (string exe, string dir) FindOcrPath()
        {
            // 1. 应用目录/ocr/PaddleOCR-json.exe
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var bundled = Path.Combine(appDir, "ocr", "PaddleOCR-json.exe");
            if (File.Exists(bundled))
                return (bundled, Path.Combine(appDir, "ocr"));

            // 2. UmiOCR 安装路径
            var umi = Path.Combine(@"D:\Program Files\图片文字识别\UmiOCR-data\plugins\win7_x64_PaddleOCR-json");
            var umiExe = Path.Combine(umi, "PaddleOCR-json.exe");
            if (File.Exists(umiExe))
                return (umiExe, umi);

            // 3. C:\Program Files\...
            var progFiles = Path.Combine(@"C:\Program Files", "图片文字识别", "UmiOCR-data", "plugins", "win7_x64_PaddleOCR-json");
            var progExe = Path.Combine(progFiles, "PaddleOCR-json.exe");
            if (File.Exists(progExe))
                return (progExe, progFiles);

            return (null, null);
        }

        /// <summary>OCR 识别单张图片</summary>
        public OcrResult Recognize(string imagePath)
        {
            if (!File.Exists(_ocrExe))
                return new OcrResult { FullText = "OCR_ERROR: PaddleOCR-json.exe 未找到" };

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ocrExe,
                    Arguments = $"-image_path=\"{imagePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _ocrDir
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return new OcrResult { FullText = "OCR_ERROR: 无法启动 OCR 进程" };

                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30000);

                if (!proc.HasExited)
                {
                    proc.Kill();
                    return new OcrResult { FullText = "OCR_ERROR: OCR 超时" };
                }

                return ParseOutput(stdout);
            }
            catch (Exception ex)
            {
                return new OcrResult { FullText = $"OCR_ERROR: {ex.Message}" };
            }
        }

        /// <summary>解析 PaddleOCR-json 输出</summary>
        private OcrResult ParseOutput(string output)
        {
            var result = new OcrResult();

            // 去除 ANSI 颜色码
            var clean = Regex.Replace(output, @"\x1b\[[0-9;]*m", "");
            var lines = clean.Split('\n');

            JObject json = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("{"))
                {
                    try { json = JObject.Parse(trimmed); break; }
                    catch { }
                }
            }

            if (json == null)
            {
                result.FullText = "OCR_ERROR: 无法解析 OCR 输出";
                return result;
            }

            var data = json["data"] as JArray;
            if (data == null)
            {
                // 可能没有 "data" 字段，检查 "code" 字段判断错误
                var code = json["code"]?.ToString();
                if (code != null && code != "0" && code != "100")
                {
                    result.FullText = $"OCR_ERROR: {json["msg"]}";
                    return result;
                }
                result.FullText = "";
                return result;
            }

            var texts = new List<string>();
            foreach (var item in data)
            {
                var text = item["text"]?.ToString() ?? "";
                var box = item["box"] as JArray;
                if (!string.IsNullOrEmpty(text))
                    texts.Add(text);

                if (box != null && box.Count >= 4)
                {
                    double x1 = 1e9, y1 = 1e9, x2 = -1, y2 = -1;
                    foreach (var pt in box)
                    {
                        double px = pt[0]?.ToObject<double>() ?? 0;
                        double py = pt[1]?.ToObject<double>() ?? 0;
                        if (px < x1) x1 = px;
                        if (px > x2) x2 = px;
                        if (py < y1) y1 = py;
                        if (py > y2) y2 = py;
                    }
                    result.Items.Add(new OcrItem
                    {
                        Text = text,
                        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2
                    });
                }
            }

            result.FullText = string.Join("\n", texts);
            return result;
        }
    }
}
