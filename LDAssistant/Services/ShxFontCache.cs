using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace LDAssistant.Services
{
	/// <summary>
	/// SHX 矢量字体解析缓存
	/// 解析 AutoCAD SHX 字体文件，提取字符的矢量笔画数据
	/// 参考: https://github.com/tatarize/shxparser
	/// </summary>
	public class ShxFontCache
	{
		private class ShxFont
		{
			public string Name;
			public string Type; // "unifont", "bigfont", "shapes"
			public int Above; // 基线以上高度
			public int Below; // 基线以下高度
			// 字符码 → 字节码数据
			public Dictionary<int, byte[]> Glyphs = new Dictionary<int, byte[]>();
			// 缓存的笔画（字符码→笔画列表，归一化坐标0~1）
			public Dictionary<int, List<List<Point>>> CharStrokes = new Dictionary<int, List<List<Point>>>();
		}

		private readonly Dictionary<string, ShxFont> _fonts = new Dictionary<string, ShxFont>(StringComparer.OrdinalIgnoreCase);

		// 16方向编码的dx,dy查找表
		private static readonly double[] DirX = { 1, 1, 1, 0.5, 0, -0.5, -1, -1, -1, -1, -1, -0.5, 0, 0.5, 1, 1 };
		private static readonly double[] DirY = { 0, 0.5, 1, 1, 1, 1, 1, 0.5, 0, -0.5, -1, -1, -1, -1, -1, -0.5 };

		public void LoadFonts(string fontsDir)
		{
			if (!Directory.Exists(fontsDir)) return;
			foreach (var file in Directory.GetFiles(fontsDir, "*.shx", SearchOption.TopDirectoryOnly))
			{
				try
				{
					var name = Path.GetFileNameWithoutExtension(file);
					var font = ParseFont(file, name);
					if (font != null)
						_fonts[name] = font;
				}
				catch { }
			}
		}

		private ShxFont ParseFont(string path, string name)
		{
			var data = File.ReadAllBytes(path);
			if (data.Length < 27) return null;

			// 解析头部: "AutoCAD-86 <type> <version>\r\n\x1a" + 2字节填充
			// 找到 \x1a
			int eofPos = -1;
			for (int i = 0; i < Math.Min(data.Length, 50); i++)
			{
				if (data[i] == 0x1a) { eofPos = i; break; }
			}
			if (eofPos < 0) return null;

			var headerStr = Encoding.ASCII.GetString(data, 0, eofPos);
			var font = new ShxFont { Name = name };

			if (headerStr.Contains("unifont")) font.Type = "unifont";
			else if (headerStr.Contains("bigfont")) font.Type = "bigfont";
			else if (headerStr.Contains("shapes")) font.Type = "shapes";
			else return null;

		// 头部 \x1a 之后:
		// unifont: 直接是 uint32 dataLength + uint16 count + 字体信息 + shape数据
		// bigfont: 2字节padding, 然后 uint16 count + uint16 length + uint16 changeCount + 范围表 + 索引表
		int pos = eofPos + 1;
		if (font.Type == "bigfont") pos += 2; // bigfont跳过2字节padding

			if (font.Type == "unifont")
				ParseUnifont(data, pos, font);
			else if (font.Type == "bigfont")
				ParseBigfont(data, pos, font);
			else
				ParseShapes(data, pos, font);

			return font;
		}

	private void ParseUnifont(byte[] data, int pos, ShxFont font)
	{
		// unifont格式:
		// \x1a后2字节padding，然后:
		// uint32 dataLength (shape数据区总长度)
		// uint16 count (字形条目数)
		// 然后是字体信息字符串(以\0结束) + above(int8) + below(int8) + mode + encoding + embedded + ignore
		// 然后是count个字形条目: uint16 index + uint16 length + length字节data

		// pos 已经是 \x1a + 1 + 2 (跳过2字节padding)
		// 但实际: \x1a后紧跟的就是数据，没有额外padding
		// 文件偏移0x18=\x1a, 0x19=ae 01 00 00 = uint32 dataLength=430
		// 0x1d=33 00 = uint16 count=51

		if (pos + 6 > data.Length) return;
		uint dataLength = BitConverter.ToUInt32(data, pos); pos += 4;
		ushort count = BitConverter.ToUInt16(data, pos); pos += 2;

		// 字体信息: 名称字符串(以\0结束)
		var nameSb = new StringBuilder();
		while (pos < data.Length && data[pos] != 0)
		{
			nameSb.Append((char)data[pos]);
			pos++;
		}
		pos++; // 跳过\0

		// above, below, mode, encoding, embedded, ignore (6字节)
		if (pos + 6 > data.Length) return;
		font.Above = (sbyte)data[pos++];
		font.Below = (sbyte)data[pos++];
		pos += 1; // mode
		pos += 3; // encoding, embedded, ignore

		System.Diagnostics.Debug.WriteLine($"  unifont dataLength={dataLength} count={count} name='{nameSb}' above={font.Above}");

		// 读取 count 个字形条目
		for (int i = 0; i < count && pos + 4 <= data.Length; i++)
		{
			ushort index = BitConverter.ToUInt16(data, pos); pos += 2;
			ushort len = BitConverter.ToUInt16(data, pos); pos += 2;

			if (len == 0 || pos + len > data.Length)
			{
				pos += len;
				continue;
			}

			var glyphData = new byte[len];
			Array.Copy(data, pos, glyphData, 0, len);
			font.Glyphs[index] = glyphData;
			pos += len;
		}
	}

	private void ParseBigfont(byte[] data, int pos, ShxFont font)
	{
		// bigfont格式 (从\x1a后开始):
		// uint16 count (字形条目数)
		// uint16 length (保留/数据长度)
		// uint16 changeCount (范围段数)
		// ranges[changeCount]: (start uint16, end uint16) = 4字节/段
		// 索引表: count条，每条8字节
		//   (uint16 index, uint16 length, uint16 offset, uint16 padding)
		// 其中offset是文件中的绝对偏移(低16位有效)

		if (pos + 6 > data.Length) return;
		ushort count = BitConverter.ToUInt16(data, pos); pos += 2;
		ushort length = BitConverter.ToUInt16(data, pos); pos += 2;
		ushort changeCount = BitConverter.ToUInt16(data, pos); pos += 2;

		// 范围表: changeCount对 (start uint16, end uint16)
		// 实测索引表从固定位置开始，需要通过搜索定位
		// 跳过范围表后搜索第一个有效GB2312条目
		int rangeEnd = pos + changeCount * 4;
		// 搜索索引表起始位置（找到第一个GB2312范围的条目）
		int idxStart = -1;
		for (int srch = rangeEnd; srch < Math.Min(data.Length - 8, rangeEnd + 2000); srch++)
		{
			ushort idx = BitConverter.ToUInt16(data, srch);
			ushort len = BitConverter.ToUInt16(data, srch + 2);
			byte hi = (byte)(idx >> 8);
			byte lo = (byte)(idx & 0xFF);
			if (hi >= 0xA1 && hi <= 0xFE && lo >= 0xA1 && lo <= 0xFE && len >= 1 && len <= 500)
			{
				idxStart = srch;
				break;
			}
		}
		if (idxStart < 0) { idxStart = rangeEnd; }
		pos = idxStart;

		// 索引表: 8字节条目 (u16 index, u16 length, u32 offset)
		while (pos + 8 <= data.Length)
		{
			ushort index = BitConverter.ToUInt16(data, pos);
			ushort len = BitConverter.ToUInt16(data, pos + 2);
			uint offset = BitConverter.ToUInt32(data, pos + 4);
			pos += 8;

			if (index == 0) continue;
			if (len == 0 || len > 500) continue;
			if (offset >= data.Length || offset + len > data.Length) continue;

			if (index == 0 && font.Above == 0)
			{
				font.Above = (sbyte)data[offset];
				font.Below = (sbyte)data[offset + 1];
				continue;
			}

		var glyphData = new byte[len];
		Array.Copy(data, offset, glyphData, 0, len);
		font.Glyphs[index] = glyphData;
		}
	}

	private void ParseShapes(byte[] data, int pos, ShxFont font)
		{
			// shapes: start uint16 + end uint16 + count uint16
			if (pos + 6 > data.Length) return;
			ushort start = BitConverter.ToUInt16(data, pos); pos += 2;
			ushort end = BitConverter.ToUInt16(data, pos); pos += 2;
			ushort count = BitConverter.ToUInt16(data, pos); pos += 2;

			// 索引表: count条 (index uint16 + length uint16) = 4字节
			// 然后是顺序存储的字形数据
			var entries = new List<(int index, int length)>();
			for (int i = 0; i < count && pos + 4 <= data.Length; i++)
			{
				ushort index = BitConverter.ToUInt16(data, pos);
				ushort len = BitConverter.ToUInt16(data, pos + 2);
				entries.Add((index, len));
				pos += 4;
			}

			// 读取字形数据（顺序存储）
			foreach (var (index, length) in entries)
			{
				if (length == 0 || pos + length > data.Length) { pos += length; continue; }

				// shape 0 是字体信息
				if (index == 0)
				{
					// 跳过名称字符串
					int p = pos;
					while (p < data.Length && data[p] != 0 && data[p] != 0x0D && data[p] != 0x0A) p++;
					while (p < data.Length && (data[p] == 0 || data[p] == 0x0D || data[p] == 0x0A)) p++;
					if (p + 2 < data.Length)
					{
						font.Above = (sbyte)data[p];
						font.Below = (sbyte)data[p + 1];
					}
					pos += length;
					continue;
				}

				var glyphData = new byte[length];
				Array.Copy(data, pos, glyphData, 0, length);
				font.Glyphs[index] = glyphData;
				pos += length;
			}
		}

		/// 获取字符的笔画数据（缩放到指定字号）
		public List<List<Point>> GetCharStrokes(string fontName, char ch, double fontSize)
		{
			if (!_fonts.TryGetValue(fontName, out var font)) return null;

			int code = (int)ch;
			if (!font.Glyphs.TryGetValue(code, out var glyphData)) return null;

			// 检查缓存
			if (font.CharStrokes.TryGetValue(code, out var cached))
				return ScaleStrokes(cached, fontSize);

			// 解析笔画数据
			var strokes = ParseShapeBytecode(font, glyphData);
			font.CharStrokes[code] = strokes;

			return ScaleStrokes(strokes, fontSize);
		}

	/// 解析 Shape 字节码——转换为笔画列表
	private List<List<Point>> ParseShapeBytecode(ShxFont font, byte[] data)
	{
		var strokes = new List<List<Point>>();
		var currentStroke = new List<Point>();
		double x = 0, y = 0;
		bool penDown = true;
		double scale = 1.0;
		double above = font.Above > 0 ? font.Above : 1.0;

		// Shape 数据前导字节:
		// unifont: 前2字节是长度头部（大端），跳过
		// bigfont: 前2字节是 above/below padding，跳过
		int pos = 0;
		if (data.Length >= 2)
		{
			pos = 2; // 跳过前2字节
		}

		while (pos < data.Length)
		{
				byte b = data[pos++];

				int length = (b >> 4) & 0x0F;
				int direction = b & 0x0F;

				if (length != 0)
				{
					// 普通方向绘制命令
					double dx = DirX[direction] * length;
					double dy = DirY[direction] * length;
					x += dx;
					y += dy;
					if (penDown)
					{
						if (currentStroke.Count == 0)
							currentStroke.Add(new Point(x - dx, y - dy));
						currentStroke.Add(new Point(x, y));
					}
					else
					{
						// 抬笔移动——结束当前笔画
						if (currentStroke.Count >= 2) strokes.Add(currentStroke);
						currentStroke = new List<Point>();
					}
				}
				else
				{
					// 特殊命令 (length == 0, direction 是操作码)
					switch (direction)
					{
						case 0: // END_OF_SHAPE
							if (currentStroke.Count >= 2) strokes.Add(currentStroke);
							currentStroke = new List<Point>();
							return strokes;

						case 1: // PEN_DOWN
							penDown = true;
							break;

						case 2: // PEN_UP
							if (currentStroke.Count >= 2) strokes.Add(currentStroke);
							currentStroke = new List<Point>();
							penDown = false;
							break;

						case 3: // DIVIDE_VECTOR
							if (pos < data.Length)
							{
								byte factor = data[pos++];
								if (factor > 0) scale /= factor;
							}
							break;

						case 4: // MULTIPLY_VECTOR
							if (pos < data.Length)
							{
								byte factor = data[pos++];
								if (factor > 0) scale *= factor;
							}
							break;

						case 5: // PUSH_STACK
							// 保存位置（简化：不做）
							break;

						case 6: // POP_STACK
							break;

						case 7: // DRAW_SUBSHAPE
							// 跳过子形状引用
							if (font.Type == "unifont" && pos + 2 <= data.Length) pos += 2;
							else if (pos < data.Length) pos += 1;
							break;

						case 8: // XY_DISPLACEMENT
							if (pos + 2 <= data.Length)
							{
								sbyte dx = (sbyte)data[pos++];
								sbyte dy = (sbyte)data[pos++];
								x += dx * scale;
								y += dy * scale;
								if (penDown)
								{
									if (currentStroke.Count == 0)
										currentStroke.Add(new Point(x - dx * scale, y - dy * scale));
									currentStroke.Add(new Point(x, y));
								}
							}
							break;

						case 9: // POLY_XY_DISPLACEMENT
							while (pos + 2 <= data.Length)
							{
								sbyte dx = (sbyte)data[pos++];
								sbyte dy = (sbyte)data[pos++];
								if (dx == 0 && dy == 0) break;
								x += dx * scale;
								y += dy * scale;
								if (penDown)
									currentStroke.Add(new Point(x, y));
							}
							break;

						case 0xA: // OCTANT_ARC
							if (pos + 2 <= data.Length)
							{
								byte radius = data[pos++];
								byte sc = data[pos++];
								// 简化：用直线近似弧
								int s = (sc >> 4) & 7;
								int c = sc & 7;
								if (c == 0) c = 8;
								bool ccw = ((sc >> 7) & 1) == 1;
								double r = radius * scale;
								// 弧的起点
								double startX = x - r * Math.Cos(s * Math.PI / 4);
								double startY = y - r * Math.Sin(s * Math.PI / 4);
								// 弧的终点
								double endAngle = (s + c) * Math.PI / 4;
								double endX = startX + r * Math.Cos(endAngle);
								double endY = startY + r * Math.Sin(endAngle);
								// 用多段直线近似弧
								int steps = Math.Max(c, 1) * 4;
								for (int i = 1; i <= steps; i++)
								{
									double a = (s + (double)i / steps * c) * Math.PI / 4;
									double px = startX + r * Math.Cos(a);
									double py = startY + r * Math.Sin(a);
									if (penDown)
										currentStroke.Add(new Point(px, py));
								}
								x = endX;
								y = endY;
							}
							break;

						case 0xB: // FRACTIONAL_ARC
							pos += 5; // 跳过5字节参数
							break;

						case 0xC: // BULGE_ARC
							if (pos + 3 <= data.Length)
							{
								sbyte dx = (sbyte)data[pos++];
								sbyte dy = (sbyte)data[pos++];
								sbyte bulge = (sbyte)data[pos++];
								// 用直线近似
								x += dx * scale;
								y += dy * scale;
								if (penDown)
									currentStroke.Add(new Point(x, y));
							}
							break;

						case 0xD: // POLY_BULGE_ARC
							while (pos + 3 <= data.Length)
							{
								sbyte dx = (sbyte)data[pos++];
								sbyte dy = (sbyte)data[pos++];
								if (dx == 0 && dy == 0) break;
								pos++; // 跳过bulge
								x += dx * scale;
								y += dy * scale;
								if (penDown)
									currentStroke.Add(new Point(x, y));
							}
							break;

						case 0xE: // COND_MODE_2
							break;
					}
				}
			}

			if (currentStroke.Count >= 2) strokes.Add(currentStroke);

			// 归一化坐标到0~1
			NormalizeStrokes(strokes, above);

			return strokes;
		}

		private void NormalizeStrokes(List<List<Point>> strokes, double above)
		{
			if (strokes.Count == 0) return;

			double minX = double.MaxValue, maxX = double.MinValue;
			double minY = double.MaxValue, maxY = double.MinValue;

			foreach (var s in strokes)
				foreach (var p in s)
				{
					if (p.X < minX) minX = p.X;
					if (p.X > maxX) maxX = p.X;
					if (p.Y < minY) minY = p.Y;
					if (p.Y > maxY) maxY = p.Y;
				}

			double w = maxX - minX;
			double h = maxY - minY;
			if (w < 0.001) w = 1;
			if (h < 0.001) h = above;

			// 归一化到0~1，Y翻转（SHX Y向上，WPF Y向下）
			foreach (var s in strokes)
				for (int i = 0; i < s.Count; i++)
				{
					s[i] = new Point(
						(s[i].X - minX) / w,
						1.0 - (s[i].Y - minY) / h);
				}
		}

		private List<List<Point>> ScaleStrokes(List<List<Point>> strokes, double fontSize)
		{
			if (strokes == null || strokes.Count == 0) return null;
			var result = new List<List<Point>>(strokes.Count);
			foreach (var s in strokes)
			{
				var scaled = new List<Point>(s.Count);
				foreach (var p in s)
					scaled.Add(new Point(p.X * fontSize, p.Y * fontSize));
				result.Add(scaled);
			}
			return result;
		}
}
}
