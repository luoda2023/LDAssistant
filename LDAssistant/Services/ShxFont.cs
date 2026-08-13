using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LDAssistant.Services
{
    /// <summary>SHX 字形折线点（double 坐标，避免与 WPF Point 混淆）。</summary>
    public struct P2
    {
        public double X, Y;
        public P2(double x, double y) { X = x; Y = y; }
        public P2 Add(P2 o) => new(X + o.X, Y + o.Y);
        public P2 Sub(P2 o) => new(X - o.X, Y - o.Y);
        public P2 Mul(double f) => new(X * f, Y * f);
    }

    /// <summary>解析出的单个字形：多段折线 + 推进宽度信息。</summary>
    public sealed class ShxShape
    {
        public List<List<P2>> Polylines = new();
        public P2 LastPoint;
        public bool HasExplicitAdvance;
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;

        public void RecomputeBBox()
        {
            MinX = double.MaxValue; MinY = double.MaxValue; MaxX = double.MinValue; MaxY = double.MinValue;
            foreach (var pl in Polylines)
                foreach (var p in pl)
                {
                    if (p.X < MinX) MinX = p.X;
                    if (p.X > MaxX) MaxX = p.X;
                    if (p.Y < MinY) MinY = p.Y;
                    if (p.Y > MaxY) MaxY = p.Y;
                }
            if (MinX == double.MaxValue) { MinX = MinY = MaxX = MaxY = 0; }
        }

        public ShxShape Clone()
        {
            var s = new ShxShape
            {
                LastPoint = LastPoint,
                HasExplicitAdvance = HasExplicitAdvance,
                MinX = MinX, MinY = MinY, MaxX = MaxX, MaxY = MaxY,
            };
            foreach (var pl in Polylines) s.Polylines.Add(new List<P2>(pl));
            return s;
        }

        public ShxShape Offset(double dx, double dy)
        {
            var s = new ShxShape { LastPoint = new P2(LastPoint.X + dx, LastPoint.Y + dy), HasExplicitAdvance = HasExplicitAdvance };
            foreach (var pl in Polylines) s.Polylines.Add(pl.Select(p => new P2(p.X + dx, p.Y + dy)).ToList());
            s.RecomputeBBox();
            return s;
        }

        public ShxShape Scale(double f)
        {
            var s = new ShxShape { LastPoint = LastPoint.Mul(f), HasExplicitAdvance = HasExplicitAdvance };
            foreach (var pl in Polylines) s.Polylines.Add(pl.Select(p => p.Mul(f)).ToList());
            s.RecomputeBBox();
            return s;
        }

        public ShxShape NormalizeToOrigin()
        {
            RecomputeBBox();
            return Offset(-MinX, -MinY);
        }

        public bool HasInk => Polylines.Any(pl => pl.Count >= 2);
    }

    /// <summary>
    /// AutoCAD SHX 字体解析器（bigfont / unifont / shapes 三种格式）。
    /// 把字形字节码解析为折线矢量，供 WPF 细线渲染。逻辑对照 mlightcad/shx-parser。
    /// </summary>
    public sealed class ShxFont
    {
        public string FontType;          // "bigfont" | "unifont" | "shapes"
        public string Info;
        public string Orientation = "horizontal";
        public bool DualOrientation;
        public double Height = 10, Width = 10, BaseUp = 8, BaseDown = 2;
        public bool IsExtended;
        public bool VerticalDualMode;

        private readonly Dictionary<int, byte[]> _data = new();
        private readonly Dictionary<int, ShxShape> _shapeCache = new();
        private readonly Dictionary<int, ShxShape> _subCache = new();
        private double? _baselinePadding;

        private static readonly int[] BaselineSampleCodes =
        {
            0xcbc4, 0xb2e3, 0xc2a5, 0xc3e6, 0xd6d0, 0xb9fa, 0xd5e2, 0xb5c4, 0xcac2, 0xd2bb,
            0xc0b4, 0xc9fa, 0xd3d0, 0xced2, 0xcdea, 0xcbfb, 0xb2bb, 0xc8cb, 0xb5c4,
            0xd2bb, 0xc4ea, 0xcbfb, 0xcbad, 0xb5c4, 0xc3e6, 0xc7b0, 0xc3e6, 0xd6d0,
            0xc9cf, 0xc3c7, 0xcfc2, 0xb5c4, 0xb5bd, 0xc8a5, 0xcbb5, 0xb7a8, 0xb5c4, 0xcab1,
            0xc9fa, 0xb3c9, 0xb7bd, 0xd6f7, 0xbbfa, 0xc6f7, 0xb9ab, 0xbbfa, 0xcafd, 0xd6d8,
        };

        public bool HasChar(int code) => _data.ContainsKey(code);

        // 中文大字体字形按 GB2312 内码存储；图纸文字是 Unicode，查询时转换。
        // 惰性初始化 + 防御：936 代码页依赖 CodePagesEncodingProvider（App 启动时注册），
        // 万一注册失败也绝不能让渲染崩溃——返回 null 由上层回退到普通字体。
        private static Encoding _gb;
        private static Encoding Gb
        {
            get
            {
                if (_gb != null) return _gb;
                try { _gb = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
                catch { _gb = null; }
                return _gb;
            }
        }

        /// <summary>Unicode 字符 → 大字体内码（GB2312），非 GB2312 字符返回 false。</summary>
        public bool HasCharUnicode(char ch)
        {
            if (ch <= 0xff) return HasChar(ch);
            try
            {
                var b = Gb.GetBytes(ch.ToString());
                return b.Length == 2 && HasChar((b[0] << 8) | b[1]);
            }
            catch { return false; }
        }

        /// <summary>按 Unicode 字符取布局字形（内部转 GB2312 内码）。</summary>
        public ShxShape GetLayoutCharShapeUnicode(char ch, double size)
        {
            if (ch <= 0xff) return GetLayoutCharShape(ch, size);
            try
            {
                var b = Gb.GetBytes(ch.ToString());
                if (b.Length != 2) return null;
                return GetLayoutCharShape((b[0] << 8) | b[1], size);
            }
            catch { return null; }
        }

        /// <summary>加载 SHX 文件（bigfont/unifont/shapes 自动识别）。</summary>
        public static ShxFont Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            int pos = 0;
            var sb = new StringBuilder();
            while (pos < bytes.Length - 2)
            {
                int b = bytes[pos];
                if (b == 0x0d && bytes[pos + 1] == 0x0a && bytes[pos + 2] == 0x1a)
                {
                    pos += 3;
                    break;
                }
                sb.Append((char)b);
                pos++;
            }
            var headerParts = sb.ToString().Trim().Split(' ');
            if (headerParts.Length < 3)
                throw new InvalidDataException("SHX 头格式无效: " + path);
            var font = new ShxFont
            {
                FontType = headerParts[1].ToLowerInvariant(),
            };

            if (font.FontType == "bigfont")
            {
                ParseBigfontContent(font, bytes, pos);
            }
            else if (font.FontType == "unifont")
            {
                ParseUnifontContent(font, bytes, pos);
            }
            else if (font.FontType == "shapes")
            {
                ParseShapesContent(font, bytes, pos);
            }
            else
            {
                throw new InvalidDataException("不支持的 SHX 类型: " + font.FontType);
            }
            return font;
        }

        // ═══════════ content 段解析 ═══════════

        private static void ParseBigfontContent(ShxFont font, byte[] b, int p)
        {
            int pos = p;
            ReadI16(b, ref pos);                      // item length（跳过）
            int count = ReadI16(b, ref pos);
            int changeNumber = ReadI16(b, ref pos);
            pos += changeNumber * 4;                  // change table

            var items = new List<(int code, int len, int off)>();
            for (int i = 0; i < count; i++)
            {
                int code = ReadU16(b, ref pos);
                int len = ReadU16(b, ref pos);
                int off = ReadI32(b, ref pos);
                if (code != 0 || len != 0 || off != 0)
                    items.Add((code, len, off));
            }
            foreach (var it in items)
            {
                if (it.off < 0 || it.off + it.len > b.Length) continue;
                var data = new byte[it.len];
                Array.Copy(b, it.off, data, 0, it.len);
                font._data[it.code] = data;
            }

            if (font._data.TryGetValue(0, out var info))
            {
                font.ParseBigfontInfo(info);
            }
        }

        private void ParseBigfontInfo(byte[] info)
        {
            // info 文本以 0x00/0x0d/0x0a 结束，其后是度量字节
            int term = 0;
            while (term < info.Length && info[term] != 0 && info[term] != 0x0d && info[term] != 0x0a) term++;
            Info = Encoding.UTF8.GetString(info, 0, term).TrimEnd('\0');
            int index = term + 1;
            while (index < info.Length && info[index] == 0) index++;
            int remaining = info.Length - index;
            if (remaining <= 0) return;

            if (remaining >= 5)
            {
                // 扩展大字体：height, 0, modes, width, [0]
                double h = info[index++]; index++;
                Orientation = info[index++] == 0 ? "horizontal" : "vertical";
                double w = info[index++];
                Height = h; BaseUp = h; BaseDown = 0; Width = w; IsExtended = true;
            }
            else if (remaining == 4 && info[index + 1] == 0 && info[index + 3] > 0 && info[index + 3] != info[index])
            {
                double h = info[index++]; index++;
                Orientation = info[index++] == 0 ? "horizontal" : "vertical";
                double w = info[index];
                Height = h; BaseUp = h; BaseDown = 0; Width = w; IsExtended = true;
            }
            else if (remaining == 4)
            {
                double up = info[index++];
                double down = info[index++];
                Orientation = info[index++] == 0 ? "horizontal" : "vertical";
                BaseUp = up; BaseDown = down; Height = up + down; Width = up + down;
            }
            else if (remaining == 3)
            {
                double up = info[index++];
                int modes = info[index++];
                Orientation = modes == 0 ? "horizontal" : "vertical";
                VerticalDualMode = modes == 2;
                BaseUp = up; BaseDown = 0; Height = up; Width = up;
                IsExtended = VerticalDualMode;
            }
        }

        private static void ParseUnifontContent(ShxFont font, byte[] b, int p)
        {
            int pos = p;
            int count = ReadI32(b, ref pos);
            int infoLen = ReadI16(b, ref pos);
            if (infoLen < 0 || pos + infoLen > b.Length) infoLen = 0;
            var info = new byte[infoLen];
            Array.Copy(b, pos, info, 0, infoLen);
            pos += infoLen;

            int term = 0;
            while (term < info.Length && info[term] != 0 && info[term] != 0x0d && info[term] != 0x0a) term++;
            font.Info = Encoding.UTF8.GetString(info, 0, term).TrimEnd('\0');
            if (term + 3 < info.Length)
            {
                font.BaseUp = info[term + 1];
                font.BaseDown = info[term + 2];
                font.Height = font.BaseUp + font.BaseDown;
                font.Width = font.Height;
                int modes = info[term + 3];
                if (modes == 0) font.Orientation = "horizontal";
                else if (modes == 2) { font.Orientation = "horizontal"; font.DualOrientation = true; }
                else font.Orientation = "vertical";
            }

            for (int i = 0; i < count - 1 && pos + 4 <= b.Length; i++)
            {
                int code = ReadU16(b, ref pos);
                int len = ReadU16(b, ref pos);
                if (len <= 0 || pos + len > b.Length) { continue; }
                var raw = new byte[len];
                Array.Copy(b, pos, raw, 0, len);
                pos += len;
                int nul = Array.IndexOf(raw, (byte)0);
                var bytecode = nul >= 0 ? raw.Skip(nul + 1).ToArray() : raw;
                if (bytecode.Length > 0)
                    font._data[code] = bytecode;
            }
        }

        private static void ParseShapesContent(ShxFont font, byte[] b, int p)
        {
            int pos = p + 4;                          // 跳过 start/end codes
            int count = ReadI16(b, ref pos);
            var items = new List<(int code, int len)>();
            for (int i = 0; i < count; i++)
            {
                int code = ReadU16(b, ref pos);
                int len = ReadU16(b, ref pos);
                if (len > 0) items.Add((code, len));
            }
            foreach (var it in items)
            {
                if (pos + it.len > b.Length) continue;
                var raw = new byte[it.len];
                Array.Copy(b, pos, raw, 0, it.len);
                pos += it.len;
                if (it.code == 0)
                {
                    font._data[0] = raw;
                    continue;
                }
                int nul = Array.IndexOf(raw, (byte)0);
                var bytecode = nul >= 0 ? raw.Skip(nul + 1).ToArray() : raw;
                font._data[it.code] = bytecode;
            }
            if (font._data.TryGetValue(0, out var info0))
            {
                int term = 0;
                while (term < info0.Length && info0[term] != 0 && info0[term] != 0x0d && info0[term] != 0x0a) term++;
                font.Info = Encoding.UTF8.GetString(info0, 0, term).TrimEnd('\0');
                if (term + 3 < info0.Length)
                {
                    font.BaseUp = info0[term + 1];
                    font.BaseDown = info0[term + 2];
                    font.Height = font.BaseUp + font.BaseDown;
                    font.Width = font.Height;
                    int modes = info0[term + 3];
                    if (modes == 0) font.Orientation = "horizontal";
                    else if (modes == 2) { font.Orientation = "horizontal"; font.DualOrientation = true; }
                    else font.Orientation = "vertical";
                }
            }
        }

        // ═══════════ 字形解析 ═══════════

        /// <summary>取字符字形（按设计单位缩放：size = 目标字高，缩放 = size/Height）。</summary>
        public ShxShape GetCharShape(int code, double size)
        {
            if (code == 0) return null;
            if (!_data.TryGetValue(code, out var data)) return null;

            if (!_shapeCache.TryGetValue(code, out var baseShape))
            {
                try
                {
                    byte[] bytes = data;
                    if (FontType == "bigfont" && code > 0xff)
                    {
                        bytes = StripBigfontPrefix(code, data);
                    }
                    var state = ParseShape(bytes, FontType != "bigfont", true);
                    baseShape = state;
                }
                catch
                {
                    baseShape = null;
                }
                _shapeCache[code] = baseShape;
            }

            if (baseShape == null) return null;
            double scale = Height > 0 ? size / Height : 1.0;
            return baseShape.Scale(scale);
        }

        private static byte[] StripBigfontPrefix(int code, byte[] data)
        {
            int hi = (code >> 8) & 0xff, lo = code & 0xff;
            // SHX bigfont 字形 data 通常以 code 的两字节前缀开头。
            // 实测 HZTXT/gbcbig/tssdchn 等中文大字体均为小端存 code（前缀 [lo, hi]），
            // 原代码只检查大端 [hi, lo]，导致前缀未剥除 → ParseShape 把前缀字节当指令，
            // 画出错误笔画（"乱码字形"）。这里同时识别 LE/BE 两种前缀以兼容。
            int start = -1;
            if (data.Length >= 2 && data[0] == lo && data[1] == hi) start = 2;   // LE
            else if (data.Length >= 2 && data[0] == hi && data[1] == lo) start = 2; // BE
            if (start >= 0)
            {
                if (start < data.Length && data[start] == 0) start++;
                return data.Skip(start).ToArray();
            }
            return data;
        }

        /// <summary>布局对齐后的字形（基线 y=0、宽度按 advance 策略）。</summary>
        public ShxShape GetLayoutCharShape(int code, double size)
        {
            var raw = GetCharShape(code, size);
            if (raw == null) return null;

            var shape = raw;
            double scale = Height > 0 ? size / Height : 1.0;

            if (FontType == "bigfont")
            {
                double pad = GetBaselineInkPaddingNative();
                if (pad > 0) shape = shape.Offset(0, -pad * scale);
            }
            else if (FontType == "unifont")
            {
                bool baselineOrigin = UsesBaselineOrigin(raw, scale);
                if (!baselineOrigin && !DualOrientation)
                {
                    double cap = BaseUp * scale;
                    shape = shape.Offset(0, cap);
                }
            }

            double advance = ResolveAdvance(shape, size);
            return new ShxShape
            {
                Polylines = shape.Polylines,
                LastPoint = new P2(advance, shape.LastPoint.Y),
                HasExplicitAdvance = true,
            };
        }

        private double ResolveAdvance(ShxShape shape, double size)
        {
            double cellWidth = (Width > 0 ? Width : Height) * (Height > 0 ? size / Height : 1.0);
            double advanceX = shape.LastPoint.X;
            if (shape.HasExplicitAdvance) return advanceX;
            if (!shape.HasInk && Math.Abs(advanceX) > 1e-6) return advanceX;
            // 墨迹宽度 + 0.2 字宽（比例间距）
            shape.RecomputeBBox();
            double pad = cellWidth * 0.2;
            if (shape.MinX < -1e-6)
                return Math.Max(shape.MaxX, cellWidth / 2) + pad;
            return shape.MaxX + pad;
        }

        private bool UsesBaselineOrigin(ShxShape shape, double scale)
        {
            if (DualOrientation) return true;
            double desc = BaseDown * scale;
            double cap = BaseUp * scale;
            double threshold = -(desc + cap * 0.05);
            shape.RecomputeBBox();
            if (shape.MinY < threshold) return false;
            double inkHeight = shape.MaxY - shape.MinY;
            return inkHeight >= cap * 0.05;
        }

        /// <summary>bigfont 无 descender 时采样常用汉字底部内边距（中位数）。</summary>
        private double GetBaselineInkPaddingNative()
        {
            if (_baselinePadding.HasValue) return _baselinePadding.Value;
            double padding = 0;
            if (FontType == "bigfont" && BaseDown <= 0 && Height > 0)
            {
                double maxBody = Height * 0.4;
                var samples = new List<double>();
                var seen = new HashSet<int>();
                foreach (var c in BaselineSampleCodes)
                {
                    if (seen.Add(c) && c > 0xff && _data.ContainsKey(c))
                    {
                        var raw = GetCharShape(c, Height);
                        if (raw != null)
                        {
                            raw.RecomputeBBox();
                            if (raw.MinY > 0 && raw.MinY <= maxBody) samples.Add(raw.MinY);
                        }
                    }
                    if (samples.Count >= 48) break;
                }
                if (samples.Count < 8)
                {
                    foreach (var kv in _data)
                    {
                        if (seen.Contains(kv.Key) || kv.Key <= 0xff) continue;
                        seen.Add(kv.Key);
                        var raw = GetCharShape(kv.Key, Height);
                        if (raw != null)
                        {
                            raw.RecomputeBBox();
                            if (raw.MinY > 0 && raw.MinY <= maxBody) samples.Add(raw.MinY);
                        }
                        if (samples.Count >= 48) break;
                    }
                }
                if (samples.Count >= 8)
                {
                    samples.Sort();
                    int mid = samples.Count / 2;
                    padding = samples.Count % 2 == 0 ? (samples[mid - 1] + samples[mid]) / 2 : samples[mid];
                }
            }
            _baselinePadding = padding;
            return padding;
        }

        // ═══════════ 字节码状态机 ═══════════

        private sealed class ParseState
        {
            public P2 Current;
            public readonly List<List<P2>> Polylines = new();
            public List<P2> CurrentPolyline = new();
            public readonly List<P2> Stack = new();
            public bool PenDown;
            public double Scale = 1.0;
            public bool FlushOnEnd;
            public bool HasExplicitAdvance;
            public bool PendingTerminalAdvance;
        }

        private ShxShape ParseShape(byte[] data, bool initialPenDown, bool flushOnEnd)
        {
            var st = new ParseState
            {
                PenDown = initialPenDown,
                FlushOnEnd = flushOnEnd,
            };
            if (st.PenDown) st.CurrentPolyline.Add(st.Current);

            for (int i = 0; i < data.Length; i++)
            {
                int cb = data[i];
                if (cb <= 0x0f)
                    i = HandleSpecial(cb, data, i, st);
                else
                {
                    ClearPendingAdvance(st);
                    HandleVector(cb, st);
                }
            }
            FinalizeAdvance(st);

            var shape = new ShxShape { LastPoint = st.Current, HasExplicitAdvance = st.HasExplicitAdvance };
            foreach (var pl in st.Polylines) shape.Polylines.Add(new List<P2>(pl));
            if (st.CurrentPolyline.Count > 1) shape.Polylines.Add(new List<P2>(st.CurrentPolyline));
            shape.RecomputeBBox();
            return shape;
        }

        private int HandleSpecial(int cmd, byte[] data, int index, ParseState st)
        {
            int i = index;
            switch (cmd)
            {
                case 0: // End of shape
                    FinalizeAdvance(st);
                    if (st.FlushOnEnd && st.CurrentPolyline.Count > 1)
                    {
                        st.Polylines.Add(st.CurrentPolyline);
                        st.CurrentPolyline = new List<P2>();
                    }
                    else if (st.FlushOnEnd)
                    {
                        st.CurrentPolyline = new List<P2>();
                    }
                    st.PenDown = false;
                    break;
                case 1: // Draw mode on
                    ClearPendingAdvance(st);
                    if (!st.PenDown) st.CurrentPolyline.Add(st.Current);
                    st.PenDown = true;
                    break;
                case 2: // Draw mode off
                    st.PenDown = false;
                    if (st.CurrentPolyline.Count > 1) st.Polylines.Add(st.CurrentPolyline);
                    st.CurrentPolyline = new List<P2>();
                    break;
                case 3: // Divide vector lengths
                    ClearPendingAdvance(st);
                    i++;
                    if (i < data.Length && data[i] != 0) st.Scale /= data[i];
                    break;
                case 4: // Multiply vector lengths
                    ClearPendingAdvance(st);
                    i++;
                    if (i < data.Length) st.Scale *= data[i];
                    break;
                case 5: // Push location
                    ClearPendingAdvance(st);
                    if (st.Stack.Count < 4) st.Stack.Add(st.Current);
                    break;
                case 6: // Pop location
                    ClearPendingAdvance(st);
                    if (st.Stack.Count > 0)
                    {
                        st.Current = st.Stack[st.Stack.Count - 1];
                        st.Stack.RemoveAt(st.Stack.Count - 1);
                    }
                    if (st.CurrentPolyline.Count > 1) st.Polylines.Add(st.CurrentPolyline);
                    st.CurrentPolyline = new List<P2>();
                    if (st.PenDown) st.CurrentPolyline.Add(st.Current);
                    break;
                case 7: // Draw subshape
                    ClearPendingAdvance(st);
                    i = HandleSubshape(data, i, st);
                    break;
                case 8: // XY displacement
                    i = HandleXY(data, i, st);
                    break;
                case 9: // Multiple XY displacements
                    i = HandleMultiXY(data, i, st);
                    break;
                case 10: // Octant arc
                    ClearPendingAdvance(st);
                    i = HandleOctantArc(data, i, st);
                    break;
                case 11: // Fractional arc
                    ClearPendingAdvance(st);
                    i = HandleFractionalArc(data, i, st);
                    break;
                case 12: // Bulge arc
                    ClearPendingAdvance(st);
                    i = HandleBulgeArc(data, i, st);
                    break;
                case 13: // Multiple bulge arcs
                    ClearPendingAdvance(st);
                    i = HandleMultiBulgeArc(data, i, st);
                    break;
                case 14: // Orientation flag：水平布局跳过下一命令（bigfont 非 verticalDual 总是跳过）
                    ClearPendingAdvance(st);
                    if (!VerticalDualMode)
                        i = SkipCode(data, ++i);
                    break;
            }
            return i;
        }

        private void HandleVector(int cb, ParseState st)
        {
            int len = (cb & 0xf0) >> 4;
            int dir = cb & 0x0f;
            var v = DirectionVector(dir);
            st.Current = st.Current.Add(v.Mul(len * st.Scale));
            if (st.PenDown) st.CurrentPolyline.Add(st.Current);
        }

        private static P2 DirectionVector(int dir)
        {
            switch (dir)
            {
                case 0: return new P2(1, 0);
                case 1: return new P2(1, 0.5);
                case 2: return new P2(1, 1);
                case 3: return new P2(0.5, 1);
                case 4: return new P2(0, 1);
                case 5: return new P2(-0.5, 1);
                case 6: return new P2(-1, 1);
                case 7: return new P2(-1, 0.5);
                case 8: return new P2(-1, 0);
                case 9: return new P2(-1, -0.5);
                case 10: return new P2(-1, -1);
                case 11: return new P2(-0.5, -1);
                case 12: return new P2(0, -1);
                case 13: return new P2(0.5, -1);
                case 14: return new P2(1, -1);
                default: return new P2(1, -0.5);
            }
        }

        private int HandleSubshape(byte[] data, int index, ParseState st)
        {
            int i = index;
            int subCode = 0;
            var origin = st.Current;
            double height = st.Scale * BaseUp;
            double width = height;

            if (st.CurrentPolyline.Count > 1)
            {
                st.Polylines.Add(st.CurrentPolyline);
                st.CurrentPolyline = new List<P2>();
            }

            if (FontType == "bigfont")
            {
                i++;
                if (i >= data.Length) return i;
                subCode = data[i];
                if (subCode == 0)
                {
                    i++;
                    if (i + 1 >= data.Length) return i;
                    subCode = (data[i++] << 8) | data[i++];
                    if (i < data.Length) origin = new P2(origin.X + SByte(data[i++]) * st.Scale, origin.Y);
                    if (i < data.Length) origin = new P2(origin.X, origin.Y + SByte(data[i++]) * st.Scale);
                    if (IsExtended)
                    {
                        if (i < data.Length) width = data[i++] * st.Scale;
                        if (i < data.Length) height = data[i] * st.Scale;
                    }
                    else
                    {
                        if (i < data.Length) { height = data[i] * st.Scale; width = height; }
                    }
                }
            }
            else if (FontType == "unifont")
            {
                i++;
                if (i + 1 >= data.Length) return i;
                subCode = (data[i++] << 8) | data[i++];
                i--;
            }
            else
            {
                i++;
                if (i < data.Length) subCode = data[i];
            }

            if (subCode != 0)
            {
                var sub = GetScaledSubshape(subCode, width, height, origin);
                if (sub != null)
                {
                    st.Polylines.AddRange(sub.Polylines);
                    if (FontType == "bigfont")
                    {
                        if (subCode == 2 && sub.LastPoint.X != 0)
                        {
                            double cellWidth = sub.LastPoint.X - origin.X;
                            if (cellWidth > st.Current.X) st.Current = new P2(cellWidth, st.Current.Y);
                            st.HasExplicitAdvance = true;
                        }
                    }
                    else
                    {
                        st.Current = sub.LastPoint;
                    }
                    st.CurrentPolyline = new List<P2>();
                    if (st.PenDown) st.CurrentPolyline.Add(st.Current);
                }
            }
            return i;
        }

        private ShxShape GetScaledSubshape(int code, double width, double height, P2 origin)
        {
            if (!_data.TryGetValue(code, out var data)) return null;
            if (!_subCache.TryGetValue(code, out var baseShape))
            {
                baseShape = ParseShape(data, FontType != "bigfont", false);
                _subCache[code] = baseShape;
            }
            ShxShape scaled;
            if (FontType == "bigfont")
            {
                var target = baseShape.HasInk ? baseShape.NormalizeToOrigin() : baseShape;
                scaled = ScaleTo(target, height, width);
            }
            else
            {
                double f = BaseUp > 0 ? height / BaseUp : 1.0;
                scaled = baseShape.Scale(f);
            }
            return scaled.Offset(origin.X, origin.Y);
        }

        private static ShxShape ScaleTo(ShxShape shape, double height, double width)
        {
            shape.RecomputeBBox();
            double sh = shape.MaxY - shape.MinY;
            double sw = shape.MaxX - shape.MinX;
            double hs = sh > 0 ? height / sh : 1.0;
            double ws = sw > 0 ? width / sw : 1.0;
            var s = new ShxShape
            {
                LastPoint = new P2(shape.LastPoint.X * ws, shape.LastPoint.Y * hs),
                HasExplicitAdvance = shape.HasExplicitAdvance,
            };
            foreach (var pl in shape.Polylines)
                s.Polylines.Add(pl.Select(p => new P2(p.X * ws, p.Y * hs)).ToList());
            s.RecomputeBBox();
            return s;
        }

        private int HandleXY(byte[] data, int index, ParseState st)
        {
            int i = index;
            double x = 0, y = 0;
            if (i + 1 < data.Length) x = SByte(data[++i]);
            if (i + 1 < data.Length) y = SByte(data[++i]);
            st.Current = st.Current.Add(new P2(x, y).Mul(st.Scale));
            if (st.PenDown) st.CurrentPolyline.Add(st.Current);
            else NotePenUpPositioning(st);
            return i;
        }

        private int HandleMultiXY(byte[] data, int index, ParseState st)
        {
            int i = index;
            while (true)
            {
                if (i + 1 >= data.Length) break;
                double x = SByte(data[++i]);
                double y = SByte(data[++i]);
                if (x == 0 && y == 0) break;
                st.Current = st.Current.Add(new P2(x, y).Mul(st.Scale));
                if (st.PenDown) st.CurrentPolyline.Add(st.Current);
                else NotePenUpPositioning(st);
            }
            return i;
        }

        private int HandleOctantArc(byte[] data, int index, ParseState st)
        {
            int i = index;
            double radius = 0;
            if (i + 1 < data.Length) radius = data[++i] * st.Scale;
            int flag = 0;
            if (i + 1 < data.Length) flag = SByte(data[++i]);
            int startOctant = (flag & 0x70) >> 4;
            int octantCount = flag & 0x07;
            bool clockwise = flag < 0;
            double startRadian = (Math.PI / 4) * startOctant;
            var center = st.Current.Sub(new P2(Math.Cos(startRadian) * radius, Math.Sin(startRadian) * radius));
            var pts = TessellateOctant(center, radius, startOctant, octantCount, clockwise);
            if (pts.Count > 0)
            {
                if (st.PenDown && st.CurrentPolyline.Count > 0)
                {
                    st.CurrentPolyline.RemoveAt(st.CurrentPolyline.Count - 1);
                    st.CurrentPolyline.AddRange(pts);
                }
                st.Current = pts[pts.Count - 1];
            }
            return i;
        }

        private static List<P2> TessellateOctant(P2 center, double radius, int startOctant, int octantCount, bool clockwise)
        {
            var pts = new List<P2>();
            if (radius <= 0) return pts;
            int count = octantCount;
            double a0 = (Math.PI / 4) * startOctant;
            double sweep = (Math.PI / 4) * count * (clockwise ? -1 : 1);
            double step = Math.PI / 18;
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / step));
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + sweep * i / n;
                pts.Add(center.Add(new P2(Math.Cos(a) * radius, Math.Sin(a) * radius)));
            }
            return pts;
        }

        private int HandleFractionalArc(byte[] data, int index, ParseState st)
        {
            int i = index;
            int startOffset = 0, endOffset = 0, hr = 0, lr = 0, flag = 0;
            if (i + 1 < data.Length) startOffset = data[++i];
            if (i + 1 < data.Length) endOffset = data[++i];
            if (i + 1 < data.Length) hr = data[++i];
            if (i + 1 < data.Length) lr = data[++i];
            if (i + 1 < data.Length) flag = SByte(data[++i]);
            double r = (hr * 255 + lr) * st.Scale;
            int n1 = (flag & 0x70) >> 4;
            int n2 = flag & 0x07;
            if (n2 == 0) n2 = 8;
            if (endOffset != 0) n2--;
            double pi4 = Math.PI / 4;
            double span = pi4 * n2;
            double delta = Math.PI / 18;
            double sign = 1;
            if (flag < 0) { delta = -delta; span = -span; sign = -1; }
            double startRadian = pi4 * n1;
            double endRadian = startRadian + span;
            startRadian += ((pi4 * startOffset) / 256) * sign;
            endRadian += ((pi4 * endOffset) / 256) * sign;

            var center = st.Current.Sub(new P2(r * Math.Cos(startRadian), r * Math.Sin(startRadian)));
            var end = center.Add(new P2(r * Math.Cos(endRadian), r * Math.Sin(endRadian)));
            st.Current = end;

            if (st.PenDown)
            {
                var pts = new List<P2> { center.Add(new P2(r * Math.Cos(startRadian), r * Math.Sin(startRadian))) };
                double cur = startRadian;
                if (delta > 0)
                {
                    while (cur + delta < endRadian)
                    {
                        cur += delta;
                        pts.Add(center.Add(new P2(r * Math.Cos(cur), r * Math.Sin(cur))));
                    }
                }
                else
                {
                    while (cur + delta > endRadian)
                    {
                        cur += delta;
                        pts.Add(center.Add(new P2(r * Math.Cos(cur), r * Math.Sin(cur))));
                    }
                }
                pts.Add(end);
                st.CurrentPolyline.AddRange(pts);
            }
            return i;
        }

        private int HandleBulgeArc(byte[] data, int index, ParseState st)
        {
            int i = index;
            double x = 0, y = 0, bulge = 0;
            if (i + 1 < data.Length) x = SByte(data[++i]);
            if (i + 1 < data.Length) y = SByte(data[++i]);
            if (i + 1 < data.Length) bulge = SByte(data[++i]);
            st.Current = ArcSegment(st.Current, new P2(x, y), bulge, st.Scale, st.PenDown, st.CurrentPolyline);
            return i;
        }

        private int HandleMultiBulgeArc(byte[] data, int index, ParseState st)
        {
            int i = index;
            while (true)
            {
                if (i + 1 >= data.Length) break;
                double x = SByte(data[++i]);
                double y = SByte(data[++i]);
                if (x == 0 && y == 0) break;
                if (i + 1 >= data.Length) break;
                double bulge = SByte(data[++i]);
                st.Current = ArcSegment(st.Current, new P2(x, y), bulge, st.Scale, st.PenDown, st.CurrentPolyline);
            }
            return i;
        }

        private static P2 ArcSegment(P2 current, P2 vec, double bulge, double scale, bool penDown, List<P2> polyline)
        {
            vec = vec.Mul(scale);
            if (bulge < -127) bulge = -127;
            var end = current.Add(vec);
            if (penDown)
            {
                if (bulge == 0)
                {
                    polyline.Add(end);
                }
                else
                {
                    var pts = TessellateBulge(current, end, bulge / 127.0);
                    if (pts.Count > 1)
                    {
                        for (int i = 1; i < pts.Count; i++) polyline.Add(pts[i]);
                    }
                }
            }
            return end;
        }

        private static List<P2> TessellateBulge(P2 start, P2 end, double bulge)
        {
            var pts = new List<P2>();
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-9) { pts.Add(end); return pts; }
            double sagitta = chord / 2 * Math.Abs(bulge);
            double radius = (chord / 2) * (chord / 2) / (2 * sagitta) + sagitta / 2;
            double midX = (start.X + end.X) / 2, midY = (start.Y + end.Y) / 2;
            double nx = -dy / chord, ny = dx / chord;
            double dir = bulge >= 0 ? 1 : -1;
            var center = new P2(midX + nx * sagitta * dir, midY + ny * sagitta * dir);
            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
            double sweep = a1 - a0;
            while (sweep * dir < 0) sweep += 2 * Math.PI * dir;
            double step = Math.PI / 18;
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / step));
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + sweep * i / n;
                pts.Add(new P2(center.X + Math.Cos(a) * radius, center.Y + Math.Sin(a) * radius));
            }
            return pts;
        }

        private int SkipCode(byte[] data, int index)
        {
            if (index >= data.Length) return index;
            int cb = data[index];
            switch (cb)
            {
                case 0x03:
                case 0x04: return index + 1;
                case 0x07:
                    if (FontType == "bigfont")
                    {
                        if (index + 1 >= data.Length) return index;
                        index++;
                        int sub = data[index];
                        if (sub == 0) return index + (IsExtended ? 6 : 5);
                        return index;
                    }
                    if (FontType == "unifont") return index + 2;
                    return index + 1;
                case 0x08: return index + 2;
                case 0x09:
                {
                    int j = index;
                    while (true)
                    {
                        j++;
                        if (j + 1 >= data.Length) break;
                        int x = data[j++];
                        int y = data[j];
                        if (x == 0 && y == 0) break;
                    }
                    return j;
                }
                case 0x0a: return index + 2;
                case 0x0b: return index + 5;
                case 0x0c: return index + 3;
                case 0x0d:
                {
                    int j = index;
                    while (true)
                    {
                        j++;
                        if (j + 1 >= data.Length) break;
                        int x = data[j++];
                        int y = data[j];
                        if (x == 0 && y == 0) break;
                        j++;
                    }
                    return j;
                }
                default: return index;
            }
        }

        private void FinalizeAdvance(ParseState st)
        {
            if (!st.PendingTerminalAdvance) return;
            if (Math.Abs(st.Current.X) > 1e-6)
            {
                st.HasExplicitAdvance = true;
                return;
            }
            if (!StateHasInk(st)) st.HasExplicitAdvance = true;
        }

        private static bool StateHasInk(ParseState st)
        {
            if (st.CurrentPolyline.Count > 1) return true;
            return st.Polylines.Any(pl => pl.Count >= 2);
        }

        private static void NotePenUpPositioning(ParseState st) => st.PendingTerminalAdvance = true;
        private static void ClearPendingAdvance(ParseState st) => st.PendingTerminalAdvance = false;

        // ═══════════ 二进制读取 ═══════════

        private static int ReadI16(byte[] b, ref int p) { int v = (short)(b[p] | (b[p + 1] << 8)); p += 2; return v; }
        private static int ReadU16(byte[] b, ref int p) { int v = b[p] | (b[p + 1] << 8); p += 2; return v; }
        private static int ReadI32(byte[] b, ref int p) { int v = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24); p += 4; return v; }
        private static int SByte(byte b) => b >= 128 ? b - 256 : b;
    }
}
