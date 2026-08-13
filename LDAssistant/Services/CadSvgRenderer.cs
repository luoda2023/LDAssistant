using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using System.Globalization;
using System.Text;

namespace LDAssistant.Services
{
    /// <summary>
    /// 矢量 CAD 渲染器：把 DWG 实体转成 SVG（而非栅格位图）。
    /// 描边按 颜色|线宽|线型 合并到少数 path，DOM 节点极少，缩放/平移流畅，
    /// 输出的 viewBox 完整承载整张图纸，可无限缩放，最接近官方 CAD 显示效果。
    /// </summary>
    public static class CadSvgRenderer
    {
        public class SvgResult
        {
            public string Svg;
            public int PrimitiveCount;
            public double MinX, MinY, MaxX, MaxY;
            public string Error;
            public bool Success => Error == null;
        }

        const int MaxDepth = 12;

        // 按 颜色|线宽|线型 归并的描边桶
        private sealed class Bucket
        {
            public string Color;
            public string Width;
            public string Dash;
            public readonly StringBuilder D = new StringBuilder();
        }

        // 输出一个描边桶中的所有 path（按 颜色|线宽|线型 合并，极大减少 DOM 节点）
        private static void FlushBuckets(StringBuilder sb, Dictionary<string, Bucket> buckets)
        {
            foreach (var kv in buckets)
            {
                var b = kv.Value;
                if (b.D.Length == 0) continue;
                sb.Append($"<path class='s' stroke='{b.Color}' stroke-width='{b.Width}'{b.Dash} d='{b.D}'/>");
            }
        }

        public static SvgResult Render(IList<Entity> entities, bool darkBackground = true)
        {
            var res = new SvgResult();
            if (entities == null || entities.Count == 0) { res.Error = "空实体"; return res; }
            try
            {
                var pts = new List<(double x, double y)>();
                int cnt = 0;
                Walk(entities, pts, ref cnt, Identity(), 0);
                if (pts.Count == 0) { res.Error = "无几何"; return res; }

                double minX = pts.Min(p => p.x), minY = pts.Min(p => p.y);
                double maxX = pts.Max(p => p.x), maxY = pts.Max(p => p.y);
                if (maxX - minX < 1e-6) { minX -= 1; maxX += 1; }
                if (maxY - minY < 1e-6) { minY -= 1; maxY += 1; }

                double pad = Math.Max(maxX - minX, maxY - minY) * 0.02 + 1;
                double vbX = minX - pad;
                double vbY = -(maxY + pad);
                double vbW = (maxX - minX) + 2 * pad;
                double vbH = (maxY - minY) + 2 * pad;

 var sb = new StringBuilder();
 sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{F(vbX)} {F(vbY)} {F(vbW)} {F(vbH)}' "
 + "preserveAspectRatio='xMidYMid meet' font-family=\"SimSun,SimHei,'Microsoft YaHei',sans-serif\">");
 sb.Append("<style>.s{vector-effect:non-scaling-stroke;stroke-width:1;fill:none;stroke-linecap:round;stroke-linejoin:round} .pr{vector-effect:non-scaling-stroke;stroke-width:1;will-change:transform} .layer{}</style>");
 string bg = darkBackground ? "#000000" : "#ffffff";
 sb.Append($"<rect x='{F(vbX)}' y='{F(vbY)}' width='{F(vbW)}' height='{F(vbH)}' fill='{bg}'/>");

 // 按图层分组渲染：每层输出 <g data-layer="name">，支持前端按图层控制显隐
 var layerGroups = entities
 .Where(e => e != null)
 .GroupBy(e => { try { return e.Layer?.Name ?? "0"; } catch { return "0"; } })
 .OrderBy(g => g.Key);

 foreach (var layerGroup in layerGroups)
 {
 string layerName = layerGroup.Key;
 bool frozen = false, locked = false;
 try { var lyr = layerGroup.First().Layer; if (lyr != null) { frozen = lyr.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen); locked = (lyr.Flags & ACadSharp.Tables.LayerFlags.Locked) != 0; } } catch { }
 string layerColor = "#ffffff";
 try { layerColor = AdjHex(AciHex(layerGroup.First().Layer.Color.Index >= 0 ? layerGroup.First().Layer.Color.Index : 7), darkBackground); } catch { }
 // 跳过冻结图层
 if (frozen) continue;

 sb.Append($"<g class='layer' data-layer='{Escape(layerName)}' data-frozen='{frozen}' data-locked='{locked}' data-color='{layerColor}'>");
 var layerEntities = layerGroup.ToList();
 var buckets = new Dictionary<string, Bucket>();
 var primitives = new StringBuilder();

 // 填充层
 DrawList(layerEntities, sb, buckets, primitives, Identity(), null, 0, darkBackground, 0);
 FlushBuckets(sb, buckets); buckets.Clear();
 // 描边层
 DrawList(layerEntities, sb, buckets, primitives, Identity(), null, 0, darkBackground, 1);
 FlushBuckets(sb, buckets); buckets.Clear();
 // 原生几何
 sb.Append(primitives.ToString());
 // 文字层
 DrawList(layerEntities, sb, buckets, primitives, Identity(), null, 0, darkBackground, 2);
 FlushBuckets(sb, buckets); buckets.Clear();

 sb.Append("</g>");
 }
 
 sb.Append("</svg>");

                res.Svg = sb.ToString();
                res.MinX = minX; res.MinY = minY; res.MaxX = maxX; res.MaxY = maxY;
                res.PrimitiveCount = cnt;
                return res;
            }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; return res; }
        }

        /// <summary>把原始 SVG 包裹成带缩放/平移/开窗自适应的完整 HTML 页面（供 WebView2 展示 CAD 矢量图）</summary>
        public static string WrapHtml(string svgRaw, string title, bool darkBackground = true)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><title>");
            sb.Append(System.Web.HttpUtility.HtmlEncode(title ?? "CAD"));
            sb.Append("</title><style>");
            sb.Append("*{margin:0;padding:0;box-sizing:border-box;}");
            sb.Append("html,body{width:100%;height:100%;overflow:hidden;background:" + (darkBackground ? "#000000" : "#ffffff") + ";}");
            sb.Append("#cad-wrap{width:100%;height:100%;overflow:hidden;position:relative;cursor:grab;user-select:none;}");
            sb.Append("#cad-wrap:active{cursor:grabbing;}");
            sb.Append("#cad-svg{display:block;transform-origin:0 0;}");
            sb.Append("#hud{position:fixed;right:10px;bottom:10px;background:rgba(0,0,0,.55);color:#fff;font:12px/1.6 Consolas,monospace;padding:6px 10px;border-radius:6px;pointer-events:none;}");
            sb.Append("</style></head><body><div id='cad-wrap'>");
            sb.Append(svgRaw);
            sb.Append("</div><div id='hud'></div><script>");
            sb.Append("(function(){");
            sb.Append("var wrap=document.getElementById('cad-wrap');");
            sb.Append("var svg=document.getElementById('cad-svg');");
            sb.Append("if(!svg){svg=wrap.querySelector('svg');if(svg)svg.id='cad-svg';}");
            sb.Append("if(!svg)return;");
            sb.Append("var hud=document.getElementById('hud');");
            sb.Append("var _x=0,_y=0,_s=1,_pin=null,_d=null;");
            sb.Append("function fit(){var vw=wrap.clientWidth,vh=wrap.clientHeight;var vb=svg.viewBox.baseVal;");
            sb.Append("if(!vb||vb.width<=0){svg.style.transform='translate(20px,20px) scale(1)';return;}");
            sb.Append("var s=Math.min(vw/vb.width,vh/vb.height)*0.95;");
            sb.Append("_x=(vw-vb.width*s)/2-_s*0;_y=(vh-vb.height*s)/2;_s=s;apply();}");
            sb.Append("function apply(){svg.style.transform='translate('+_x+'px,'+_y+'px) scale('+_s+')';");
            sb.Append("if(hud)hud.textContent='缩放 '+( _s>=1?(_s).toFixed(2)+'x':(_s*100).toFixed(0)+'%');}");
            sb.Append("fit();");
            sb.Append("wrap.addEventListener('wheel',function(e){e.preventDefault();");
            sb.Append("var r=e.deltaY>0?0.9:1.1;var rect=wrap.getBoundingClientRect();");
            sb.Append("var mx=e.clientX-rect.left,my=e.clientY-rect.top;");
            sb.Append("_x=mx-(mx-_x)*r;_y=my-(my-_y)*r;_s*=r;apply();},{passive:false});");
            sb.Append("wrap.addEventListener('mousedown',function(e){if(e.button!==0)return;_pin=[e.clientX,e.clientY];_d=[_x,_y];});");
            sb.Append("window.addEventListener('mousemove',function(e){if(!_pin)return;_x=_d[0]+(e.clientX-_pin[0]);_y=_d[1]+(e.clientY-_pin[1]);apply();});");
            sb.Append("window.addEventListener('mouseup',function(){_pin=null;_d=null;});");
            sb.Append("window.addEventListener('resize',fit);");
            sb.Append("})();");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }

        // ───────────────────────────────────────────────────────
        // 绘制
        // ───────────────────────────────────────────────────────

        private static void DrawList(IList<Entity> entities, StringBuilder sb, Dictionary<string, Bucket> buckets,
            StringBuilder primitives, Matrix m, string parentColor, int depth, bool dark, int pass)
        {
            if (entities == null || depth > MaxDepth) return;
            foreach (var ent in entities)
            {
                if (ent == null) continue;
                try { DrawEntity(ent, sb, buckets, primitives, m, parentColor, depth, dark, pass); }
                catch { }
            }
        }

        private static void DrawEntity(Entity ent, StringBuilder sb, Dictionary<string, Bucket> buckets,
            StringBuilder primitives, Matrix m, string parentColor, int depth, bool dark, int pass)
        {
            switch (ent)
            {
                case Insert ins:
                {
                    var block = ins.Block;
                    if (block?.Entities == null) break;
                    double sx = ins.XScale == 0 ? 1 : ins.XScale;
                    double sy = ins.YScale == 0 ? 1 : ins.YScale;
                    var cm = Compose(m, BlockMatrix(ins.InsertPoint.X, ins.InsertPoint.Y, sx, sy, ins.Rotation));
                    string insColor = parentColor;
                    try { if (!ins.Color.IsByBlock) insColor = ResolveColorHex(ins, parentColor, dark); } catch { }
                    DrawList(block.Entities.ToList(), sb, buckets, primitives, cm, insColor, depth + 1, dark, pass);
                    try { if (ins.Attributes != null) DrawList(ins.Attributes.Cast<Entity>().ToList(), sb, buckets, primitives, cm, insColor, depth + 1, dark, pass); } catch { }
                    break;
                }
                case Dimension dim:
                {
                    BlockRecord blk = null;
                    try { blk = dim.Block; } catch { }
                    if (blk?.Entities != null && blk.Entities.Count > 0)
                    {
                        string dimColor = ResolveColorHex(dim, parentColor, dark);
                        DrawList(blk.Entities.ToList(), sb, buckets, primitives, m, dimColor, depth + 1, dark, pass);
                    }
                    break;
                }
                case Hatch h when pass == 0:
                    DrawHatch(sb, m, h, parentColor, dark);
                    break;
                case Solid sd when pass == 0:
                {
                    string c = ResolveColorHex(ent, parentColor, dark);
                    var p = new[]
                    {
                        P(m, sd.FirstCorner.X, sd.FirstCorner.Y), P(m, sd.SecondCorner.X, sd.SecondCorner.Y),
                        P(m, sd.FourthCorner.X, sd.FourthCorner.Y), P(m, sd.ThirdCorner.X, sd.ThirdCorner.Y),
                    };
                    sb.Append($"<polygon points='{Pt(p[0])} {Pt(p[1])} {Pt(p[2])} {Pt(p[3])}' fill='{c}' stroke='none'/>");
                    break;
                }
                case Face3D f when pass == 0:
                {
                    string c = ResolveColorHex(ent, parentColor, dark);
                    var p = new[]
                    {
                        P(m, f.FirstCorner.X, f.FirstCorner.Y), P(m, f.SecondCorner.X, f.SecondCorner.Y),
                        P(m, f.ThirdCorner.X, f.ThirdCorner.Y), P(m, f.FourthCorner.X, f.FourthCorner.Y),
                    };
                    sb.Append($"<polygon points='{Pt(p[0])} {Pt(p[1])} {Pt(p[2])} {Pt(p[3])}' fill='{c}' stroke='none'/>");
                    break;
                }
                case Ray ray when pass == 1:
                {
                    var s = P(m, ray.StartPoint.X, ray.StartPoint.Y);
                    AddSeg(buckets, ent, parentColor, dark, m, SegLine(s, P(m, s.x + ray.Direction.X * 1e4, s.y + ray.Direction.Y * 1e4)));
                    break;
                }
                case XLine xl when pass == 1:
                {
                    var s = P(m, xl.FirstPoint.X, xl.FirstPoint.Y);
                    AddSeg(buckets, ent, parentColor, dark, m, SegLine(P(m, s.x + xl.Direction.X * 1e4, s.y + xl.Direction.Y * 1e4), P(m, s.x - xl.Direction.X * 1e4, s.y - xl.Direction.Y * 1e4)));
                    break;
                }
                case Line ln when pass == 1:
                    AddSeg(buckets, ent, parentColor, dark, m, SegLine(P(m, ln.StartPoint.X, ln.StartPoint.Y), P(m, ln.EndPoint.X, ln.EndPoint.Y)));
                    break;
                case LwPolyline pl when pass == 1:
                    AddSeg(buckets, ent, parentColor, dark, m, SegPoly(pl.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), pl.IsClosed, m));
                    break;
                case Polyline2D p2 when pass == 1:
                    AddSeg(buckets, ent, parentColor, dark, m, SegPoly(p2.Vertices.Select(v => (v.Location.X, v.Location.Y, 0.0)).ToList(), p2.IsClosed, m));
                    break;
                case Polyline3D p3 when pass == 1:
                    AddSeg(buckets, ent, parentColor, dark, m, SegPoly(p3.Vertices.Select(v => (v.Location.X, v.Location.Y, 0.0)).ToList(), p3.IsClosed, m));
                    break;
                case Circle c when pass == 1:
                {
                    string col = ResolveColorHex(ent, parentColor, dark);
                    string w = StrokePx(ent);
                    string dash = DashArray(ent);
                    var ctr = P(m, c.Center.X, c.Center.Y);
                    double r = c.Radius * MatrixScale(m);
                    primitives.Append($"<circle class='pr' cx='{ctr.x:F2}' cy='{ctr.y:F2}' r='{r:F2}' fill='none' stroke='{col}' stroke-width='{w}'{dash}/>");
                    break;
                }
                case Arc a when pass == 1:
                {
                    string col = ResolveColorHex(ent, parentColor, dark);
                    string w = StrokePx(ent);
                    string dash = DashArray(ent);
                    double r = a.Radius * MatrixScale(m);
                    double sa = a.StartAngle, ea = a.EndAngle;
                    double span = ea - sa;
                    while (span <= 0) span += 2 * Math.PI;
                    while (span > 2 * Math.PI) span -= 2 * Math.PI;
                    if (span >= 2 * Math.PI - 0.001)
                    {
                        var ctr = P(m, a.Center.X, a.Center.Y);
                        primitives.Append($"<circle class='pr' cx='{ctr.x:F2}' cy='{ctr.y:F2}' r='{r:F2}' fill='none' stroke='{col}' stroke-width='{w}'{dash}/>");
                        break;
                    }
                    var ps = P(m, a.Center.X + a.Radius * Math.Cos(sa), a.Center.Y + a.Radius * Math.Sin(sa));
                    var pe = P(m, a.Center.X + a.Radius * Math.Cos(ea), a.Center.Y + a.Radius * Math.Sin(ea));
                    int large = (span > Math.PI) ? 1 : 0;
                    primitives.Append($"<path class='pr' d='M{ps.x:F2} {ps.y:F2} A{r:F2} {r:F2} 0 {large} 1 {pe.x:F2} {pe.y:F2}' fill='none' stroke='{col}' stroke-width='{w}'{dash}/>");
                    break;
                }
                case Ellipse e when pass == 1:
                {
                    string col = ResolveColorHex(ent, parentColor, dark);
                    string w = StrokePx(ent);
                    string dash = DashArray(ent);
                    var ctr = P(m, e.Center.X, e.Center.Y);
                    double rx = e.MajorAxis * MatrixScale(m);
                    double ry = e.MajorAxis * e.RadiusRatio * MatrixScale(m);
                    double rotDeg = e.Rotation * 180.0 / Math.PI;
                    primitives.Append($"<ellipse class='pr' cx='{ctr.x:F2}' cy='{ctr.y:F2}' rx='{rx:F2}' ry='{ry:F2}' transform='rotate({rotDeg:F2} {ctr.x:F2} {ctr.y:F2})' fill='none' stroke='{col}' stroke-width='{w}'{dash}/>");
                    break;
                }
                case Spline sp when pass == 1:
                {
                    var list = (sp.FitPoints != null && sp.FitPoints.Count >= 2) ? sp.FitPoints : sp.ControlPoints;
                    if (list != null && list.Count >= 2)
                        AddSeg(buckets, ent, parentColor, dark, m, SegPoly(list.Select(p => (p.X, p.Y, 0.0)).ToList(), sp.IsClosed, m));
                    break;
                }
                case Leader ld when pass == 1:
                {
                    if (ld.Vertices != null && ld.Vertices.Count >= 2)
                        AddSeg(buckets, ent, parentColor, dark, m, SegPoly(ld.Vertices.Select(v => (v.X, v.Y, 0.0)).ToList(), false, m));
                    break;
                }
                case ACadSharp.Entities.Point ptE when pass == 1:
                {
                    var p = P(m, ptE.Location.X, ptE.Location.Y);
                    string c = ResolveColorHex(ent, parentColor, dark);
                    sb.Append($"<circle cx='{F(p.x)}' cy='{F(p.y)}' r='1.5' fill='{c}' stroke='none'/>");
                    break;
                }
                case MText mt when pass == 2:
                {
                    string txt = CleanMText(mt.Value);
                    if (string.IsNullOrWhiteSpace(txt)) break;
                    // MText 是“框宽内自动折行”的多行文本：按参考框宽估宽折行，
                    // 长段落不再被渲染成一条顶出图纸的长线、与其它图形重叠
                    txt = WrapMText(txt, mt.RectangleWidth, mt.Height > 0 ? mt.Height : 2.5);
                    var p = P(m, mt.InsertPoint.X, mt.InsertPoint.Y);
                    double h = mt.Height > 0 ? mt.Height : 2.5;
                    AppendText(sb, p, h * MatrixScale(m), -mt.Rotation * 180 / Math.PI, ResolveColorHex(ent, parentColor, dark), txt, ResolveFontFamily(ent));
                    break;
                }
 case TextEntity tx when pass == 2:
 {
 if (tx is AttributeDefinition) break;
 string txt = CleanMText(tx.Value);
 if (string.IsNullOrWhiteSpace(txt)) break;
 double bx = tx.InsertPoint.X, by = tx.InsertPoint.Y;
 string anchor = "start";
 try
 {
 if (tx.HorizontalAlignment != TextHorizontalAlignment.Left && (tx.AlignmentPoint.X != 0 || tx.AlignmentPoint.Y != 0))
 { bx = tx.AlignmentPoint.X; by = tx.AlignmentPoint.Y; }
 if (tx.HorizontalAlignment == TextHorizontalAlignment.Right) anchor = "end";
 else if (tx.HorizontalAlignment == TextHorizontalAlignment.Center) anchor = "middle";
 }
 catch { }
 var p = P(m, bx, by);
 double h = tx.Height > 0 ? tx.Height : 2.5;
 AppendText(sb, p, h * MatrixScale(m), -tx.Rotation * 180 / Math.PI, ResolveColorHex(ent, parentColor, dark), txt, ResolveFontFamily(ent), anchor);
 break;
 }
            }
        }

        // ───────────────────────────────────────────────────────
        // 几何 → SVG path d 段
        // ───────────────────────────────────────────────────────

        private static void AddSeg(Dictionary<string, Bucket> buckets, Entity ent, string parentColor, bool dark, Matrix m, string seg)
        {
            if (string.IsNullOrEmpty(seg)) return;
            string color = ResolveColorHex(ent, parentColor, dark);
            string width = StrokePx(ent);
            string dash = DashArray(ent);
            string key = color + "|" + width + "|" + dash;
            if (!buckets.TryGetValue(key, out var b))
            {
                b = new Bucket { Color = color, Width = width, Dash = dash };
                buckets[key] = b;
            }
            b.D.Append(seg);
        }

        private static string SegLine((double x, double y) a, (double x, double y) b)
            => $"M{F(a.x)} {F(a.y)}L{F(b.x)} {F(b.y)}";

        private static string SegPoly(List<(double x, double y, double bulge)> verts, bool closed, Matrix m)
        {
            if (verts.Count == 0) return "";
            var sb = new StringBuilder();
            bool started = false;
            for (int i = 0; i < verts.Count; i++)
            {
                var cur = P(m, verts[i].x, verts[i].y);
                if (!started) { sb.Append($"M{F(cur.x)} {F(cur.y)}"); started = true; }
                else
                {
                    var prev = P(m, verts[i - 1].x, verts[i - 1].y);
                    if (Math.Abs(verts[i - 1].bulge) > 1e-4) sb.Append(BulgeCmd(prev, cur, verts[i - 1].bulge));
                    else sb.Append($"L{F(cur.x)} {F(cur.y)}");
                }
            }
            if (closed && started && verts.Count > 1)
            {
                var first = P(m, verts[0].x, verts[0].y);
                var last = P(m, verts[verts.Count - 1].x, verts[verts.Count - 1].y);
                if (Math.Abs(verts[verts.Count - 1].bulge) > 1e-4) sb.Append(BulgeCmd(last, first, verts[verts.Count - 1].bulge));
                else sb.Append("Z");
            }
            return sb.ToString();
        }

        private static string BulgeCmd((double x, double y) p0, (double x, double y) p1, double bulge)
        {
            double chord = Math.Sqrt((p1.x - p0.x) * (p1.x - p0.x) + (p1.y - p0.y) * (p1.y - p0.y));
            if (chord < 1e-6) return $"L{F(p1.x)} {F(p1.y)}";
            double sag = bulge * chord / 2;
            double mx = (p0.x + p1.x) / 2, my = (p0.y + p1.y) / 2;
            double nx = -(p1.y - p0.y), ny = (p1.x - p0.x);
            double nl = Math.Sqrt(nx * nx + ny * ny);
            if (nl < 1e-6) return $"L{F(p1.x)} {F(p1.y)}";
            nx /= nl; ny /= nl;
            return $"Q{F(mx - nx * sag)} {F(my - ny * sag)} {F(p1.x)} {F(p1.y)}";
        }

        private static string SegCircle(Matrix m, double cx, double cy, double r)
        {
            int seg = 64;
            var sb = new StringBuilder();
            for (int i = 0; i <= seg; i++)
            {
                double t = i * 2 * Math.PI / seg;
                var p = P(m, cx + r * Math.Cos(t), cy + r * Math.Sin(t));
                sb.Append(i == 0 ? $"M{F(p.x)} {F(p.y)}" : $"L{F(p.x)} {F(p.y)}");
            }
            return sb.Append("Z").ToString();
        }

        private static string SegArc(Matrix m, double cx, double cy, double r, double a0, double a1)
        {
            double sweep = a1 - a0;
            if (sweep <= 0) sweep += 2 * Math.PI;
            int seg = Math.Max(8, Math.Min(64, (int)(sweep / (Math.PI / 24))));
            var sb = new StringBuilder();
            for (int i = 0; i <= seg; i++)
            {
                double t = a0 + sweep * i / seg;
                var p = P(m, cx + r * Math.Cos(t), cy + r * Math.Sin(t));
                sb.Append(i == 0 ? $"M{F(p.x)} {F(p.y)}" : $"L{F(p.x)} {F(p.y)}");
            }
            return sb.ToString();
        }

        private static string SegEllipse(Matrix m, Ellipse e)
        {
            double rx = e.MajorAxis, ry = e.MajorAxis * e.RadiusRatio, rot = e.Rotation;
            int seg = 96;
            var sb = new StringBuilder();
            for (int i = 0; i <= seg; i++)
            {
                double t = i * 2 * Math.PI / seg;
                double ex = e.Center.X + rx * Math.Cos(t) * Math.Cos(rot) - ry * Math.Sin(t) * Math.Sin(rot);
                double ey = e.Center.Y + rx * Math.Cos(t) * Math.Sin(rot) + ry * Math.Sin(t) * Math.Cos(rot);
                var p = P(m, ex, ey);
                sb.Append(i == 0 ? $"M{F(p.x)} {F(p.y)}" : $"L{F(p.x)} {F(p.y)}");
            }
            return sb.Append("Z").ToString();
        }

        private static void DrawHatch(StringBuilder sb, Matrix m, Hatch h, string parentColor, bool dark)
        {
            if (h.Paths == null) return;
            string color = ResolveColorHex(h, parentColor, dark);
            bool solid = false;
            try { solid = h.IsSolid || (h.Pattern != null && string.Equals(h.Pattern.Name, "SOLID", StringComparison.OrdinalIgnoreCase)); } catch { }

            var dPath = new StringBuilder();
            dPath.Append("<path d='");
            bool any = false;
            foreach (var bp in h.Paths)
            {
                IEnumerable<Entity> edges = null;
                try
                {
                    if (bp.Entities != null && bp.Entities.Count > 0) edges = bp.Entities;
                    else if (bp.Edges != null) edges = bp.Edges.Select(e => { try { return e.ToEntity(); } catch { return null; } }).Where(e => e != null);
                }
                catch { }
                if (edges == null) continue;
                bool started = false;
                foreach (var e in edges)
                {
                    switch (e)
                    {
                        case Line l:
                        {
                            var a = P(m, l.StartPoint.X, l.StartPoint.Y);
                            var b = P(m, l.EndPoint.X, l.EndPoint.Y);
                            if (!started) { dPath.Append($"M{F(a.x)} {F(a.y)}"); started = true; any = true; }
                            dPath.Append($"L{F(b.x)} {F(b.y)}");
                            break;
                        }
                        case LwPolyline pl:
                            foreach (var v in pl.Vertices)
                            {
                                var p = P(m, v.Location.X, v.Location.Y);
                                if (!started) { dPath.Append($"M{F(p.x)} {F(p.y)}"); started = true; any = true; }
                                else dPath.Append($"L{F(p.x)} {F(p.y)}");
                            }
                            break;
                        case Arc arc:
                        {
                            double sweep = arc.EndAngle - arc.StartAngle;
                            if (sweep < 0) sweep += 2 * Math.PI;
                            int seg = Math.Max(8, (int)(sweep / (Math.PI / 24)));
                            for (int i = 0; i <= seg; i++)
                            {
                                double t = arc.StartAngle + sweep * i / seg;
                                var p = P(m, arc.Center.X + arc.Radius * Math.Cos(t), arc.Center.Y + arc.Radius * Math.Sin(t));
                                if (!started) { dPath.Append($"M{F(p.x)} {F(p.y)}"); started = true; any = true; }
                                else dPath.Append($"L{F(p.x)} {F(p.y)}");
                            }
                            break;
                        }
                        case Circle cc:
                            var c = P(m, cc.Center.X, cc.Center.Y);
                            int seg2 = 32;
                            for (int i = 0; i <= seg2; i++)
                            {
                                double t = i * 2 * Math.PI / seg2;
                                var p = P(m, cc.Center.X + cc.Radius * Math.Cos(t), cc.Center.Y + cc.Radius * Math.Sin(t));
                                if (!started) { dPath.Append($"M{F(p.x)} {F(p.y)}"); started = true; any = true; }
                                else dPath.Append($"L{F(p.x)} {F(p.y)}");
                            }
                            break;
                    }
                }
                if (started) dPath.Append("Z");
            }
            if (!any) return;
            dPath.Append("' ");
            sb.Append(dPath.ToString());
            sb.Append($"fill='{color}' fill-rule='evenodd' fill-opacity='{(solid ? 1 : 0.35)}' stroke='none'/>");
            if (!solid)
            {
                sb.Append(dPath.ToString());
                sb.Append($"class='s' stroke='{color}' stroke-width='{StrokePx(h)}'/>");
            }
        }

private static void AppendText(StringBuilder sb, (double x, double y) p, double fontSize, double rotDeg, string color, string text, string fontFamily = null, string anchor = "start")
{
var lines = text.Replace("\r", "").Split('\n');
string ff = string.IsNullOrEmpty(fontFamily) ? "" : $" font-family=\"{fontFamily}\"";
sb.Append($"<text x='{F(p.x)}' y='{F(p.y)}' fill='{color}' font-size='{F(fontSize)}'{ff} text-anchor='{anchor}' "
+ (Math.Abs(rotDeg) > 0.1 ? $"transform='rotate({F(rotDeg)} {F(p.x)} {F(p.y)})' " : "")
+ "stroke='none'>");
            double lh = fontSize * 1.25;
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0) sb.Append(Escape(lines[i]));
                else sb.Append($"<tspan x='{F(p.x)}' dy='{F(lh)}'>{Escape(lines[i])}</tspan>");
            }
            sb.Append("</text>");
        }

        // ───────────────────────────────────────────────────────
        // 颜色 / 线宽 / 线型
        // ───────────────────────────────────────────────────────

        private static string ResolveColorHex(Entity ent, string parentColor, bool dark)
        {
            try
            {
                var c = ent.Color;
                if (c.IsByBlock) return parentColor ?? DefHex(dark);
                if (c.IsByLayer && ent.Layer != null) c = ent.Layer.Color;
                if (c.IsByLayer) return parentColor ?? DefHex(dark);
                if (c.IsTrueColor)
                {
                    uint v = unchecked((uint)c.TrueColor);
                    return AdjHex(RgbToHex((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)), dark);
                }
                int idx = 7;
                try { idx = c.Index; } catch { }
                if (idx == 0 || idx == 256) return parentColor ?? DefHex(dark);
                return AdjHex(AciHex(idx), dark);
            }
            catch { return DefHex(dark); }
        }

        private static string DefHex(bool dark) => dark ? "#ffffff" : "#000000";

        private static string AdjHex(string hex, bool dark)
        {
            var (r, g, b) = HexToRgb(hex);
            double lum = 0.299 * r + 0.587 * g + 0.114 * b;
            if (dark)
            {
                if (lum < 45)
                {
                    if (lum < 12) return "#ffffff";
                    double k = 110.0 / Math.Max(lum, 1);
                    return RgbToHex((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
                }
            }
            else if (lum > 225) return "#000000";
            return hex;
        }

        private static double LineWeightMm(Entity ent)
        {
            try
            {
                var lw = ent.LineWeight;
                if (lw == LineWeightType.ByLayer && ent.Layer != null) lw = ent.Layer.LineWeight;
                if (lw == LineWeightType.ByBlock || lw == LineWeightType.Default || lw == LineWeightType.ByLayer) return 0.25;
                double mm = (int)lw / 100.0;
                return mm <= 0 ? 0.25 : mm;
            }
            catch { return 0.25; }
        }

 private static string StrokePx(Entity ent)
 {
 // 线宽统一设为 0：所有线条都用同一条细线，不随缩放改变
 // 配合 CSS vector-effect:non-scaling-stroke，缩放时视觉宽度恒定不变
 return "0.5";
 }

        private static string DashArray(Entity ent)
        {
            try
            {
                var lt = ent.LineType;
                if (lt == null && ent.Layer != null) lt = ent.Layer.LineType;
                if (lt == null) return "";
                string name = lt.Name ?? "";
                if (name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return "";
                var segs = lt.Segments?.ToList();
                if (segs == null || segs.Count < 2) return "";
                var arr = new List<string>();
                foreach (var s in segs)
                {
                    double len = Math.Abs(s.Length);
                    if (len < 1e-4) len = 0.6;
                    arr.Add(len.ToString("F2", CultureInfo.InvariantCulture));
                }
                if (arr.Count % 2 != 0) arr.Add(arr[arr.Count - 1]);
                return " stroke-dasharray='" + string.Join(" ", arr) + "'";
            }
 catch { return ""; }
 }

 // ───────────────────────────────────────────────────────
 // 字体解析：从实体的 TextStyle 读取字体名，映射 SHX 大字体到中文字体
 // ───────────────────────────────────────────────────────

 private static string ResolveFontFamily(Entity ent)
 {
 try
 {
 ACadSharp.Tables.TextStyle style = null;
 if (ent is MText mt) style = mt.Style;
 else if (ent is TextEntity tx) style = tx.Style;
 if (style == null) return null;
 string primaryFont = null;
 string bigFont = null;
 try
 {
 var styleType = style.GetType();
 var pProp = styleType.GetProperty("Filename") ?? styleType.GetProperty("FontFile") ?? styleType.GetProperty("PrimaryFont");
 if (pProp != null) primaryFont = pProp.GetValue(style)?.ToString();
 var bProp = styleType.GetProperty("BigFontFilename") ?? styleType.GetProperty("BigFont");
 if (bProp != null) bigFont = bProp.GetValue(style)?.ToString();
 }
 catch { }

 string result = null;
 // 大字体优先：SHX 大字体映射到中文字体
 if (!string.IsNullOrEmpty(bigFont))
 result = MapShxToFont(bigFont);
 // 主字体
 if (string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(primaryFont))
 result = MapShxToFont(primaryFont);

 // 如果解析到 TTF 字体名，直接用
 if (!string.IsNullOrEmpty(primaryFont) && (primaryFont.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || primaryFont.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
 {
 string ttfName = System.IO.Path.GetFileNameWithoutExtension(primaryFont);
 result = $"{ttfName}, SimSun, SimHei, 'Microsoft YaHei', sans-serif";
 }

 return result; // null 时用根 svg 的默认字体
 }
 catch { return null; }
 }

 private static string MapShxToFont(string shxName)
 {
 if (string.IsNullOrEmpty(shxName)) return null;
 string name = System.IO.Path.GetFileNameWithoutExtension(shxName).ToLowerInvariant();

 // 常见 SHX 大字体 → 中文字体映射
 switch (name)
 {
 // 中文大字体 SHX → 用 Windows 中文字体替代（浏览器无法渲染 SHX）
 case "hztxt":
 case "hzdx":
 case "hz":
 case "chineset":
 case "gbcbig":
 case "gbchinese":
 return "'SimSun', '宋体', SimHei, 'Microsoft YaHei', sans-serif";
 case "hzfs":
 return "'仿宋', FangSong, 'Microsoft YaHei', sans-serif";
 case "hzkt":
 return "'楷体', KaiTi, 'Microsoft YaHei', sans-serif";
 case "hzht":
 return "'黑体', SimHei, 'Microsoft YaHei', sans-serif";
 // 英文 SHX → 英文字体
 case "txt":
 case "simplex":
 case "romans":
 return "Arial, Helvetica, sans-serif";
 case "romand":
 case "romantic":
 return "'Times New Roman', serif";
 case "italic":
 case "italict":
 return "'Times New Roman', serif";
 case "scripts":
 case "scriptc":
 return "'Comic Sans MS', cursive";
 default:
 // 未知的 SHX 文件名，尝试去掉后缀直接用（可能恰好是 TTF 名）
 if (shxName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) || shxName.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
 return $"'{name}', SimSun, SimHei, 'Microsoft YaHei', sans-serif";
 return null;
 }
 }

 // ───────────────────────────────────────────────────────
 // 包围盒遍历（排除 Ray/XLine）
        // ───────────────────────────────────────────────────────

        private static void Walk(IList<Entity> entities, List<(double x, double y)> pts, ref int cnt, Matrix m, int depth)
        {
            if (entities == null || depth > MaxDepth) return;
            foreach (var ent in entities)
            {
                if (ent == null) continue;
                cnt++;
                try { WalkEntity(ent, pts, ref cnt, m, depth); } catch { }
            }
        }

        private static void WalkEntity(Entity ent, List<(double x, double y)> pts, ref int cnt, Matrix m, int depth)
        {
            switch (ent)
            {
                case Line l: Add(pts, m, l.StartPoint.X, l.StartPoint.Y); Add(pts, m, l.EndPoint.X, l.EndPoint.Y); break;
                case Arc a: SampleArc(pts, m, a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle); break;
                case Circle c: Add(pts, m, c.Center.X + c.Radius, c.Center.Y); Add(pts, m, c.Center.X - c.Radius, c.Center.Y); Add(pts, m, c.Center.X, c.Center.Y + c.Radius); Add(pts, m, c.Center.X, c.Center.Y - c.Radius); break;
                case Ellipse e:
                {
                    double rx = e.MajorAxis, ry = e.MajorAxis * e.RadiusRatio, rot = e.Rotation;
                    for (int i = 0; i < 16; i++)
                    {
                        double t = i * 2 * Math.PI / 16;
                        Add(pts, m, e.Center.X + rx * Math.Cos(t) * Math.Cos(rot) - ry * Math.Sin(t) * Math.Sin(rot), e.Center.Y + rx * Math.Cos(t) * Math.Sin(rot) + ry * Math.Sin(t) * Math.Cos(rot));
                    }
                    break;
                }
                case LwPolyline pl: foreach (var v in pl.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                case Polyline2D p2: foreach (var v in p2.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                case Polyline3D p3: foreach (var v in p3.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                case Spline s:
                {
                    var list = (s.FitPoints != null && s.FitPoints.Count >= 2) ? s.FitPoints : s.ControlPoints;
                    if (list != null) foreach (var p in list) Add(pts, m, p.X, p.Y);
                    break;
                }
                case Solid sd: Add(pts, m, sd.FirstCorner.X, sd.FirstCorner.Y); Add(pts, m, sd.SecondCorner.X, sd.SecondCorner.Y); Add(pts, m, sd.ThirdCorner.X, sd.ThirdCorner.Y); Add(pts, m, sd.FourthCorner.X, sd.FourthCorner.Y); break;
                case Face3D f: Add(pts, m, f.FirstCorner.X, f.FirstCorner.Y); Add(pts, m, f.SecondCorner.X, f.SecondCorner.Y); Add(pts, m, f.ThirdCorner.X, f.ThirdCorner.Y); Add(pts, m, f.FourthCorner.X, f.FourthCorner.Y); break;
                case Leader ld: if (ld.Vertices != null) foreach (var v in ld.Vertices) Add(pts, m, v.X, v.Y); break;
                case MText mt: Add(pts, m, mt.InsertPoint.X, mt.InsertPoint.Y); break;
                case TextEntity t: Add(pts, m, t.InsertPoint.X, t.InsertPoint.Y); break;
                case ACadSharp.Entities.Point p: Add(pts, m, p.Location.X, p.Location.Y); break;
                case Hatch h:
                {
                    if (h.Paths == null) break;
                    foreach (var bp in h.Paths)
                    {
                        IEnumerable<Entity> edges = null;
                        try { edges = bp.Entities ?? bp.Edges?.Select(e => { try { return e.ToEntity(); } catch { return null; } }).Where(e => e != null); } catch { }
                        if (edges == null) continue;
                        foreach (var e in edges)
                        {
                            switch (e)
                            {
                                case Line l2: Add(pts, m, l2.StartPoint.X, l2.StartPoint.Y); Add(pts, m, l2.EndPoint.X, l2.EndPoint.Y); break;
                                case LwPolyline pl2: foreach (var v in pl2.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                                case Arc a2: SampleArc(pts, m, a2.Center.X, a2.Center.Y, a2.Radius, a2.StartAngle, a2.EndAngle); break;
                                case Circle c2: Add(pts, m, c2.Center.X + c2.Radius, c2.Center.Y); Add(pts, m, c2.Center.X - c2.Radius, c2.Center.Y); break;
                            }
                        }
                    }
                    break;
                }
                case Insert ins:
                {
                    var b = ins.Block;
                    if (b?.Entities == null) break;
                    double sx = ins.XScale == 0 ? 1 : ins.XScale, sy = ins.YScale == 0 ? 1 : ins.YScale;
                    Walk(b.Entities.ToList(), pts, ref cnt, Compose(m, BlockMatrix(ins.InsertPoint.X, ins.InsertPoint.Y, sx, sy, ins.Rotation)), depth + 1);
                    break;
                }
                case Dimension dim:
                {
                    BlockRecord blk = null;
                    try { blk = dim.Block; } catch { }
                    if (blk?.Entities != null) Walk(blk.Entities.ToList(), pts, ref cnt, m, depth + 1);
                    break;
                }
                case Ray _: case XLine _: break;
            }
        }

        // ───────────────────────────────────────────────────────
        // 辅助
        // ───────────────────────────────────────────────────────

        private static (double x, double y) P(Matrix m, double x, double y)
        {
            var (X, Y) = Tf(m, x, y);
            return (X, -Y);
        }

        private static void Add(List<(double x, double y)> pts, Matrix m, double x, double y)
        {
            var (X, Y) = Tf(m, x, y);
            if (double.IsNaN(X) || double.IsNaN(Y) || double.IsInfinity(X) || double.IsInfinity(Y)) return;
            pts.Add((X, Y));
        }

        private static void SampleArc(List<(double x, double y)> pts, Matrix m, double cx, double cy, double r, double a0, double a1)
        {
            double sweep = a1 - a0;
            if (sweep <= 0) sweep += 2 * Math.PI;
            int seg = Math.Max(4, Math.Min(24, (int)(sweep / (Math.PI / 8))));
            for (int i = 0; i <= seg; i++)
            {
                double t = a0 + sweep * i / seg;
                Add(pts, m, cx + r * Math.Cos(t), cy + r * Math.Sin(t));
            }
        }

        private static string Pt((double x, double y) p) => $"{F(p.x)},{F(p.y)}";
        private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string CleanMText(string s)
        {
            if (s == null) return "";
            s = s.Replace("\\P", "\n").Replace("\\p", "\n").Replace("\\~", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\f[^;]*;", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[A-Za-z]+\d*;?", "");
            s = s.Replace("{}", "");
            return s;
        }

        /// <summary>MText 按参考框宽自动折行：CJK 按 1em、ASCII 按 0.55em 估宽。</summary>
        private static string WrapMText(string text, double boxWidth, double fontHeight)
        {
            if (string.IsNullOrEmpty(text) || boxWidth <= 0 || fontHeight <= 0) return text;
            double maxEm = boxWidth / fontHeight;
            if (maxEm < 2) return text;   // 框宽过窄（未设置宽度）不折行
            var sb = new StringBuilder();
            bool first = true;
            foreach (var rawLine in text.Split('\n'))
            {
                if (!first) sb.Append('\n');
                first = false;
                if (rawLine.Length == 0) continue;
                var cur = new StringBuilder();
                double w = 0;
                foreach (char ch in rawLine)
                {
                    double cw = IsFullWidthChar(ch) ? 1.0 : 0.55;
                    if (cur.Length > 0 && w + cw > maxEm)
                    {
                        sb.Append(cur);
                        sb.Append('\n');
                        cur.Clear();
                        w = 0;
                    }
                    cur.Append(ch);
                    w += cw;
                }
                sb.Append(cur);
            }
            return sb.ToString();
        }

        private static bool IsFullWidthChar(char c)
        {
            int v = c;
            return (v >= 0x1100 && v <= 0x115F)      // Hangul Jamo
                || (v >= 0x2E80 && v <= 0x303E)      // CJK Radicals .. CJK Symbols/Punctuation
                || (v >= 0x3041 && v <= 0x33FF)      // Hiragana .. CJK Compatibility
                || (v >= 0x3400 && v <= 0x4DBF)      // CJK Ext A
                || (v >= 0x4E00 && v <= 0x9FFF)      // CJK Unified Ideographs
                || (v >= 0xA000 && v <= 0xA4CF)      // Yi
                || (v >= 0xAC00 && v <= 0xD7A3)      // Hangul Syllables
                || (v >= 0xF900 && v <= 0xFAFF)      // CJK Compatibility Ideographs
                || (v >= 0xFE30 && v <= 0xFE4F)      // CJK Compatibility Forms
                || (v >= 0xFF00 && v <= 0xFF60)      // Fullwidth Forms
                || (v >= 0xFFE0 && v <= 0xFFE6)      // Fullwidth Signs
                || (v >= 0x20000 && v <= 0x2FA1F);   // CJK Ext B..F
        }

        private readonly struct Matrix { public readonly double A, B, C, D, E, F; public Matrix(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; } }
        private static Matrix Identity() => new Matrix(1, 0, 0, 1, 0, 0);
        private static Matrix BlockMatrix(double tx, double ty, double sx, double sy, double rot)
        {
            double c = Math.Cos(rot), s = Math.Sin(rot);
            return new Matrix(c * sx, s * sx, -s * sy, c * sy, tx, ty);
        }
        private static Matrix Compose(Matrix p, Matrix c) => new Matrix(
            p.A * c.A + p.C * c.B, p.B * c.A + p.D * c.B,
            p.A * c.C + p.C * c.D, p.B * c.C + p.D * c.D,
            p.A * c.E + p.C * c.F + p.E, p.B * c.E + p.D * c.F + p.F);
        private static (double x, double y) Tf(Matrix m, double x, double y) => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);
        private static double MatrixScale(Matrix m)
        {
            double sx = Math.Sqrt(m.A * m.A + m.B * m.B);
            double sy = Math.Sqrt(m.C * m.C + m.D * m.D);
            double s = (sx + sy) / 2;
            return s <= 1e-9 ? 1 : s;
        }

        // ── ACI 调色板 ──
        private static string AciHex(int idx)
        {
            if (idx <= 0) return "#000000";
            if (idx <= 9)
            {
                return idx switch
                {
                    1 => "#ff0000", 2 => "#ffff00", 3 => "#00ff00", 4 => "#00ffff",
                    5 => "#0000ff", 6 => "#ff00ff", 7 => "#ffffff", 8 => "#808080",
                    9 => "#c0c0c0", _ => "#000000"
                };
            }
            if (idx <= 249)
            {
                int i = idx - 10;
                double hue = (i % 20) * 18.0;
                int row = i / 20;
                double sat = (row < 6) ? 1.0 : 0.5;
                double lum = 1.0 - (row % 6) * 0.15;
                return HslToHex(hue, sat, lum);
            }
            int g = (idx - 250) * 51;
            return RgbToHex((byte)g, (byte)g, (byte)g);
        }

        private static string HslToHex(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360 / 360;
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = Hue2Rgb(p, q, h + 1.0 / 3);
                g = Hue2Rgb(p, q, h);
                b = Hue2Rgb(p, q, h - 1.0 / 3);
            }
            return RgbToHex((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
        private static double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        private static string RgbToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
        private static (byte r, byte g, byte b) HexToRgb(string hex)
        {
            hex = hex.TrimStart('#');
            return (Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }
    }
}
