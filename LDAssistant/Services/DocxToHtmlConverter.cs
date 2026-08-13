using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ParagraphW = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;
using Break = DocumentFormat.OpenXml.Wordprocessing.Break;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;
using TableRowW = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using TableCellW = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace LDAssistant.Services
{
 /// DOCX → HTML 转换器（快速，不依赖 LibreOffice）
 /// 目标：尽量还原 WORD/WPS 的版面 —— 字体(含东亚)、字号(含 w:szCs)、
 /// 行距/段间距/字间距/首行缩进、表格列宽/边框/底纹/对齐/垂直对齐/行高/合并/文字方向。
 public class DocxToHtmlConverter
 {
 // 页面参数（twips → px：1 inch = 1440 twips = 96px → 1 twip = 1/15 px）
 private const double TwipsPerPx = 1440.0 / 96.0;

		private Dictionary<string, Style> _styleCache;
		private WordprocessingDocument _doc;

		// ── 列表编号 ──
		/// numId → abstractNumId
		private Dictionary<int, int> _numToAbstract;
		/// abstractNumId → (级别 → 级别定义)
		private Dictionary<int, Dictionary<int, NumLevel>> _abstractLevels;
		/// 运行期计数器：(numId, level) → 当前序号
		private Dictionary<(int num, int lvl), int> _numCounters;

		/// 默认制表位宽度(px)，来自 settings.xml 的 w:defaultTabStop
		private double _defaultTabPx = 42.0; // 420 twips ≈ 0.74cm，Word 中文默认 2 字符

		/// 文本框递归深度保护（文本框里可以再嵌文本框）
		private int _txbxDepth = 0;

		/// 列表级别定义
		private class NumLevel
		{
			public string Format = "decimal";   // decimal / bullet / chineseCounting / lowerLetter ...
			public string Text = "%1.";          // lvlText，如 "%1." "第%1章" "•"
			public int Start = 1;
			public double IndentLeftPx = 0;
			public double IndentHangingPx = 0;
			public string Suffix = "tab";        // tab / space / nothing
			public string Font;                  // 项目符号字体
		}

		private void InitStyleCache(WordprocessingDocument doc)
		{
			_doc = doc;
			_styleCache = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
			try
			{
				var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
				if (stylesPart?.Styles == null) return;
				foreach (var style in stylesPart.Styles.Elements<Style>())
				{
					var id = style.StyleId?.Value;
					if (!string.IsNullOrEmpty(id))
						_styleCache[id] = style;
				}
			}
			catch { }
		}

		/// <summary>
		/// 读取 numbering.xml —— 没有这一步，Word 里的“一、二、三”“1.1.1”“•”
		/// 全部会丢失，正文会退化成没有编号的散段，这是版式最刺眼的差异之一。
		/// </summary>
		private void InitNumbering(WordprocessingDocument doc)
		{
			_numToAbstract = new Dictionary<int, int>();
			_abstractLevels = new Dictionary<int, Dictionary<int, NumLevel>>();
			_numCounters = new Dictionary<(int, int), int>();
			try
			{
				var numbering = doc.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
				if (numbering == null) return;

				foreach (var an in numbering.Elements<AbstractNum>())
				{
					int aid = an.AbstractNumberId?.Value ?? -1;
					if (aid < 0) continue;
					var levels = new Dictionary<int, NumLevel>();
					foreach (var lvl in an.Elements<Level>())
					{
						int li = lvl.LevelIndex?.Value ?? 0;
						var nl = new NumLevel
						{
							Format = lvl.NumberingFormat?.Val?.InnerText ?? "decimal",
							Text = lvl.LevelText?.Val?.Value ?? "%1.",
							Start = lvl.StartNumberingValue?.Val?.Value ?? 1,
							Suffix = lvl.LevelSuffix?.Val?.InnerText ?? "tab",
							Font = lvl.NumberingSymbolRunProperties?.RunFonts?.Ascii?.Value
								   ?? lvl.NumberingSymbolRunProperties?.RunFonts?.HighAnsi?.Value
						};
						var ind = lvl.PreviousParagraphProperties?.Indentation;
						if (ind != null)
						{
							if (int.TryParse(ind.Left?.Value, out int l)) nl.IndentLeftPx = TwipsToPx(l);
							if (int.TryParse(ind.Hanging?.Value, out int h)) nl.IndentHangingPx = TwipsToPx(h);
						}
						levels[li] = nl;
					}
					_abstractLevels[aid] = levels;
				}

				foreach (var ni in numbering.Elements<NumberingInstance>())
				{
					int nid = ni.NumberID?.Value ?? -1;
					int aid = ni.AbstractNumId?.Val?.Value ?? -1;
					if (nid >= 0 && aid >= 0) _numToAbstract[nid] = aid;
				}
			}
			catch { }
		}

		private void InitSettings(WordprocessingDocument doc)
		{
			try
			{
				var dts = doc.MainDocumentPart?.DocumentSettingsPart?.Settings
					?.Elements<DefaultTabStop>().FirstOrDefault()?.Val?.Value;
				if (dts != null && dts.Value > 0) _defaultTabPx = TwipsToPx(dts.Value);
			}
			catch { }
		}

		/// <summary>生成某段的编号标签文本（如 "三、" "1.2.1" "•"），无编号返回 null</summary>
		private (string label, NumLevel lvl) BuildNumberLabel(ParagraphW para, ParagraphProperties stylePProps)
		{
			try
			{
				var numPr = para.ParagraphProperties?.NumberingProperties
							?? stylePProps?.NumberingProperties;
				if (numPr == null) return (null, null);
				int numId = numPr.NumberingId?.Val?.Value ?? -1;
				int ilvl = numPr.NumberingLevelReference?.Val?.Value ?? 0;
				if (numId <= 0) return (null, null);
				if (_numToAbstract == null || !_numToAbstract.TryGetValue(numId, out int aid)) return (null, null);
				if (!_abstractLevels.TryGetValue(aid, out var levels)) return (null, null);
				if (!levels.TryGetValue(ilvl, out var lvl)) return (null, null);

				// 递增本级计数，并清空所有更深层级（Word 的编号语义）
				var key = (numId, ilvl);
				_numCounters[key] = _numCounters.TryGetValue(key, out int cur) ? cur + 1 : lvl.Start;
				foreach (var k in _numCounters.Keys.Where(k => k.num == numId && k.lvl > ilvl).ToList())
					_numCounters.Remove(k);

				if (lvl.Format == "bullet")
					return (BulletChar(lvl.Text), lvl);

				// lvlText 里的 %1..%9 替换为对应层级的计数值
				var text = lvl.Text ?? "%1.";
				for (int i = 1; i <= 9; i++)
				{
					var ph = "%" + i;
					if (!text.Contains(ph)) continue;
					int v = _numCounters.TryGetValue((numId, i - 1), out int cv) ? cv
							: (levels.TryGetValue(i - 1, out var dl) ? dl.Start : 1);
					var fmt = (i - 1 == ilvl) ? lvl.Format
							: (levels.TryGetValue(i - 1, out var pl) ? pl.Format : "decimal");
					text = text.Replace(ph, FormatNumber(v, fmt));
				}
				return (System.Net.WebUtility.HtmlEncode(text), lvl);
			}
			catch { return (null, null); }
		}

		private static string BulletChar(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return "•";
			// Wingdings/Symbol 私用区字符映射到通用符号
			char c = raw[0];
			switch (c)
			{
				case '\uF0B7': case '\u00B7': return "•";
				case '\uF0A7': case '\u25AA': return "▪";
				case '\uF06F': case '\u006F': return "○";
				case '\uF0D8': return "➢";
				case '\uF0FC': return "✓";
				default:
					return char.IsControl(c) || c >= '\uF000' ? "•" : System.Net.WebUtility.HtmlEncode(raw);
			}
		}

		private static readonly string[] CnDigits = { "〇", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

		private static string FormatNumber(int n, string fmt)
		{
			switch (fmt)
			{
				case "chineseCounting":
				case "chineseCountingThousand":
				case "chineseLegalSimplified":
					return ToChineseNumber(n);
				case "lowerLetter": return n >= 1 ? ((char)('a' + (n - 1) % 26)).ToString() : "a";
				case "upperLetter": return n >= 1 ? ((char)('A' + (n - 1) % 26)).ToString() : "A";
				case "lowerRoman": return ToRoman(n).ToLower();
				case "upperRoman": return ToRoman(n);
				case "decimalEnclosedCircle":
					return (n >= 1 && n <= 20) ? ((char)('\u2460' + n - 1)).ToString() : n.ToString();
				case "ideographDigital": return ToChineseNumber(n);
				case "none": return "";
				default: return n.ToString();
			}
		}

		private static string ToChineseNumber(int n)
		{
			if (n <= 0) return "〇";
			if (n < 10) return CnDigits[n];
			if (n == 10) return "十";
			if (n < 20) return "十" + CnDigits[n % 10];
			if (n < 100)
			{
				var s = CnDigits[n / 10] + "十";
				if (n % 10 > 0) s += CnDigits[n % 10];
				return s;
			}
			if (n < 1000)
			{
				var s = CnDigits[n / 100] + "百";
				int r = n % 100;
				if (r == 0) return s;
				if (r < 10) return s + "〇" + CnDigits[r];
				return s + ToChineseNumber(r);
			}
			return n.ToString();
		}

		private static string ToRoman(int n)
		{
			if (n <= 0 || n > 3999) return n.ToString();
			int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
			string[] syms = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
			var sb = new StringBuilder();
			for (int i = 0; i < vals.Length; i++)
				while (n >= vals[i]) { sb.Append(syms[i]); n -= vals[i]; }
			return sb.ToString();
		}

		/// <summary>
		/// 读 docDefaults / Normal 的默认字体。Word 中文文档默认多为宋体或等线，
		/// 这里若不读、一律回落到微软雅黑，字宽不同会导致每行断字位置全变，
		/// 是"排版和 Word 不一样"最直接的原因之一。
		/// </summary>
		private static string ReadDocDefaultFontFamily(WordprocessingDocument doc)
		{
			try
			{
				var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
				var docDefaults = stylesPart?.Styles?.Elements<DocDefaults>().FirstOrDefault();
				var rf = docDefaults?.Elements<RunPropertiesDefault>().FirstOrDefault()
						 ?.RunPropertiesBaseStyle?.RunFonts;
				var normal = stylesPart?.Styles?.Elements<Style>()
					?.FirstOrDefault(s => s.StyleId?.Value == "Normal" || s.StyleId?.Value == "正文");
				var nrf = normal?.Elements<RunProperties>().FirstOrDefault()?.RunFonts;

				var list = new List<string>();
				void Add(string f)
				{
					if (string.IsNullOrEmpty(f)) return;
					var q = f.Contains(' ') ? $"'{f}'" : f;
					if (!list.Contains(q)) list.Add(q);
				}
				Add(nrf?.Ascii?.Value); Add(nrf?.EastAsia?.Value);
				Add(rf?.Ascii?.Value); Add(rf?.EastAsia?.Value);
				Add(rf?.HighAnsi?.Value);
				if (list.Count == 0) return null;
				return string.Join(",", list);
			}
			catch { }
			return null;
		}

		/// 表格默认单元格边距（tblCellMar 在样式层，取 Normal Table / docDefaults）
		private static (double top, double right, double bottom, double left) ReadDefaultCellMargin(WordprocessingDocument doc)
		{
			// Word 出厂默认：上下 0，左右 108 twips = 7.2px
			double t = 0, r = 7.2, b = 0, l = 7.2;
			try
			{
				var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
				var tblStyle = stylesPart?.Styles?.Elements<Style>()
					?.FirstOrDefault(s => s.StyleId?.Value == "TableNormal" || s.StyleId?.Value == "普通表格");
				var mar = tblStyle?.StyleTableProperties?.TableCellMarginDefault;
				if (mar != null)
				{
					if (int.TryParse(mar.TopMargin?.Width?.Value, out int tv)) t = TwipsToPx(tv);
					if (int.TryParse(mar.BottomMargin?.Width?.Value, out int bv)) b = TwipsToPx(bv);
					var rm = mar.TableCellRightMargin;
					if (rm?.Width != null) r = TwipsToPx(rm.Width.Value);
					var lm = mar.TableCellLeftMargin;
					if (lm?.Width != null) l = TwipsToPx(lm.Width.Value);
				}
			}
			catch { }
			return (t, r, b, l);
		}

 private static string ReadDocDefaultFontSize(WordprocessingDocument doc)
 {
 try
 {
 var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
 var docDefaults = stylesPart?.Styles?.Elements<DocDefaults>().FirstOrDefault();
 var rPrDefault = docDefaults?.Elements<RunPropertiesDefault>().FirstOrDefault();
 var sz = rPrDefault?.RunPropertiesBaseStyle?.FontSize?.Val?.Value;
 if (!string.IsNullOrEmpty(sz) && int.TryParse(sz, out int szVal))
 return $"{szVal / 2.0:F1}pt";
 var normalStyle = stylesPart?.Styles?.Elements<Style>()
 ?.FirstOrDefault(s => s.StyleId?.Value == "Normal" || s.StyleId?.Value == "正文");
 var normalSz = normalStyle?.Elements<RunProperties>().FirstOrDefault()?.FontSize?.Val?.Value;
 if (!string.IsNullOrEmpty(normalSz) && int.TryParse(normalSz, out int nszVal))
 return $"{nszVal / 2.0:F1}pt";
 }
 catch { }
 return null;
 }

 private Style FindStyle(string styleId)
 {
 if (string.IsNullOrEmpty(styleId) || _styleCache == null) return null;
 if (_styleCache.TryGetValue(styleId, out var s)) return s;
 return null;
 }

 private RunProperties ResolveStyleRunProps(string styleId, HashSet<string> visited = null)
 {
 if (visited == null) visited = new HashSet<string>();
 if (string.IsNullOrEmpty(styleId) || visited.Contains(styleId)) return null;
 visited.Add(styleId);

 var style = FindStyle(styleId);
 if (style == null) return null;

 RunProperties result = null;
 var basedOn = style.Elements<BasedOn>().FirstOrDefault()?.Val?.Value;
 if (!string.IsNullOrEmpty(basedOn))
 result = ResolveStyleRunProps(basedOn, visited);

 var rp = style.Elements<RunProperties>().FirstOrDefault();
 if (rp != null)
 {
 if (result == null)
 result = (RunProperties)rp.CloneNode(true);
 else
 {
 foreach (var child in rp.ChildElements)
 {
 var existing = result.Elements<OpenXmlElement>().FirstOrDefault(e => e.LocalName == child.LocalName);
 if (existing != null)
 result.RemoveChild(existing);
 result.AppendChild(child.CloneNode(true));
 }
 }
 }
 return result;
 }

 private ParagraphProperties ResolveStyleParaProps(string styleId, HashSet<string> visited = null)
 {
 if (visited == null) visited = new HashSet<string>();
 if (string.IsNullOrEmpty(styleId) || visited.Contains(styleId)) return null;
 visited.Add(styleId);

 var style = FindStyle(styleId);
 if (style == null) return null;

 ParagraphProperties result = null;
 var basedOn = style.Elements<BasedOn>().FirstOrDefault()?.Val?.Value;
 if (!string.IsNullOrEmpty(basedOn))
 result = ResolveStyleParaProps(basedOn, visited);

 var pp = style.Elements<ParagraphProperties>().FirstOrDefault();
 if (pp != null)
 {
 if (result == null)
 result = (ParagraphProperties)pp.CloneNode(true);
 else
 {
 foreach (var child in pp.ChildElements)
 {
 var existing = result.Elements<OpenXmlElement>().FirstOrDefault(e => e.LocalName == child.LocalName);
 if (existing != null)
 result.RemoveChild(existing);
 result.AppendChild(child.CloneNode(true));
 }
 }
 }
 return result;
 }

 private static double TwipsToPx(int twips) => twips / TwipsPerPx;

 public static string Convert(string docxPath)
 {
			var converter = new DocxToHtmlConverter();
			using var doc = WordprocessingDocument.Open(docxPath, false);
			converter.InitStyleCache(doc);
			converter.InitNumbering(doc);
			converter.InitSettings(doc);
			return converter.ConvertInternal(doc);
 }

 private string ConvertInternal(WordprocessingDocument doc)
 {
 var body = doc.MainDocumentPart?.Document?.Body;
 if (body == null) return "<p>文档为空</p>";

 var defaultSectPr = body.Elements<SectionProperties>().LastOrDefault();
 var defaultLayout = ParseSectionProperties(defaultSectPr);
 string defaultFontSize = ReadDocDefaultFontSize(doc) ?? "10.5pt";
 double defaultFsPt = ParsePt(defaultFontSize, 10.5);
 // 文档自身的默认字体优先，回落链保证缺字时不乱码
 string docFont = ReadDocDefaultFontFamily(doc);
 string fontStack = string.IsNullOrEmpty(docFont)
	 ? "'SimSun','宋体','Microsoft YaHei','微软雅黑','SimHei','黑体',serif"
	 : docFont + ",'SimSun','宋体','Microsoft YaHei','微软雅黑',serif";
 var cellMar = ReadDefaultCellMargin(doc);

 var sb = new StringBuilder();
 sb.Append("<!DOCTYPE html><html lang='zh-CN'><head><meta charset='UTF-8'>");
 sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
 sb.Append("<style>");
 sb.Append("*{margin:0;padding:0;box-sizing:border-box;}");
 sb.Append("html,body{background:#ECEFF1;}");
 sb.Append($"body{{font-family:{fontStack};padding:20px 0;}}");
 // 中英文混排间距交给显式空格控制，避免浏览器自动加空隙造成与 Word 行长不一致
 sb.Append("body{text-spacing-trim:normal;}");
 // 默认页面样式
 sb.Append($".page{{width:{defaultLayout.PageWidthPx}px;height:{defaultLayout.PageHeightPx}px;margin:0 auto 20px;background:#fff;");
 sb.Append($"padding:{defaultLayout.MarginTopPx}px {defaultLayout.MarginRightPx}px {defaultLayout.MarginBottomPx}px {defaultLayout.MarginLeftPx}px;");
 sb.Append("box-shadow:0 2px 8px rgba(0,0,0,0.15);position:relative;overflow:hidden;page-break-after:always;}");
 sb.Append(".page:last-child{page-break-after:auto;margin-bottom:0;}");
 if (defaultLayout.ColumnCount > 1)
 {
 sb.Append($".page{{column-count:{defaultLayout.ColumnCount};column-gap:{defaultLayout.ColumnGapPx}px;}}");
 sb.Append("h1,h2,h3,h4,h5,h6{column-span:all;}");
 sb.Append("table{column-span:all;}");
 }
 // 标题
 sb.Append("h1{font-size:22pt;text-align:center;margin:14pt 0;font-weight:bold;line-height:1.3;}");
 sb.Append("h2{font-size:16pt;margin:12pt 0;font-weight:bold;line-height:1.3;}");
 sb.Append("h3{font-size:14pt;margin:10pt 0;font-weight:bold;line-height:1.3;}");
 sb.Append("h4{font-size:12pt;margin:8pt 0;font-weight:bold;line-height:1.3;}");
 sb.Append("h5,h6{font-size:10.5pt;margin:6pt 0;font-weight:bold;line-height:1.3;}");
 // 段落默认
 // Word 的默认段落对齐是左对齐，强制 justify 会把每行拉伸，字距和原文完全不同
 sb.Append("p{font-size:" + defaultFontSize + ";line-height:1.5;color:#000;text-align:left;}");
 sb.Append("div.cellp{font-size:" + defaultFontSize + ";line-height:1.5;color:#000;}");
 // 中文按字断行、西文按单词断行，和 Word 的换行规则一致
 sb.Append("p,div.cellp,td,th{word-wrap:break-word;overflow-wrap:break-word;line-break:strict;}");
 // 表格包装
 sb.Append(".docx-table-wrap{}");
 sb.Append("table.docx-table{border-collapse:collapse;font-size:" + defaultFontSize + ";}");
 sb.Append($"table.docx-table td,table.docx-table th{{padding:{cellMar.top:F1}px {cellMar.right:F1}px {cellMar.bottom:F1}px {cellMar.left:F1}px;vertical-align:top;}}");
 sb.Append("tr.tbl-header-row{font-weight:bold;}");
 sb.Append(".center{text-align:center;}");
 sb.Append(".right{text-align:right;}");
 sb.Append(".justify{text-align:justify;}");
 sb.Append(".bold{font-weight:bold;}");
 sb.Append(".underline{text-decoration:underline;}");
 sb.Append("hr{border:none;border-top:1px solid #999;margin:10pt 0;}");
 sb.Append(".page-break{page-break-after:always;break-after:page;}");
 sb.Append("img{max-width:100%;}");
 // 页眉/页脚：定位在页面的上下页边距区域内，和 Word 一致
 sb.Append($".pg-hdr,.pg-ftr{{position:absolute;left:{defaultLayout.MarginLeftPx}px;right:{defaultLayout.MarginRightPx}px;");
 sb.Append("font-size:9pt;line-height:1.4;color:#000;overflow:hidden;}");
 sb.Append($".pg-hdr{{top:{defaultLayout.HeaderPx}px;}}");
 sb.Append($".pg-ftr{{bottom:{defaultLayout.FooterPx}px;}}");
 sb.Append(".pg-hdr p,.pg-ftr p{font-size:9pt;line-height:1.4;margin:0;}");
 sb.Append("</style></head><body>");

 // 页眉/页脚模板：JS 分页后逐页克隆注入（页数只有分页完才知道）
 var hdrHtml = ExtractHeaderFooterHtml(doc, defaultSectPr, true);
 var ftrHtml = ExtractHeaderFooterHtml(doc, defaultSectPr, false);
 if (!string.IsNullOrEmpty(hdrHtml))
	 sb.Append($"<template id='__hdr'>{hdrHtml}</template>");
 if (!string.IsNullOrEmpty(ftrHtml))
	 sb.Append($"<template id='__ftr'>{ftrHtml}</template>");

 // 按分页符/分节符切成多个 .page（seed pages），剩余溢出由 JS 再精确分页
 var pages = new List<(string content, string pageClass)>();
 var pageContent = new StringBuilder();
 string currentPageClass = "";
 int sectionIdx = 0;

 foreach (var element in body.ChildElements)
 {
 if (element is SectionProperties) continue;

 bool hasPageBreak = false;
 bool hasSectionBreak = false;
 string html = "";

 if (element is ParagraphW para)
 {
 foreach (var run in para.Elements<Run>())
 foreach (var child in run.ChildElements)
 if (child is Break br && br.Type?.Value == BreakValues.Page)
 hasPageBreak = true;

 var embeddedSectPr = para.Elements<ParagraphProperties>().FirstOrDefault()?.Elements<SectionProperties>().FirstOrDefault();
 if (embeddedSectPr != null) hasSectionBreak = true;
 html = ConvertParagraph(para, doc);
 }
 else if (element is Table table)
 {
 html = ConvertTable(table, doc);
 }

 pageContent.Append(html);

 if (hasPageBreak || hasSectionBreak)
 {
 if (pageContent.Length > 0)
 {
 pages.Add((pageContent.ToString(), currentPageClass));
 pageContent.Clear();
 }
 if (hasSectionBreak && element is ParagraphW sectPara)
 {
 var secLayout = ParseSectionProperties(
 sectPara.Elements<ParagraphProperties>().FirstOrDefault()
 ?.Elements<SectionProperties>().FirstOrDefault());
 sectionIdx++;
 var sec = $"sec{sectionIdx}";
 sb.Append($"<style>.{sec}{{width:{secLayout.PageWidthPx}px !important;height:{secLayout.PageHeightPx}px !important;");
 sb.Append($"padding:{secLayout.MarginTopPx}px {secLayout.MarginRightPx}px {secLayout.MarginBottomPx}px {secLayout.MarginLeftPx}px !important;");
 if (secLayout.ColumnCount > 1)
 sb.Append($"column-count:{secLayout.ColumnCount};column-gap:{secLayout.ColumnGapPx}px;");
 sb.Append("}}</style>");
 currentPageClass = sec;
 }
 }
 }

 if (pageContent.Length > 0)
 pages.Add((pageContent.ToString(), currentPageClass));

 if (pages.Count == 0)
 sb.Append("<div class='page'><p>文档为空</p></div>");
 else
 foreach (var (content, pageClass) in pages)
 {
 var classAttr = string.IsNullOrEmpty(pageClass) ? "page" : $"page {pageClass}";
 sb.Append($"<div class='{classAttr}'>{content}</div>");
 }

 // 注入 JS 分页（健壮版：按高度把溢出块移到下一页，绝不裁切内容）
 var contentH = defaultLayout.PageHeightPx - defaultLayout.MarginTopPx - defaultLayout.MarginBottomPx;
 var colCount = defaultLayout.ColumnCount;
 sb.Append("<script>");
 sb.Append("(function(){");
 sb.Append($"var MAXH={contentH:F0};var COLS={colCount};");
            sb.Append(@"
function px(el,prop){var v=parseFloat(getComputedStyle(el)[prop]);return isNaN(v)?0:v;}
function blockH(el){return px(el,'marginTop')+el.offsetHeight+px(el,'marginBottom');}
function availOf(page){return page.clientHeight-px(page,'paddingTop')-px(page,'paddingBottom');}
function kidsOf(page){
  return [].slice.call(page.children).filter(function(c){
    return getComputedStyle(c).position!=='absolute'&&c.tagName!=='STYLE'&&c.tagName!=='SCRIPT';
  });
}
function headerRowsOf(table){
  return [].slice.call(table.querySelectorAll('tr.tbl-header-row'));
}
function cloneTableShell(orig){
  var t=document.createElement('table');
  t.className=orig.className;
  if(orig.style&&orig.style.cssText)t.style.cssText=orig.style.cssText;
  var cg=orig.querySelector('colgroup');
  if(cg)t.appendChild(cg.cloneNode(true));
  t.appendChild(document.createElement('tbody'));
  return t;
}
/* 取 [from,to] 行构成新表；withHeader=true 时在前面补重复标题行（Word 的跨页表头） */
function sliceTable(orig,from,to,withHeader){
  var t=cloneTableShell(orig);
  var tb=t.querySelector('tbody');
  if(withHeader){
    var hdrs=headerRowsOf(orig);
    for(var h=0;h<hdrs.length;h++){
      /* 标题行本身若落在保留区间内，避免重复插入 */
      if(hdrs[h].rowIndex>=from&&hdrs[h].rowIndex<=to) continue;
      tb.appendChild(hdrs[h].cloneNode(true));
    }
  }
  var rows=orig.rows;
  for(var i=from;i<=to&&i<rows.length;i++) tb.appendChild(rows[i].cloneNode(true));
  return t;
}
/* 在剩余高度 remain 内最多能放下多少行（不含跨页重复表头的开销） */
function rowsFitting(table,remain){
  var acc=0,keep=0;
  for(var r=0;r<table.rows.length;r++){
    var h=table.rows[r].offsetHeight;
    if(acc+h>remain) break;
    acc+=h;keep=r+1;
  }
  return keep;
}
function newPageAfter(refPage){
  var np=document.createElement('div');
  np.className=refPage.className;
  refPage.parentNode.insertBefore(np,refPage.nextSibling);
  return np;
}
/*
 * 把一个 seed page 的内容按可用高度流式排布到若干页。
 * 关键点（旧版的坑）：
 *  1. 每次搬运后必须重新测量新页，不能只处理一轮
 *  2. 表格拆分要按“当前页剩余高度”，不是整页高度
 *  3. 拆表后剩余兄弟元素必须一起搬到新页，否则内容会黏在旧页
 */
function flowPage(startPage){
  var page=startPage,guard=0;
  while(guard++<600){
    var avail=availOf(page);
    if(avail<=0) return;
    var kids=kidsOf(page);
    if(!kids.length) return;

    var y=0,cut=-1;
    for(var i=0;i<kids.length;i++){
      var ch=blockH(kids[i]);
      if(y+ch>avail){
        /* 首块就超高且无法拆分 → 只能让它独占本页 */
        if(y===0&&!(kids[i].tagName==='TABLE'&&kids[i].rows.length>1)){ y+=ch; continue; }
        cut=i;break;
      }
      y+=ch;
    }
    if(cut<0) return;   /* 本页装得下，收工 */

    var k=kids[cut];
    var np=newPageAfter(page);

    /* 表格：优先按行拆，让上半部分填满本页 */
    if(k.tagName==='TABLE'&&k.rows&&k.rows.length>1){
      var remain=avail-y;
      var keep=rowsFitting(k,remain);
      var hdrCount=headerRowsOf(k).length;
      /* 只装得下表头就不值得拆，整张表挪到下一页 */
      if(keep>hdrCount&&keep<k.rows.length){
        var t1=sliceTable(k,0,keep-1,false);
        var t2=sliceTable(k,keep,k.rows.length-1,true);
        k.parentNode.insertBefore(t1,k);
        k.parentNode.removeChild(k);
        np.appendChild(t2);
        for(var j=cut+1;j<kids.length;j++) np.appendChild(kids[j]);
        page=np;continue;
      }
    }
    /* 非表格 / 无法拆分：整块连同后续兄弟一起搬到新页 */
    for(var j=cut;j<kids.length;j++) np.appendChild(kids[j]);
    page=np;
  }
}
function paginate(){
  var seeds=[].slice.call(document.querySelectorAll('.page'));
  for(var s=0;s<seeds.length;s++) flowPage(seeds[s]);
  /* 清理：分页后可能产生完全空白的尾页 */
  var all=[].slice.call(document.querySelectorAll('.page'));
  for(var p=all.length-1;p>=0;p--){
    if(all.length<=1) break;
    if(!all[p].textContent.trim()&&!all[p].querySelector('img,table,hr')){
      all[p].parentNode.removeChild(all[p]);
      all.splice(p,1);
    }
  }
  /* 安全网：单块超高（超大图/不可拆表）时放开高度，宁可长一点也不裁切内容 */
  all=[].slice.call(document.querySelectorAll('.page'));
  for(var p2=0;p2<all.length;p2++){
    if(all[p2].scrollHeight>all[p2].clientHeight+5){
      all[p2].style.height='auto';
      all[p2].style.overflow='visible';
    }
  }
  /* 逐页注入页眉/页脚。必须在分页之后做，否则会被当成正文参与高度计算 */
  var hdrTpl=document.getElementById('__hdr');
  var ftrTpl=document.getElementById('__ftr');
  if(hdrTpl||ftrTpl){
    for(var q=0;q<all.length;q++){
      if(hdrTpl&&!all[q].querySelector(':scope>.pg-hdr')){
        var hd=document.createElement('div');hd.className='pg-hdr';
        hd.appendChild(hdrTpl.content.cloneNode(true));
        all[q].appendChild(hd);
      }
      if(ftrTpl&&!all[q].querySelector(':scope>.pg-ftr')){
        var fd=document.createElement('div');fd.className='pg-ftr';
        fd.appendChild(ftrTpl.content.cloneNode(true));
        all[q].appendChild(fd);
      }
    }
  }
  /* 填充 PAGE / NUMPAGES 域（Word 页码域），分页完成后才知道真实页号 */
  for(var p3=0;p3<all.length;p3++){
    var nums=all[p3].querySelectorAll('.pgnum');
    for(var n=0;n<nums.length;n++) nums[n].textContent=String(p3+1);
    var tot=all[p3].querySelectorAll('.pgtot');
    for(var m=0;m<tot.length;m++) tot[m].textContent=String(all.length);
  }
  /* 滚动性能：视口外的页不做布局/绘制，长文档滚动从卡顿变顺滑。
     contain-intrinsic-size 用页面真实高度做占位，避免滚动条跳动。 */
  for(var p4=0;p4<all.length;p4++){
    /* 写入页号属性：页数/页范围识别（GetPageRectAsync/GetPdfPageCountAsync）依赖 data-page-number */
    all[p4].setAttribute('data-page-number', String(p4+1));
    var ph=all[p4].offsetHeight;
    all[p4].style.containIntrinsicSize=all[p4].offsetWidth+'px '+ph+'px';
    all[p4].style.contentVisibility='auto';
  }
  document.body.setAttribute('data-paged','1');
  document.body.setAttribute('data-pagecount',String(all.length));
}
/* 截图/打印前临时放开 content-visibility，保证离屏页也能被完整渲染 */
window.__cvOff=function(){
  var all=document.querySelectorAll('.page');
  for(var i=0;i<all.length;i++) all[i].style.contentVisibility='visible';
  return all.length;
};
window.__cvOn=function(){
  var all=document.querySelectorAll('.page');
  for(var i=0;i<all.length;i++) all[i].style.contentVisibility='auto';
  return all.length;
};
function doPagination(){
  try{ paginate(); }catch(e){ document.body.setAttribute('data-paged','1'); }
}
/* 等字体真正就绪再分页，否则测高不准会导致缩略图与正文不一致 */
function whenReady(fn){
  if(document.fonts&&document.fonts.ready) document.fonts.ready.then(function(){setTimeout(fn,80);});
  else setTimeout(fn,300);
}
if(document.readyState==='complete') whenReady(doPagination);
else window.addEventListener('load',function(){whenReady(doPagination);});
");
 sb.Append("})();");
 sb.Append("</script>");

 sb.Append("</body></html>");
 return sb.ToString();
 }

 // ═════════════ 段落 ═════════════
 private string ConvertParagraph(ParagraphW para, WordprocessingDocument doc, string wrapper = "p")
 {
 var sb = new StringBuilder();
 var pProps = para.ParagraphProperties;
 var style = pProps?.ParagraphStyleId?.Val?.Value ?? "";
 var justify = pProps?.Justification?.Val ?? JustificationValues.Left;
 var stylePProps = ResolveStyleParaProps(style);

 bool isHeading = style.IndexOf("Heading", StringComparison.OrdinalIgnoreCase) >= 0 ||
 style.IndexOf("标题", StringComparison.OrdinalIgnoreCase) >= 0;
 int headingLevel = 0;
 if (isHeading)
 {
 var match = Regex.Match(style, @"\d+");
 if (match.Success) headingLevel = int.Parse(match.Value);
 else headingLevel = 1;
 }

 bool hasHr = false;
 foreach (var run in para.Elements<Run>())
 foreach (var child in run.ChildElements)
 if (child.LocalName == "pict" || child.LocalName == "drawing")
 {
 var xml = child.OuterXml;
 if (xml.Contains("o:hr=") || xml.Contains("hr=\"t\"")) hasHr = true;
 }

 if (hasHr) sb.Append("<hr/>");

			var content = new StringBuilder();
			BuildInlineContent(para, style, content);

			// ── 列表编号：必须在正文之前算，且空段也要占号位（与 Word 一致）──
			var (numLabel, numLvl) = BuildNumberLabel(para, stylePProps);

			if (content.Length == 0 && !hasHr && numLabel == null)
				return "<" + wrapper + ">&nbsp;</" + wrapper + ">";
			if (hasHr && content.Length == 0 && numLabel == null) return sb.ToString();

			if (numLabel != null)
			{
				// Word 的悬挂缩进：编号占 hanging 宽度，正文从 left 位置起排
				double gap = numLvl.IndentHangingPx > 0 ? numLvl.IndentHangingPx : _defaultTabPx;
				var fontCss = string.IsNullOrEmpty(numLvl.Font) ? "" : $"font-family:'{numLvl.Font}';";
				content.Insert(0,
					$"<span style=\"display:inline-block;min-width:{gap:F1}px;{fontCss}\">{numLabel}</span>");
			}

			var pStyle = BuildParagraphStyle(pProps, stylePProps, numLvl);
 var pStyleAttr = string.IsNullOrEmpty(pStyle) ? "" : $" style='{pStyle}'";
 var align = GetAlign(justify);
 var alignClass = string.IsNullOrEmpty(align) ? "" : $" class='{align}'";

 if (headingLevel > 0 && wrapper == "p")
 {
 var tag = headingLevel <= 6 ? $"h{headingLevel}" : "h6";
 sb.Append($"<{tag}{alignClass}{pStyleAttr}>{content}</{tag}>");
 }
 else
 {
 sb.Append($"<{wrapper}{alignClass}{pStyleAttr}>{content}</{wrapper}>");
 }
 return sb.ToString();
 }

		/// <summary>
		/// 按文档顺序展开段落内联内容（Run / 超链接 / 域）。
		/// 旧版先遍历 Run 再遍历 Hyperlink，会把链接文字全部甩到段尾 —— 顺序错乱。
		/// 同时处理 Word 的域代码：页码域输出占位符交给 JS 按实际页填充，
		/// 其余域（如目录、日期）沿用 Word 缓存的结果文本。
		/// </summary>
		private void BuildInlineContent(OpenXmlElement container, string style, StringBuilder content)
		{
			int fieldDepth = 0;
			string instr = null;
			bool inResult = false;
			bool emitted = false;

			foreach (var child in container.ChildElements)
			{
				if (child is Run run)
				{
					var fc = run.Elements<FieldChar>().FirstOrDefault();
					if (fc != null)
					{
						var t = fc.FieldCharType?.Value;
						if (t == FieldCharValues.Begin) { fieldDepth++; instr = null; inResult = false; emitted = false; }
						else if (t == FieldCharValues.Separate) { inResult = true; }
						else if (t == FieldCharValues.End) { if (fieldDepth > 0) fieldDepth--; inResult = false; instr = null; }
						continue;
					}

					if (fieldDepth > 0)
					{
						var code = string.Concat(run.Elements<FieldCode>().Select(x => x.Text));
						if (!string.IsNullOrEmpty(code)) { instr = (instr ?? "") + code; continue; }
						if (!inResult) continue;

						var ph = FieldPlaceholder(instr);
						if (ph != null)
						{
							if (!emitted) { content.Append(ph); emitted = true; }
							continue;
						}
						content.Append(ConvertRun(run, style));
						continue;
					}

					content.Append(ConvertRun(run, style));
				}
				else if (child is Hyperlink hl)
				{
					foreach (var r in hl.Elements<Run>())
						content.Append(ConvertRun(r, style));
				}
				else if (child is SimpleField sf)
				{
					var ph = FieldPlaceholder(sf.Instruction?.Value);
					if (ph != null) content.Append(ph);
					else foreach (var r in sf.Elements<Run>()) content.Append(ConvertRun(r, style));
				}
			}
		}

		/// 页码类域 → 占位符，由 JS 按所在页填真实数字
		private static string FieldPlaceholder(string instr)
		{
			if (string.IsNullOrEmpty(instr)) return null;
			var s = instr.Trim().ToUpperInvariant();
			if (s.StartsWith("PAGE ") || s == "PAGE" || s.StartsWith("PAGE\\")) return "<span class='pgnum'>1</span>";
			if (s.StartsWith("NUMPAGES")) return "<span class='pgtot'>1</span>";
			return null;
		}

		/// <summary>
		/// 取当前节的页眉或页脚 HTML。Word 文档的页码几乎都在页脚里，
		/// 不渲染就等于每页底部凭空少一行，和原稿对不上。
		/// </summary>
		private string ExtractHeaderFooterHtml(WordprocessingDocument doc, SectionProperties sectPr, bool header)
		{
			try
			{
				if (sectPr == null) return null;
				string relId = null;
				if (header)
				{
					var refs = sectPr.Elements<HeaderReference>().ToList();
					relId = refs.FirstOrDefault(r => r.Type?.Value == HeaderFooterValues.Default)?.Id?.Value
							?? refs.FirstOrDefault()?.Id?.Value;
				}
				else
				{
					var refs = sectPr.Elements<FooterReference>().ToList();
					relId = refs.FirstOrDefault(r => r.Type?.Value == HeaderFooterValues.Default)?.Id?.Value
							?? refs.FirstOrDefault()?.Id?.Value;
				}
				if (string.IsNullOrEmpty(relId)) return null;

				var part = doc.MainDocumentPart?.GetPartById(relId);
				OpenXmlElement root = header
					? (part as HeaderPart)?.Header as OpenXmlElement
					: (part as FooterPart)?.Footer as OpenXmlElement;
				if (root == null) return null;

				var html = ConvertHeaderFooter(root);
				// 只有空段落就不注入，免得凭空多出空白条
				var plain = Regex.Replace(html ?? "", "<[^>]+>", "").Replace("&nbsp;", "").Trim();
				if (string.IsNullOrEmpty(plain) && !(html ?? "").Contains("<img")) return null;
				return html;
			}
			catch { return null; }
		}

		/// 页眉/页脚内容 → HTML
		private string ConvertHeaderFooter(OpenXmlElement root)
		{
			if (root == null) return "";
			var sb = new StringBuilder();
			foreach (var el in root.ChildElements)
			{
				if (el is ParagraphW p) sb.Append(ConvertParagraph(p, _doc, "div"));
				else if (el is Table t) sb.Append(ConvertTable(t, _doc));
			}
			return sb.ToString();
		}

		private string BuildParagraphStyle(ParagraphProperties pProps, ParagraphProperties stylePProps, NumLevel numLvl = null)
		{
			var sb = new StringBuilder();
 T Get<T>(Func<ParagraphProperties, T> getter) where T : class
 {
 var d = pProps != null ? getter(pProps) : null;
 if (d != null) return d;
 var i = stylePProps != null ? getter(stylePProps) : null;
 return i;
 }

 var spacing = Get(sp => sp.SpacingBetweenLines);
 if (spacing != null)
 {
 var lineStr = spacing.Line?.Value;
 var lineRule = spacing.LineRule?.Value ?? LineSpacingRuleValues.Auto;
 if (int.TryParse(lineStr, out int lineVal) && lineVal > 0)
 {
 if (lineRule == LineSpacingRuleValues.Auto)
 sb.Append($"line-height:{lineVal / 240.0:F3};");
 else
 sb.Append($"line-height:{(lineVal / 20.0):F1}pt;");
 }
 // 段前/段后：Word 允许以"行"为单位（w:beforeLines，1/100 行），优先级高于 twips
 var beforeLines = spacing.BeforeLines?.Value;
 var afterLines = spacing.AfterLines?.Value;
 if (beforeLines.HasValue && beforeLines.Value > 0)
 sb.Append($"margin-top:{beforeLines.Value / 100.0:F2}em;");
 else if (int.TryParse(spacing.Before?.Value, out int beforeVal) && beforeVal > 0)
 sb.Append($"margin-top:{TwipsToPx(beforeVal):F1}px;");
 if (afterLines.HasValue && afterLines.Value > 0)
 sb.Append($"margin-bottom:{afterLines.Value / 100.0:F2}em;");
 else if (int.TryParse(spacing.After?.Value, out int afterVal) && afterVal > 0)
 sb.Append($"margin-bottom:{TwipsToPx(afterVal):F1}px;");
 }

			var indent = Get(sp => sp.Indentation);
			// 段落自身未定义缩进时，继承列表级别的缩进（Word 的 numPr 缩进继承）
			double numLeft = numLvl?.IndentLeftPx ?? 0;
			double numHang = numLvl?.IndentHangingPx ?? 0;
			if (indent != null)
			{
				// ── 字符单位缩进优先 ──
				// 中文 Word 文档几乎都用"首行缩进 2 字符"(w:firstLineChars="200")，
				// 只读 twips 版的 w:firstLine 会把缩进整个丢掉，段落看起来全部顶格。
				// CSS 的 em 恰好等于一个全角汉字宽，直接换算最准。
				int? leftChars = indent.LeftChars?.Value;
				int? rightChars = indent.RightChars?.Value;
				int? firstChars = indent.FirstLineChars?.Value;
				int? hangChars = indent.HangingChars?.Value;

				bool leftDone = false;
				if (leftChars.HasValue && leftChars.Value != 0)
				{
					sb.Append($"margin-left:{leftChars.Value / 100.0:F2}em;");
					leftDone = true;
				}
				else if (int.TryParse(indent.Left?.Value, out int leftVal) && leftVal != 0)
				{
					sb.Append($"margin-left:{TwipsToPx(leftVal):F1}px;");
					leftDone = true;
				}
				else if (numLeft > 0)
				{
					sb.Append($"margin-left:{numLeft:F1}px;");
					leftDone = true;
				}

				if (rightChars.HasValue && rightChars.Value != 0)
					sb.Append($"margin-right:{rightChars.Value / 100.0:F2}em;");
				else if (int.TryParse(indent.Right?.Value, out int rightVal) && rightVal != 0)
					sb.Append($"margin-right:{TwipsToPx(rightVal):F1}px;");

				if (firstChars.HasValue && firstChars.Value > 0)
					sb.Append($"text-indent:{firstChars.Value / 100.0:F2}em;");
				else if (int.TryParse(indent.FirstLine?.Value, out int firstLineVal) && firstLineVal > 0)
					sb.Append($"text-indent:{TwipsToPx(firstLineVal):F1}px;");

				if (hangChars.HasValue && hangChars.Value > 0)
				{
					sb.Append($"text-indent:-{hangChars.Value / 100.0:F2}em;");
					if (!leftDone) sb.Append($"margin-left:{hangChars.Value / 100.0:F2}em;");
				}
				else if (int.TryParse(indent.Hanging?.Value, out int hangingVal) && hangingVal > 0)
				{
					sb.Append($"text-indent:-{TwipsToPx(hangingVal):F1}px;");
					if (!leftDone) sb.Append($"margin-left:{TwipsToPx(hangingVal):F1}px;");
				}
				else if (numHang > 0)
					sb.Append($"text-indent:-{numHang:F1}px;");
			}
			else if (numLvl != null)
			{
				if (numLeft > 0) sb.Append($"margin-left:{numLeft:F1}px;");
				if (numHang > 0) sb.Append($"text-indent:-{numHang:F1}px;");
			}

 // 段对齐（inline，便于表格单元格内也生效）
 var jc = Get(sp => sp.Justification)?.Val?.Value;
 if (jc == JustificationValues.Center) sb.Append("text-align:center;");
 else if (jc == JustificationValues.Right) sb.Append("text-align:right;");
 else if (jc == JustificationValues.Both) sb.Append("text-align:justify;");
 else if (jc == JustificationValues.Left || jc == JustificationValues.Start) sb.Append("text-align:left;");
 else if (jc == JustificationValues.Distribute)
 sb.Append("text-align:justify;text-align-last:justify;text-justify:distribute;");

 var shading = Get(sp => sp.Shading);
 if (shading != null)
 {
 var fill = shading.Fill?.Value;
 if (!string.IsNullOrEmpty(fill) && fill != "auto")
 sb.Append($"background-color:#{fill};");
 }
 return sb.ToString();
 }

 // ═════════════ Run ═════════════
 private string ConvertRun(Run run, string paraStyleId = "")
 {
 var sb = new StringBuilder();
 var props = run.RunProperties;
 var styleProps = ResolveStyleRunProps(paraStyleId);

 T Get<T>(Func<RunProperties, T> getter) where T : class
 {
 if (props != null) { var v = getter(props); if (v != null) return v; }
 if (styleProps != null) { var v = getter(styleProps); if (v != null) return v; }
 return null;
 }

 bool bold = IsPropTrue(Get(r => r.Bold)) || IsPropTrue(Get(r => r.BoldComplexScript));
 bool italic = IsPropTrue(Get(r => r.Italic)) || IsPropTrue(Get(r => r.ItalicComplexScript));
 bool underline = RunHasUnderline(Get(r => r.Underline));
 bool strike = IsPropTrue(Get(r => r.Strike));

 var color = Get(r => r.Color)?.Val?.Value;
 var sz = Get(r => r.FontSize)?.Val?.Value ?? Get(r => r.FontSizeComplexScript)?.Val?.Value;
 var runFonts = Get(r => r.RunFonts);

 var prefix = ""; var suffix = "";
 if (bold) { prefix += "<b>"; suffix = "</b>" + suffix; }
 if (italic) { prefix += "<i>"; suffix = "</i>" + suffix; }
 if (underline) { prefix += "<u>"; suffix = "</u>" + suffix; }
 if (strike) { prefix += "<s>"; suffix = "</s>" + suffix; }

 var vertAlign = Get(r => r.VerticalTextAlignment);
 if (vertAlign != null)
 {
 if (vertAlign.Val?.Value == VerticalPositionValues.Superscript) { prefix += "<sup>"; suffix = "</sup>" + suffix; }
 else if (vertAlign.Val?.Value == VerticalPositionValues.Subscript) { prefix += "<sub>"; suffix = "</sub>" + suffix; }
 }

 var styleAttr = new StringBuilder();
 // 字体（Ascii / EastAsia / HighAnsi / ComplexScript）
 if (runFonts != null)
 {
 var ascii = runFonts.Ascii?.Value ?? runFonts.HighAnsi?.Value;
 var ea = runFonts.EastAsia?.Value;
 var cs = runFonts.ComplexScript?.Value;
 var fonts = new List<string>();
 if (!string.IsNullOrEmpty(ascii)) fonts.Add(ascii);
 if (!string.IsNullOrEmpty(ea)) fonts.Add(ea);
 if (!string.IsNullOrEmpty(cs)) fonts.Add(cs);
 fonts.Add("'Microsoft YaHei'");
 fonts.Add("'SimSun'");
 fonts.Add("sans-serif");
 if (fonts.Count > 0)
 styleAttr.Append($"font-family:{string.Join(",", fonts)};");
 }
 // 字号（优先东亚 szCs，保证中文文档字高正确）
 if (!string.IsNullOrEmpty(sz) && int.TryParse(sz, out int szVal) && szVal > 0)
 styleAttr.Append($"font-size:{szVal / 2.0:F1}pt;");
 // 颜色
 if (!string.IsNullOrEmpty(color) && color != "auto" && color != "000000")
 styleAttr.Append($"color:#{color};");
 // 字符间距（字间距） w:spacing/@w:val 单位 twips
 var charSpacing = props?.Spacing ?? styleProps?.Spacing;
 if (charSpacing?.Val?.Value != null)
 {
 double pxv = TwipsToPx(charSpacing.Val.Value);
 if (Math.Abs(pxv) > 0.01)
 styleAttr.Append($"letter-spacing:{pxv:F1}px;");
 }
 // 字符缩放 w:w (percent) → font-stretch
 var wscale = props?.CharacterScale?.Val?.Value ?? styleProps?.CharacterScale?.Val?.Value;
 if (wscale != null && wscale != 100)
 styleAttr.Append($"font-stretch:{wscale.Value}%;");
 // 高亮
 var highlight = props?.Highlight ?? styleProps?.Highlight;
 if (highlight?.Val?.Value != null)
 {
 var hl = highlight.Val.Value.ToString();
 if (hl != "none") styleAttr.Append($"background-color:{HighlightToColor(hl)};");
 }
 // 字符底纹
 var rshading = props?.Shading ?? styleProps?.Shading;
 if (rshading != null)
 {
 var fill = rshading.Fill?.Value;
 if (!string.IsNullOrEmpty(fill) && fill != "auto") styleAttr.Append($"background-color:#{fill};");
 }

 if (styleAttr.Length > 0)
 {
 prefix = $"<span style='{styleAttr}'>" + prefix;
 suffix = suffix + "</span>";
 }

 foreach (var child in run.ChildElements)
 {
 if (child is Text text)
 {
 var escaped = System.Net.WebUtility.HtmlEncode(text.Text);
 escaped = escaped.Replace("  ", "&nbsp; ");
 sb.Append(prefix + escaped + suffix);
 }
 else if (child is Break br)
 {
 if (br.Type?.Value != BreakValues.Page) sb.Append("<br/>");
 }
				else if (child.LocalName == "pict" || child.LocalName == "drawing")
				{
					// 文本框（VML v:textbox / DrawingML wps:txbx）里装的是真正的段落。
					// WPS 生成的页眉页脚页码、图表标注大量用这个结构，
					// 一律当图片处理会把整段文字丢掉 —— 页脚会变成空白。
					var txbx = child.Descendants().FirstOrDefault(d => d.LocalName == "txbxContent");
					if (txbx != null && _txbxDepth < 4)
					{
						_txbxDepth++;
						var inner = new StringBuilder();
						try
						{
							foreach (var el in txbx.ChildElements)
							{
								if (el is ParagraphW tp) inner.Append(ConvertParagraph(tp, _doc, "div"));
								else if (el is Table tt) inner.Append(ConvertTable(tt, _doc));
							}
						}
						catch { }
						finally { _txbxDepth--; }
						if (inner.Length > 0) { sb.Append(inner); continue; }
					}
					var imgHtml = ConvertImage(child, run);
					if (!string.IsNullOrEmpty(imgHtml)) sb.Append(imgHtml);
				}
				else if (child is TabChar)
				{
					// 按文档的 w:defaultTabStop 走，固定 1.5em 会让对齐的列全部错位
					sb.Append($"<span style='display:inline-block;width:{_defaultTabPx:F1}px;'>&nbsp;</span>");
				}
 else if (child is CarriageReturn)
 {
 sb.Append("<br/>");
 }
 }
 return sb.ToString();
 }

 private static bool RunHasUnderline(Underline u)
 {
 if (u == null) return false;
 var v = u.Val?.Value;
 if (v == null) return true; // 元素存在无 Val = 单下划线
 return v != UnderlineValues.None;
 }

 private static bool IsPropTrue<T>(T direct) where T : OpenXmlElement
 {
 if (direct == null) return false;
 var valProp = direct.GetType().GetProperty("Val");
 if (valProp != null)
 {
 var val = valProp.GetValue(direct);
 if (val != null)
 {
 var innerProp = val.GetType().GetProperty("Value");
 if (innerProp != null)
 {
 var inner = innerProp.GetValue(val);
 if (inner is bool b) return b;
 }
 }
 return true;
 }
 return false;
 }

 private static string HighlightToColor(string name)
 {
 return name?.ToLower() switch
 {
 "yellow" => "#ffff00", "green" => "#00ff00", "cyan" => "#00ffff",
 "magenta" => "#ff00ff", "blue" => "#0000ff", "red" => "#ff0000",
 "darkblue" => "#00008b", "darkcyan" => "#008b8b", "darkgreen" => "#006400",
 "darkmagenta" => "#8b008b", "darkred" => "#8b0000", "darkyellow" => "#8b8b00",
 "darkgray" => "#a9a9a9", "lightgray" => "#d3d3d3", "black" => "#000000",
 _ => "#ffff00",
 };
 }

 // ═════════════ 图片 ═════════════
 private static string ConvertImage(OpenXmlElement element, Run parentRun)
 {
 var xml = element.OuterXml;
 var match = Regex.Match(xml, "r:embed=\"([^\"]+)\"|r:id=\"([^\"]+)\"");
 if (!match.Success) return "";
 var relId = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
 try
 {
 var docPart = parentRun.Ancestors<Document>()?.FirstOrDefault();
 if (docPart == null) return "";
 var mainPart = docPart.MainDocumentPart;
 if (mainPart == null) return "";
 var imagePart = mainPart.GetPartById(relId) as ImagePart;
 if (imagePart == null) return "";
 using var stream = imagePart.GetStream();
 using var ms = new MemoryStream();
 stream.CopyTo(ms);
 var base64 = System.Convert.ToBase64String(ms.ToArray());
 var mime = imagePart.ContentType;
 string imgStyle = "max-width:100%;margin:6px 0;display:block;";
 var extentMatch = Regex.Match(xml, "cx=\"(\\d+)\".*?cy=\"(\\d+)\"");
 if (extentMatch.Success && long.TryParse(extentMatch.Groups[1].Value, out long cx) && long.TryParse(extentMatch.Groups[2].Value, out long cy))
 {
 double wPx = Math.Round(cx / 914400.0 * 96.0);
 double hPx = Math.Round(cy / 914400.0 * 96.0);
 if (wPx > 0 && hPx > 0) imgStyle = $"width:{wPx:F0}px;height:{hPx:F0}px;margin:6px 0;display:block;max-width:100%;";
 }
 return $"<img src='data:{mime};base64,{base64}' style='{imgStyle}'/> ";
 }
 catch { return ""; }
 }

 // ═════════════ 表格 ═════════════
 private string ConvertTable(Table table, WordprocessingDocument doc)
 {
 var tp = table.GetFirstChild<TableProperties>();
 var tblW = tp?.TableWidth;
 string tblWidthCss = WidthToCss(tblW?.Type?.InnerText, tblW?.Width?.Value.ToString());
 bool fixedLayout = tp?.TableLayout?.Type?.Value == TableLayoutValues.Fixed;
 var tblBorders = tp?.TableBorders;
 var cellSpacing = tp?.TableCellSpacing;
 double? spacingPx = null;
 if (cellSpacing != null && int.TryParse(cellSpacing.Width?.Value.ToString() ?? "0", out int cs2) && cs2 > 0)
 spacingPx = TwipsToPx(cs2);
 var tblJc = tp?.GetFirstChild<TableJustification>()?.Val?.InnerText;
 var tblInd = tp?.TableIndentation;
 double? indPx = null;
 if (tblInd != null && int.TryParse(tblInd.Width?.Value.ToString() ?? "0", out int indVal) && indVal != 0)
 indPx = TwipsToPx(indVal);

            var rows = table.Elements<TableRowW>().ToList();

            // ── 列宽：优先用 <w:tblGrid>（Word 的权威列定义），
            //    退化到首行单元格宽度。只看首行在有合并单元格时会算错。
            var colWidths = new List<string>();
            double gridTotalTwips = 0;
            var tblGrid = table.Elements<TableGrid>().FirstOrDefault();
            if (tblGrid != null)
            {
                foreach (var gc in tblGrid.Elements<GridColumn>())
                {
                    var wv = gc.Width?.Value;
                    if (!string.IsNullOrEmpty(wv) && int.TryParse(wv, out int gw) && gw > 0)
                    {
                        colWidths.Add($"{TwipsToPx(gw):F1}px");
                        gridTotalTwips += gw;
                    }
                    else colWidths.Add("auto");
                }
            }
            if (colWidths.Count == 0 && rows.Count > 0)
            {
                foreach (var cell in rows[0].Elements<TableCellW>())
                {
                    int gs = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                    var cw = cell.TableCellProperties?.TableCellWidth;
                    string w = WidthToCss(cw?.Type?.InnerText, cw?.Width?.Value.ToString()) ?? "auto";
                    for (int k = 0; k < gs; k++) colWidths.Add(w);
                }
            }
            // 有明确网格时强制 fixed 布局，否则浏览器会按内容重算列宽 → 与 Word 不符
            bool hasGrid = gridTotalTwips > 0;

 var sb = new StringBuilder();
 sb.Append("<table class='docx-table'");
            var tStyle = new StringBuilder();
            if (!string.IsNullOrEmpty(tblWidthCss)) tStyle.Append($"width:{tblWidthCss};");
            else if (hasGrid) tStyle.Append($"width:{TwipsToPx((int)gridTotalTwips):F1}px;");
            tStyle.Append((fixedLayout || hasGrid) ? "table-layout:fixed;" : "table-layout:auto;");
 if (spacingPx.HasValue) tStyle.Append($"border-collapse:separate;border-spacing:{spacingPx:F1}px;");
 if (tblJc == "center") tStyle.Append("margin-left:auto;margin-right:auto;");
 else if (tblJc == "right") tStyle.Append("margin-left:auto;margin-right:0;");
 if (indPx.HasValue && tblJc != "center") tStyle.Append($"margin-left:{indPx:F1}px;");

 // 外边框
 if (tblBorders != null)
 {
 var top = BorderToCss(tblBorders.TopBorder);
 var right = BorderToCss(tblBorders.RightBorder);
 var bot = BorderToCss(tblBorders.BottomBorder);
 var left = BorderToCss(tblBorders.LeftBorder);
 if (top != null) tStyle.Append($"border-top:{top};");
 if (right != null) tStyle.Append($"border-right:{right};");
 if (bot != null) tStyle.Append($"border-bottom:{bot};");
 if (left != null) tStyle.Append($"border-left:{left};");
 }
 if (tStyle.Length > 0) sb.Append($" style='{tStyle}'");
 sb.Append(">");

 if (colWidths.Count > 0)
 {
 sb.Append("<colgroup>");
 foreach (var w in colWidths) sb.Append($"<col style='width:{w};'/>");
 sb.Append("</colgroup>");
 }

 for (int r = 0; r < rows.Count; r++)
 {
 bool isHeaderRow = rows[r].TableRowProperties?.GetFirstChild<TableHeader>() != null;

 var trStyle = new StringBuilder();
 var trH = rows[r].TableRowProperties?.GetFirstChild<TableRowHeight>();
 if (trH != null && int.TryParse(trH.Val?.Value.ToString() ?? "0", out int rhVal) && rhVal > 0)
 {
 // hRule: atLeast/exact/auto
 var hRule = trH.HeightType?.InnerText;
 if (hRule == "exact")
 trStyle.Append($"height:{TwipsToPx(rhVal):F1}px;");
 else
 trStyle.Append($"min-height:{TwipsToPx(rhVal):F1}px;");
 }
 sb.Append(isHeaderRow ? "<tr class='tbl-header-row'" : "<tr");
 if (trStyle.Length > 0) sb.Append($" style='{trStyle}'");
 sb.Append(">");

 int colIdx = 0;
 foreach (var cell in rows[r].Elements<TableCellW>())
 {
 var tcp = cell.TableCellProperties;
 var gridSpan = tcp?.GridSpan?.Val?.Value ?? 1;
 var vMerge = tcp?.VerticalMerge;
 var vMergeVal = vMerge == null ? (MergedCellValues?)null : (vMerge.Val?.Value ?? MergedCellValues.Continue);

 // 垂直合并：continue 跳过（不输出 td）
 if (vMergeVal == MergedCellValues.Continue)
 {
 colIdx += gridSpan;
 continue;
 }

 int rowspan = 1;
 if (vMergeVal == MergedCellValues.Restart)
 {
 for (int rr = r + 1; rr < rows.Count; rr++)
 {
 int checkCol = 0;
 bool found = false;
 foreach (var c2 in rows[rr].Elements<TableCellW>())
 {
 int gs2 = c2.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
 if (checkCol == colIdx)
 {
 var vm2 = c2.TableCellProperties?.VerticalMerge;
 var v2 = vm2 == null ? (MergedCellValues?)null : (vm2.Val?.Value ?? MergedCellValues.Continue);
 if (v2 == MergedCellValues.Continue) { found = true; rowspan++; }
 break;
 }
 checkCol += gs2;
 }
 if (!found) break;
 }
 }

 // 单元格样式
 var cellStyle = new StringBuilder();
 // 宽度（auto 布局时有用）
 var cw2 = tcp?.TableCellWidth;
 string cwCss = WidthToCss(cw2?.Type?.InnerText, cw2?.Width?.Value.ToString());
 if (!string.IsNullOrEmpty(cwCss) && !fixedLayout) cellStyle.Append($"width:{cwCss};");
 // 底纹
 var shd = tcp?.Shading;
 if (shd != null)
 {
 var fill = shd.Fill?.Value;
 if (!string.IsNullOrEmpty(fill) && fill != "auto") cellStyle.Append($"background-color:#{fill};");
 }
 // 垂直对齐
 var vAlign = tcp?.TableCellVerticalAlignment?.Val?.Value;
 if (vAlign == TableVerticalAlignmentValues.Center) cellStyle.Append("vertical-align:middle;");
 else if (vAlign == TableVerticalAlignmentValues.Bottom) cellStyle.Append("vertical-align:bottom;");
 else if (vAlign == TableVerticalAlignmentValues.Top) cellStyle.Append("vertical-align:top;");
 // 文字方向
 var tdStr = tcp?.TextDirection?.Val?.InnerText;
 if (tdStr == "tbRl")
 cellStyle.Append("writing-mode:vertical-rl;");
 else if (tdStr == "btLr")
 cellStyle.Append("writing-mode:vertical-lr;");
 // 单元格边框（tcBorders 优先，否则用表格内边框）
 string cellBorderCss = BuildCellBorderCss(tcp?.TableCellBorders, tblBorders);
 if (!string.IsNullOrEmpty(cellBorderCss)) cellStyle.Append(cellBorderCss);

 var spanAttr = gridSpan > 1 ? $" colspan='{gridSpan}'" : "";
 var rowSpanAttr = rowspan > 1 ? $" rowspan='{rowspan}'" : "";

 // 单元格内容
 var cellContent = new StringBuilder();
 foreach (var para in cell.Elements<ParagraphW>())
 cellContent.Append(ConvertParagraph(para, doc, "div"));

 // 去重包装：把外层 div.cellp 合并，保持内联样式
 var inner = cellContent.ToString();

 sb.Append($"<td{spanAttr}{rowSpanAttr}");
 if (cellStyle.Length > 0) sb.Append($" style='{cellStyle}'");
 sb.Append($">{inner}</td>");
 colIdx += gridSpan;
 }
 sb.Append("</tr>");
 }
 sb.Append("</table>");
 return sb.ToString();
 }

 // 单元格边框 CSS：tcBorders 优先，否则用 tblBorders 的内边框(insideH/insideV)
 private string BuildCellBorderCss(TableCellBorders tcB, TableBorders tblB)
 {
 BorderType top = null, left = null, bottom = null, right = null;
 if (tcB != null)
 {
 top = tcB.TopBorder; left = tcB.LeftBorder; bottom = tcB.BottomBorder; right = tcB.RightBorder;
 }
 if (top == null && tblB != null)
 {
 // 内边框：单元格上/左用 insideH/insideV，下/右用 insideH/insideV（collapse 时合并）
 top = tblB.InsideHorizontalBorder; left = tblB.InsideVerticalBorder;
 bottom = tblB.InsideHorizontalBorder; right = tblB.InsideVerticalBorder;
 }
 var sb = new StringBuilder();
 var t = BorderToCss(top); if (t != null) sb.Append($"border-top:{t};");
 var l = BorderToCss(left); if (l != null) sb.Append($"border-left:{l};");
 var b = BorderToCss(bottom); if (b != null) sb.Append($"border-bottom:{b};");
 var rt = BorderToCss(right); if (rt != null) sb.Append($"border-right:{rt};");
 return sb.Length > 0 ? sb.ToString() : "";
 }

 // ═════════════ 边框/尺寸辅助 ═════════════
 private static string BorderToCss(BorderType border)
 {
 if (border == null) return null;
 var val = border.Val?.InnerText;
 if (val == null || val == "none" || val == "nil") return "none";
 uint szU = border.Size?.Value ?? 0; // 1/8 pt
 int sz = (int)szU;
 var color = border.Color?.Value;
 if (color == "auto") color = "000000";
 if (string.IsNullOrEmpty(color)) color = "000000";
 // 1/8 pt → pt → px (1pt≈1.333px)
 double px = Math.Max(0.5, (sz / 8.0) * 1.3333);
 string cssStyle = val switch
 {
 "double" => "double",
 "dashed" => "dashed",
 "dotted" => "dotted",
 "dashDot" => "dashed",
 "dashDotDot" => "dashed",
 "wave" => "solid",
 _ => "solid",
 };
 if (val == "double") px = Math.Max(3.0, px); // double 需足够粗才显示
 return $"{px:F1}px {cssStyle} #{color}";
 }

 private static string WidthToCss(string type, string val)
 {
 if (string.IsNullOrEmpty(val)) return null;
 if (type == "pct")
 {
 if (int.TryParse(val, out int p) && p > 0) return $"{p / 50.0:F2}%"; // fiftieths of percent
 return null;
 }
 if (int.TryParse(val, out int dxa) && dxa > 0) return $"{TwipsToPx(dxa):F1}px";
 return null;
 }

 private static double ParsePt(string css, double fallback)
 {
 if (string.IsNullOrEmpty(css)) return fallback;
 var m = Regex.Match(css, @"([\d.]+)pt");
 if (m.Success && double.TryParse(m.Groups[1].Value, out double v)) return v;
 return fallback;
 }

 private static string GetAlign(JustificationValues? justify)
 {
 if (justify == null) return "";
 if (justify == JustificationValues.Center) return "center";
 if (justify == JustificationValues.Right) return "right";
 if (justify == JustificationValues.Both) return "justify";
 return "";
 }

 // ═════════════ 分节属性 ═════════════
 private static PageLayout ParseSectionProperties(SectionProperties sectPr)
 {
 var layout = new PageLayout();
 if (sectPr == null) { layout.ApplyDefaults(); return layout; }
 var pgSz = sectPr.Elements<PageSize>().FirstOrDefault();
 if (pgSz != null)
 {
 layout.PageWidthTwips = GetIntValue(pgSz, "Width", 11906);
 layout.PageHeightTwips = GetIntValue(pgSz, "Height", 16838);
 }
 else layout.ApplyDefaults();
 var pgMar = sectPr.Elements<PageMargin>().FirstOrDefault();
 if (pgMar != null)
 {
 layout.MarginTopTwips = GetIntValue(pgMar, "Top", 1440);
 layout.MarginBottomTwips = GetIntValue(pgMar, "Bottom", 1440);
 layout.MarginLeftTwips = GetIntValue(pgMar, "Left", 1800);
 layout.MarginRightTwips = GetIntValue(pgMar, "Right", 1800);
 }
 else
 {
 layout.MarginTopTwips = 1440; layout.MarginBottomTwips = 1440;
 layout.MarginLeftTwips = 1800; layout.MarginRightTwips = 1800;
 }
 layout.IsLandscape = layout.PageWidthTwips > layout.PageHeightTwips;
 var cols = sectPr.Elements<Columns>().FirstOrDefault();
 if (cols != null)
 {
 layout.ColumnCount = GetIntValue(cols, "ColumnCount", "NumberColumns", 1);
 if (layout.ColumnCount < 1) layout.ColumnCount = 1;
 layout.ColumnGapTwips = GetIntValue(cols, "Space", 425);
 }
 var pgMar2 = sectPr.Elements<PageMargin>().FirstOrDefault();
 if (pgMar2 != null)
 {
 layout.HeaderTwips = GetIntValue(pgMar2, "Header", 720);
 layout.FooterTwips = GetIntValue(pgMar2, "Footer", 720);
 }
 return layout;
 }

 private static int GetIntValue(OpenXmlElement elem, string propName, int defaultValue)
 {
 return GetIntValue(elem, propName, null, defaultValue);
 }
 private static int GetIntValue(OpenXmlElement elem, string prop1, string prop2, int defaultValue)
 {
 foreach (var name in new[] { prop1, prop2 })
 {
 if (string.IsNullOrEmpty(name)) continue;
 var prop = elem.GetType().GetProperty(name);
 if (prop == null) continue;
 var val = prop.GetValue(elem);
 if (val == null) continue;
 var valProp = val.GetType().GetProperty("Value");
 if (valProp != null)
 {
 var inner = valProp.GetValue(val);
 if (inner != null && int.TryParse(inner.ToString(), out int result)) return result;
 }
 if (int.TryParse(val.ToString(), out int result2)) return result2;
 }
 return defaultValue;
 }

 /// 页面布局参数
 internal class PageLayout
 {
 public int PageWidthTwips { get; set; }
 public int PageHeightTwips { get; set; }
 public bool IsLandscape { get; set; }
 public int MarginTopTwips { get; set; }
 public int MarginBottomTwips { get; set; }
 public int MarginLeftTwips { get; set; }
 public int MarginRightTwips { get; set; }
 public int ColumnCount { get; set; } = 1;
 public int ColumnGapTwips { get; set; } = 425;
 /// 页眉距上边界 / 页脚距下边界（twips）
 public int HeaderTwips { get; set; } = 720;
 public int FooterTwips { get; set; } = 720;
 public double HeaderPx => Math.Round(HeaderTwips / (1440.0 / 96.0));
 public double FooterPx => Math.Round(FooterTwips / (1440.0 / 96.0));
 public double PageWidthPx => Math.Round(PageWidthTwips / (1440.0 / 96.0));
 public double PageHeightPx => Math.Round(PageHeightTwips / (1440.0 / 96.0));
 public double MarginTopPx => Math.Round(MarginTopTwips / (1440.0 / 96.0));
 public double MarginBottomPx => Math.Round(MarginBottomTwips / (1440.0 / 96.0));
 public double MarginLeftPx => Math.Round(MarginLeftTwips / (1440.0 / 96.0));
 public double MarginRightPx => Math.Round(MarginRightTwips / (1440.0 / 96.0));
 public double ColumnGapPx => Math.Round(ColumnGapTwips / (1440.0 / 96.0));
 public void ApplyDefaults()
 {
 PageWidthTwips = 11906; PageHeightTwips = 16838; IsLandscape = false;
 MarginTopTwips = 1440; MarginBottomTwips = 1440;
 MarginLeftTwips = 1800; MarginRightTwips = 1800;
 ColumnCount = 1; ColumnGapTwips = 425;
 }
 }
 }
}
