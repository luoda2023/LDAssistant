using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LDAssistant.Models;
using ClosedXML.Excel;

namespace LDAssistant.Services
{
    /// <summary>报告导出服务</summary>
    public static class ExportService
    {
        /// <summary>导出 Word 检查报告</summary>
        public static void ExportWord(string outputPath, string fileName, List<CheckResult> results)
        {
            using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();

            // 标题
            body.AppendChild(new Paragraph(
                new Run(new Text($"规范编号检查报告")) { RunProperties = new RunProperties { Bold = new Bold(), FontSize = new FontSize { Val = "36" } } })
            { ParagraphProperties = new ParagraphProperties { Justification = new Justification { Val = JustificationValues.Center } } });

            body.AppendChild(new Paragraph(new Run(new Text($"文件: {fileName}"))));
            body.AppendChild(new Paragraph(new Run(new Text($"检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"))));
            body.AppendChild(new Paragraph(new Run(new Text($"规范编号数量: {results.Count}"))));
            body.AppendChild(new Paragraph(new Run(new Text(""))));

            // 统计
            var valid = results.Count(r => r.Status == "现行");
            var obsolete = results.Count(r => r.Status == "作废" || r.Status == "废止");
            var replaced = results.Count(r => r.Status == "被代替" || r.Status == "被替代");
            var notFound = results.Count(r => r.Status == "未找到" || r.Status == "待检查");

            body.AppendChild(new Paragraph(new Run(new Text($"现行: {valid} | 作废: {obsolete} | 被替代: {replaced} | 未找到: {notFound}"))));
            body.AppendChild(new Paragraph(new Run(new Text(""))));

            // 表格
            var table = new Table();
            var tblPr = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 1 },
                    new BottomBorder { Val = BorderValues.Single, Size = 1 },
                    new LeftBorder { Val = BorderValues.Single, Size = 1 },
                    new RightBorder { Val = BorderValues.Single, Size = 1 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 1 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 1 }
                )
            );
            table.Append(tblPr);

            // 表头
            table.Append(CreateRow("序号", "规范编号", "名称", "状态", "替代信息", "发布单位", true));

            foreach (var r in results)
            {
                table.Append(CreateRow(
                    r.No.ToString(),
                    r.Code,
                    r.Name,
                    r.Status,
                    r.Replacement,
                    r.Publisher));
            }

            body.Append(table);
            mainPart.Document.Append(body);
        }

        private static TableRow CreateRow(params string[] cells)
        {
            return CreateRow(cells, false);
        }

        private static TableRow CreateRow(string[] cells, bool isHeader)
        {
            var row = new TableRow();
            foreach (var text in cells)
            {
                var run = new Run(new Text(text));
                if (isHeader)
                    run.RunProperties = new RunProperties { Bold = new Bold() };
                row.Append(new TableCell(new Paragraph(run)));
            }
            return row;
        }

        /// <summary>导出 Excel 检查报告</summary>
        public static void ExportExcel(string outputPath, string fileName, List<CheckResult> results)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("规范检查报告");

            // 标题行
            ws.Cell(1, 1).Value = "规范编号检查报告";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 16;
            ws.Range(1, 1, 1, 7).Merge();

            ws.Cell(2, 1).Value = $"文件: {fileName}";
            ws.Range(2, 1, 2, 7).Merge();
            ws.Cell(3, 1).Value = $"检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            ws.Range(3, 1, 3, 7).Merge();

            // 表头
            int headerRow = 5;
            var headers = new[] { "序号", "规范编号", "名称", "状态", "替代信息", "发布单位", "来源" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(headerRow, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F5F5F5");
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            // 数据行
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                int row = headerRow + 1 + i;
                ws.Cell(row, 1).Value = r.No;
                ws.Cell(row, 2).Value = r.Code;
                ws.Cell(row, 3).Value = r.Name;
                ws.Cell(row, 4).Value = r.Status;
                ws.Cell(row, 5).Value = r.Replacement;
                ws.Cell(row, 6).Value = r.Publisher;
                ws.Cell(row, 7).Value = r.Source;

                // 状态颜色
                var statusCell = ws.Cell(row, 4);
                statusCell.Style.Font.FontColor = r.Status switch
                {
                    "现行" => XLColor.FromHtml("#4CAF50"),
                    "作废" or "废止" => XLColor.FromHtml("#F44336"),
                    "被代替" or "被替代" => XLColor.FromHtml("#FF9800"),
                    _ => XLColor.FromHtml("#9E9E9E")
                };
                statusCell.Style.Font.Bold = true;
            }

            // 列宽
            ws.Column(1).Width = 8;
            ws.Column(2).Width = 20;
            ws.Column(3).Width = 40;
            ws.Column(4).Width = 12;
            ws.Column(5).Width = 30;
            ws.Column(6).Width = 20;
            ws.Column(7).Width = 15;

            ws.Rows().AdjustToContents();

            wb.SaveAs(outputPath);
        }
    }
}
