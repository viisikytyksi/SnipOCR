// -----------------------------------------------------------------------------
// RapidOCR engine for SnipOCR (PP-OCRv6 small, ONNX Runtime, CPU only).
//
// Why: Windows.Media.Ocr and Tesseract both mis-read small Japanese screen text.
// This runs Baidu's PP-OCRv6 "small" detector + recognizer through ONNX Runtime,
// which is far more accurate on Japanese while still light enough for 8 GB PCs.
//
// Everything here is self-contained: DB text detection post-processing, rotated
// rect unclipping, perspective cropping and CTC greedy decoding are implemented
// locally, so the only dependencies are onnxruntime.dll and its managed wrapper.
//
// C# 5 compatible (the build uses the .NET Framework csc.exe).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SnipOCR
{
    /// <summary>One recognized text line with its on-screen geometry.</summary>
    sealed class RapidOcrLine
    {
        public string Text = "";
        public float Score;
        public int Row;                     // reading-order row index
        // top-left, top-right, bottom-right, bottom-left in source-image pixels
        public float[] Quad = new float[8];
        public int Left, Top, Right, Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public int CenterY { get { return (Top + Bottom) / 2; } }
    }

    /// <summary>Simple RGB24 image buffer used by the OCR stages.</summary>
    sealed class RapidImage
    {
        public int W;
        public int H;
        public byte[] Rgb;   // interleaved R,G,B

        public RapidImage(int w, int h)
        {
            W = w; H = h; Rgb = new byte[w * h * 3];
        }

        public static RapidImage FromBitmap(Bitmap bmp)
        {
            var img = new RapidImage(bmp.Width, bmp.Height);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData bd = null;
            try
            {
                bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = bd.Stride;
                var buf = new byte[stride * bmp.Height];
                Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                byte[] dst = img.Rgb;
                int w = bmp.Width, h = bmp.Height;
                for (int y = 0; y < h; y++)
                {
                    int src = y * stride;
                    int d = y * w * 3;
                    for (int x = 0; x < w; x++)
                    {
                        int i = src + x * 4;      // BGRA
                        dst[d] = buf[i + 2];      // R
                        dst[d + 1] = buf[i + 1];  // G
                        dst[d + 2] = buf[i];      // B
                        d += 3;
                    }
                }
            }
            finally
            {
                if (bd != null) bmp.UnlockBits(bd);
            }
            return img;
        }

        public static RapidImage FromBitmap32(Bitmap bmp)
        {
            // Convert anything (indexed, 24bpp) into a 32bpp ARGB copy first.
            if (bmp.PixelFormat == PixelFormat.Format32bppArgb ||
                bmp.PixelFormat == PixelFormat.Format32bppPArgb)
                return FromBitmap(bmp);
            using (var tmp = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(tmp)) g.DrawImageUnscaled(bmp, 0, 0);
                return FromBitmap(tmp);
            }
        }
    }

    /// <summary>Lazily created, reused OCR engine.</summary>
    static class RapidOcr
    {
        static RapidOcrEngine _engine;
        static readonly object _gate = new object();

        public static string Dir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rapidocr"); }
        }

        public static bool IsAvailable()
        {
            try
            {
                return File.Exists(DetModelPath) && File.Exists(RecModelPath);
            }
            catch { return false; }
        }

        public static string DetModelPath
        {
            get
            {
                string custom = Path.Combine(Dir, "det.onnx");
                if (File.Exists(custom)) return custom;
                return Path.Combine(Dir, "PP-OCRv6_det_small.onnx");
            }
        }

        public static string RecModelPath
        {
            get
            {
                string custom = Path.Combine(Dir, "rec.onnx");
                if (File.Exists(custom)) return custom;
                return Path.Combine(Dir, "PP-OCRv6_rec_small.onnx");
            }
        }

        public static RapidOcrEngine Engine
        {
            get
            {
                lock (_gate)
                {
                    if (_engine == null) _engine = new RapidOcrEngine(DetModelPath, RecModelPath);
                    return _engine;
                }
            }
        }

        public static string EngineInfo
        {
            get { return "RapidOCR (PP-OCRv6 small)"; }
        }

        /// <summary>Recognize a screen capture. Returns rows in reading order.</summary>
        public static List<RapidOcrLine> Recognize(Bitmap bmp, int maxSide)
        {
            var engine = Engine;
            return engine.Recognize(bmp, maxSide);
        }

        public static List<RapidOcrLine> Recognize(Bitmap bmp)
        {
            return Recognize(bmp, 960);
        }

        /// <summary>
        /// Join lines into plain text: cells that share a row are separated by a
        /// space, rows by a newline. OcrService.CleanText then drops spaces that
        /// touch CJK characters, so Japanese prose comes out unspaced.
        /// </summary>
        public static string ToText(List<RapidOcrLine> lines)
        {
            if (lines == null || lines.Count == 0) return "";
            var sb = new StringBuilder();
            int row = int.MinValue;
            foreach (var l in lines)
            {
                if (l.Row != row)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    row = l.Row;
                }
                else if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(l.Text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Build tab-separated rows for table mode. Every detected box is already a
        /// cell or a line fragment, so the gaps inside a row decide column breaks.
        /// </summary>
        public static string ToTsv(List<RapidOcrLine> lines)
        {
            if (lines == null || lines.Count == 0) return "";
            var rowIds = new List<int>();
            var rows = new List<List<RapidOcrLine>>();
            foreach (var l in lines)
            {
                int idx = rowIds.IndexOf(l.Row);
                if (idx < 0)
                {
                    rowIds.Add(l.Row);
                    rows.Add(new List<RapidOcrLine>());
                    idx = rows.Count - 1;
                }
                rows[idx].Add(l);
            }

            var sb = new StringBuilder();
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r];
                cells.Sort(delegate (RapidOcrLine a, RapidOcrLine b) { return a.Left.CompareTo(b.Left); });
                var heights = new List<int>();
                foreach (var c in cells) heights.Add(Math.Max(1, c.Height));
                heights.Sort();
                int medianH = heights[heights.Count / 2];
                int gapMin = Math.Max(8, (int)(medianH * 0.8));

                if (sb.Length > 0) sb.Append("\r\n");
                var cell = new StringBuilder();
                int prevRight = int.MinValue;
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    if (i > 0)
                    {
                        int gap = c.Left - prevRight;
                        if (gap > gapMin)
                        {
                            sb.Append(cell.ToString().Trim()).Append('\t');
                            cell.Length = 0;
                        }
                        else if (cell.Length > 0 && c.Text.Length > 0 &&
                                 !(IsCjk(cell[cell.Length - 1]) || IsCjk(c.Text[0])))
                        {
                            cell.Append(' ');
                        }
                    }
                    cell.Append(c.Text);
                    if (c.Right > prevRight) prevRight = c.Right;
                }
                sb.Append(cell.ToString().Trim());
            }
            return sb.ToString();
        }

        static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x30FF)
                || (c >= 0x3400 && c <= 0x4DBF)
                || (c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0xFF00 && c <= 0xFFEF);
        }
    }

    /// <summary>Detector + recognizer pair backed by ONNX Runtime sessions.</summary>
    sealed class RapidOcrEngine : IDisposable
    {
        readonly InferenceSession _det;
        readonly InferenceSession _rec;
        readonly string _detInput;
        readonly string _recInput;
        readonly string[] _charset;
        bool _disposed;

        // Detector tuning (PaddleOCR defaults, validated on Japanese screen text).
        const int DetLimitSide = 960;    // long side cap before the DB network (PaddleOCR default)
        const float DetThresh = 0.3f;    // binarisation threshold
        const float BoxThresh = 0.6f;    // mean-probability filter per box
        const float UnclipRatio = 1.5f;
        const int MinBoxArea = 8;

        const int RecHeight = 48;
        static readonly int[] CanvasBuckets = { 320, 512, 768, 1024, 1280, 1536, 1920 };
        const int BatchSize = 6;

        // last-run timings, handy for the --selftest diagnostics
        public long LastDetMs;
        public long LastRecMs;

        public RapidOcrEngine(string detPath, string recPath)
        {
            _det = CreateSession(detPath);
            _rec = CreateSession(recPath);
            _detInput = _det.InputMetadata.Keys.First();
            _recInput = _rec.InputMetadata.Keys.First();

            string chars = "";
            IDictionary<string, string> meta = _rec.ModelMetadata.CustomMetadataMap;
            if (meta != null && meta.ContainsKey("character")) chars = meta["character"];
            _charset = chars.Length > 0 ? chars.Split('\n') : new string[0];
        }

        static InferenceSession CreateSession(string path)
        {
            var opt = new SessionOptions();
            try
            {
                opt.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                // Keep CPU and memory modest: this must stay usable on 8 GB office PCs.
                int cores = Environment.ProcessorCount;
                opt.IntraOpNumThreads = Math.Max(1, Math.Min(4, cores));
                opt.InterOpNumThreads = 1;
                opt.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opt.EnableCpuMemArena = false;   // keep the resident set small on 8 GB PCs
            }
            catch { }
            return new InferenceSession(path, opt);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _det.Dispose(); } catch { }
            try { _rec.Dispose(); } catch { }
        }

        // ---------------------------------------------------------------------
        // public API
        // ---------------------------------------------------------------------
        public List<RapidOcrLine> Recognize(Bitmap bmp, int maxSide)
        {
            var img = RapidImage.FromBitmap32(bmp);
            return Recognize(img, maxSide);
        }

        public List<RapidOcrLine> Recognize(RapidImage img, int maxSide)
        {
            int limit = maxSide > 0 ? maxSide : DetLimitSide;
            if (limit > DetLimitSide) limit = DetLimitSide;
            var swDet = System.Diagnostics.Stopwatch.StartNew();
            List<DetBox> dets = Detect(img, limit);
            swDet.Stop();
            LastDetMs = swDet.ElapsedMilliseconds;
            if (dets.Count == 0) return new List<RapidOcrLine>();

            dets = OrderQuads(dets);
            var crops = new List<RapidImage>(dets.Count);
            var used = new List<DetBox>(dets.Count);
            foreach (var d in dets)
            {
                var c = WarpQuad(img, d.Quad);
                if (c == null || c.W < 2 || c.H < 2) continue;
                crops.Add(c);
                used.Add(d);
            }
            var swRec = System.Diagnostics.Stopwatch.StartNew();
            var texts = RecognizeCrops(crops);
            swRec.Stop();
            LastRecMs = swRec.ElapsedMilliseconds;

            var lines = new List<RapidOcrLine>();
            for (int i = 0; i < used.Count && i < texts.Count; i++)
            {
                string t = texts[i];
                if (string.IsNullOrEmpty(t)) continue;
                var line = new RapidOcrLine();
                line.Text = t;
                line.Score = used[i].Score;
                line.Row = used[i].Row;
                float[] q = used[i].Quad;
                line.Quad = q;
                float x0 = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6]));
                float x1 = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
                float y0 = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7]));
                float y1 = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
                line.Left = (int)Math.Floor(x0);
                line.Top = (int)Math.Floor(y0);
                line.Right = (int)Math.Ceiling(x1);
                line.Bottom = (int)Math.Ceiling(y1);
                lines.Add(line);
            }
            return lines;
        }

        // ---------------------------------------------------------------------
        // detection
        // ---------------------------------------------------------------------
        List<DetBox> Detect(RapidImage img, int limit)
        {
            int w = img.W, h = img.H;
            double ratio = Math.Min(1.0, limit / (double)Math.Max(w, h));
            int rw = Math.Max(32, (int)Math.Round(w * ratio));
            int rh = Math.Max(32, (int)Math.Round(h * ratio));
            // keep the DB network's stride happy
            int pw = (32 - rw % 32) % 32;
            int ph = (32 - rh % 32) % 32;
            int tw = rw + pw, th = rh + ph;

            var resized = RapidResize.Resize(img, rw, rh);
            var data = new float[3 * th * tw];
            FillDetInput(resized, data, th, tw);
            var tensor = new DenseTensor<float>(data, new[] { 1, 3, th, tw });

            using (var results = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detInput, tensor) }))
            {
                var outT = results.First().AsTensor<float>();
                float[] prob = outT.ToArray();
                // output is [1,1,th,tw]
                return DbPostProcess(prob, tw, th, ratio, w, h);
            }
        }

        static void FillDetInput(RapidImage resized, float[] data, int th, int tw)
        {
            // BGR order + PaddleOCR normalisation, laid out as NCHW.
            const float meanB = 0.485f, meanG = 0.456f, meanR = 0.406f;
            const float stdB = 0.229f, stdG = 0.224f, stdR = 0.225f;
            int plane = tw * th;
            // PaddleOCR normalises *after* zero-padding, so the padding is not 0
            // in tensor space but (0 - mean) / std.
            float padB = (0f - meanB) / stdB;
            float padG = (0f - meanG) / stdG;
            float padR = (0f - meanR) / stdR;
            for (int i = 0; i < plane; i++)
            {
                data[i] = padB;
                data[plane + i] = padG;
                data[plane * 2 + i] = padR;
            }

            byte[] src = resized.Rgb;
            int rw = resized.W, rh = resized.H;
            for (int y = 0; y < rh; y++)
            {
                int srow = y * rw * 3;
                int drow = y * tw;
                for (int x = 0; x < rw; x++)
                {
                    int s = srow + x * 3;
                    int d = drow + x;
                    float b = src[s + 2] / 255f, g = src[s + 1] / 255f, r = src[s] / 255f;
                    data[d] = (b - meanB) / stdB;
                    data[plane + d] = (g - meanG) / stdG;
                    data[plane * 2 + d] = (r - meanR) / stdR;
                }
            }
        }

        List<DetBox> DbPostProcess(float[] prob, int tw, int th, double ratio, int ow, int oh)
        {
            var mask = new bool[tw * th];
            for (int i = 0; i < mask.Length; i++) mask[i] = prob[i] > DetThresh;

            int[] labels;
            int count;
            ComponentLabel(mask, tw, th, out labels, out count);
            var boxes = new List<DetBox>();

            if (count == 0) return boxes;

            var sum = new double[count];
            var cnt = new int[count];
            var minX = new int[count];
            var minY = new int[count];
            var maxX = new int[count];
            var maxY = new int[count];
            for (int i = 0; i < count; i++)
            {
                minX[i] = int.MaxValue; minY[i] = int.MaxValue;
                maxX[i] = int.MinValue; maxY[i] = int.MinValue;
            }
            for (int y = 0; y < th; y++)
            {
                int row = y * tw;
                for (int x = 0; x < tw; x++)
                {
                    int lab = labels[row + x];
                    if (lab < 0) continue;
                    sum[lab] += prob[row + x];
                    cnt[lab]++;
                    if (x < minX[lab]) minX[lab] = x;
                    if (x > maxX[lab]) maxX[lab] = x;
                    if (y < minY[lab]) minY[lab] = y;
                    if (y > maxY[lab]) maxY[lab] = y;
                }
            }

            for (int lab = 0; lab < count; lab++)
            {
                if (cnt[lab] < MinBoxArea) continue;
                if (sum[lab] / cnt[lab] < BoxThresh) continue;
                int bw = maxX[lab] - minX[lab] + 1;
                int bh = maxY[lab] - minY[lab] + 1;
                if (bw < 2 || bh < 2) continue;

                // collect the component's pixels once (needed for the rotated rect)
                var list = new List<int>(cnt[lab]);
                for (int y = minY[lab]; y <= maxY[lab]; y++)
                {
                    int row = y * tw;
                    for (int x = minX[lab]; x <= maxX[lab]; x++)
                        if (labels[row + x] == lab) list.Add(x | (y << 16));
                }

                float cx, cy, rw, rh, ang;
                MinAreaRect(list, out cx, out cy, out rw, out rh, out ang);
                if (Math.Min(rw, rh) < 2) continue;

                // pyclipper-style offset used by PaddleOCR's unclip
                float dist = rw * rh * UnclipRatio / Math.Max(1e-6f, 2f * (rw + rh));
                float nw = rw + 2 * dist, nh = rh + 2 * dist;
                float[] q = RectQuad(cx, cy, nw, nh, ang);
                q = OrderQuad(q);

                for (int k = 0; k < 4; k++)
                {
                    q[k * 2] = (float)Math.Max(0, Math.Min(ow, q[k * 2] / ratio));
                    q[k * 2 + 1] = (float)Math.Max(0, Math.Min(oh, q[k * 2 + 1] / ratio));
                }
                float qw = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6])) -
                           Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6]));
                float qh = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7])) -
                           Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7]));
                if (qw < 3 || qh < 3) continue;
                var box = new DetBox();
                box.Quad = q;
                box.Score = (float)(sum[lab] / cnt[lab]);
                boxes.Add(box);
            }
            return boxes;
        }

        // --- connected components (8-connectivity) over a boolean mask ----------
        static void ComponentLabel(bool[] mask, int w, int h, out int[] labels, out int count)
        {
            labels = new int[w * h];
            for (int i = 0; i < labels.Length; i++) labels[i] = -1;

            int[] parent = new int[1024];
            int nextId = 0;
            var rowRuns = new List<int[]>(h);     // flattened [start, endExclusive, id]
            int[] prevS = null, prevE = null, prevId = null;

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                var runs = new List<int>();
                int x = 0;
                while (x < w)
                {
                    if (!mask[row + x]) { x++; continue; }
                    int s = x;
                    while (x < w && mask[row + x]) x++;
                    int e = x;
                    if (nextId >= parent.Length) Array.Resize(ref parent, parent.Length * 2);
                    parent[nextId] = nextId;
                    runs.Add(s); runs.Add(e); runs.Add(nextId);
                    nextId++;
                }
                int[] flat = runs.ToArray();
                rowRuns.Add(flat);
                int nRuns = flat.Length / 3;
                if (prevS != null)
                {
                    for (int a = 0; a < nRuns; a++)
                    {
                        int s = flat[a * 3], e = flat[a * 3 + 1], id = flat[a * 3 + 2];
                        for (int b = 0; b < prevS.Length; b++)
                            if (prevS[b] <= e && prevE[b] >= s) Union(parent, id, prevId[b]);
                    }
                }
                prevS = new int[nRuns];
                prevE = new int[nRuns];
                prevId = new int[nRuns];
                for (int a = 0; a < nRuns; a++)
                {
                    prevS[a] = flat[a * 3];
                    prevE[a] = flat[a * 3 + 1];
                    prevId[a] = flat[a * 3 + 2];
                }
            }

            var map = new Dictionary<int, int>();
            count = 0;
            for (int y = 0; y < h; y++)
            {
                int[] flat = rowRuns[y];
                int row = y * w;
                for (int a = 0; a < flat.Length; a += 3)
                {
                    int root = Find(parent, flat[a + 2]);
                    int lab;
                    if (!map.TryGetValue(root, out lab))
                    {
                        lab = count++;
                        map[root] = lab;
                    }
                    for (int x = flat[a]; x < flat[a + 1]; x++) labels[row + x] = lab;
                }
            }
        }

        static int Find(int[] parent, int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        // --- geometry ----------------------------------------------------------
        static void MinAreaRect(List<int> pts, out float cx, out float cy, out float w, out float h, out float ang)
        {
            int n = pts.Count;
            var xs = new float[n];
            var ys = new float[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = pts[i] & 0xFFFF;
                ys[i] = pts[i] >> 16;
            }
            // convex hull (monotone chain)
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, delegate (int a, int b)
            {
                int c = xs[a].CompareTo(xs[b]);
                return c != 0 ? c : ys[a].CompareTo(ys[b]);
            });
            var hull = new List<int>(n * 2);
            for (int i = 0; i < n; i++)
            {
                int p = idx[i];
                while (hull.Count >= 2 && Cross(xs[hull[hull.Count - 2]], ys[hull[hull.Count - 2]],
                                                xs[hull[hull.Count - 1]], ys[hull[hull.Count - 1]],
                                                xs[p], ys[p]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                int p = idx[i];
                while (hull.Count >= lower && Cross(xs[hull[hull.Count - 2]], ys[hull[hull.Count - 2]],
                                                    xs[hull[hull.Count - 1]], ys[hull[hull.Count - 1]],
                                                    xs[p], ys[p]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);

            if (hull.Count < 3)
            {
                float x0 = xs.Min(), x1 = xs.Max(), y0 = ys.Min(), y1 = ys.Max();
                cx = (x0 + x1) / 2f; cy = (y0 + y1) / 2f;
                w = Math.Max(1f, x1 - x0); h = Math.Max(1f, y1 - y0); ang = 0f;
                return;
            }

            float bestArea = float.MaxValue;
            cx = 0; cy = 0; w = 1; h = 1; ang = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                int a = hull[i], b = hull[(i + 1) % hull.Count];
                float dx = xs[b] - xs[a], dy = ys[b] - ys[a];
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6f) continue;
                float ux = dx / len, uy = dy / len;
                float vx = -uy, vy = ux;
                float minU = float.MaxValue, maxU = float.MinValue;
                float minV = float.MaxValue, maxV = float.MinValue;
                for (int k = 0; k < hull.Count; k++)
                {
                    int p = hull[k];
                    float pu = xs[p] * ux + ys[p] * uy;
                    float pv = xs[p] * vx + ys[p] * vy;
                    if (pu < minU) minU = pu;
                    if (pu > maxU) maxU = pu;
                    if (pv < minV) minV = pv;
                    if (pv > maxV) maxV = pv;
                }
                float bw = maxU - minU, bh = maxV - minV;
                float area = bw * bh;
                if (area < bestArea)
                {
                    bestArea = area;
                    float cu = (maxU + minU) / 2f, cv = (maxV + minV) / 2f;
                    cx = cu * ux + cv * vx;
                    cy = cu * uy + cv * vy;
                    w = Math.Max(1f, bw);
                    h = Math.Max(1f, bh);
                    ang = (float)Math.Atan2(uy, ux);
                }
            }
        }

        static float Cross(float ox, float oy, float ax, float ay, float bx, float by)
        {
            return (ax - ox) * (by - oy) - (ay - oy) * (bx - ox);
        }

        static float[] RectQuad(float cx, float cy, float w, float h, float ang)
        {
            float c = (float)Math.Cos(ang), s = (float)Math.Sin(ang);
            float dx = w / 2f, dy = h / 2f;
            float[][] local = new float[][] {
                new float[] { -dx, -dy }, new float[] { dx, -dy },
                new float[] { dx, dy }, new float[] { -dx, dy }
            };
            var q = new float[8];
            for (int i = 0; i < 4; i++)
            {
                float lx = local[i][0], ly = local[i][1];
                q[i * 2] = cx + lx * c - ly * s;
                q[i * 2 + 1] = cy + lx * s + ly * c;
            }
            return q;
        }

        static float[] OrderQuad(float[] q)
        {
            // order as tl, tr, br, bl
            var pts = new int[] { 0, 1, 2, 3 };
            float[] sum = new float[4], diff = new float[4];
            for (int i = 0; i < 4; i++)
            {
                sum[i] = q[i * 2] + q[i * 2 + 1];
                diff[i] = q[i * 2 + 1] - q[i * 2];
            }
            int tl = 0, br = 0, tr = 0, bl = 0;
            for (int i = 1; i < 4; i++)
            {
                if (sum[i] < sum[tl]) tl = i;
                if (sum[i] > sum[br]) br = i;
                if (diff[i] < diff[tr]) tr = i;
                if (diff[i] > diff[bl]) bl = i;
            }
            var o = new float[8];
            int[] order = new int[] { tl, tr, br, bl };
            for (int i = 0; i < 4; i++)
            {
                o[i * 2] = q[order[i] * 2];
                o[i * 2 + 1] = q[order[i] * 2 + 1];
            }
            return o;
        }

        // --- perspective crop --------------------------------------------------
        static RapidImage WarpQuad(RapidImage src, float[] q)
        {
            float wTop = Dist(q[0], q[1], q[2], q[3]);
            float wBot = Dist(q[4], q[5], q[6], q[7]);
            float hLeft = Dist(q[0], q[1], q[6], q[7]);
            float hRight = Dist(q[2], q[3], q[4], q[5]);
            float srcW = Math.Max(wTop, wBot);
            float srcH = Math.Max(1f, (hLeft + hRight) / 2f);
            int outW = (int)Math.Round(srcW * RecHeight / srcH);
            if (outW < 1) outW = 1;
            if (outW > CanvasBuckets[CanvasBuckets.Length - 1]) outW = CanvasBuckets[CanvasBuckets.Length - 1];

            // homography mapping destination pixels back to source pixels
            float[] dst = { 0, 0, outW, 0, outW, RecHeight, 0, RecHeight };
            double[] m = Homography(dst, q);
            if (m == null) return null;

            var crop = new RapidImage(outW, RecHeight);
            byte[] s = src.Rgb, d = crop.Rgb;
            for (int y = 0; y < RecHeight; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    double den = m[6] * x + m[7] * y + m[8];
                    if (Math.Abs(den) < 1e-12) continue;
                    double sx = (m[0] * x + m[1] * y + m[2]) / den;
                    double sy = (m[3] * x + m[4] * y + m[5]) / den;
                    int di = (y * outW + x) * 3;
                    SampleBilinear(src, sx, sy, d, di);
                }
            }
            return crop;
        }

        static float Dist(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        static double[] Homography(float[] src, float[] dst)
        {
            // solve the 8x8 system (h33 fixed to 1)
            var a = new double[8, 9];
            for (int i = 0; i < 4; i++)
            {
                double x = src[i * 2], y = src[i * 2 + 1];
                double u = dst[i * 2], v = dst[i * 2 + 1];
                a[i * 2, 0] = x; a[i * 2, 1] = y; a[i * 2, 2] = 1;
                a[i * 2, 6] = -u * x; a[i * 2, 7] = -u * y; a[i * 2, 8] = u;
                a[i * 2 + 1, 3] = x; a[i * 2 + 1, 4] = y; a[i * 2 + 1, 5] = 1;
                a[i * 2 + 1, 6] = -v * x; a[i * 2 + 1, 7] = -v * y; a[i * 2 + 1, 8] = v;
            }
            if (!Gauss(a)) return null;
            var m = new double[9];
            for (int i = 0; i < 8; i++) m[i] = a[i, 8];
            m[8] = 1;
            return m;
        }

        static bool Gauss(double[,] a)
        {
            int n = 8;
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-10) return false;
                if (piv != col)
                    for (int k = col; k <= n; k++) { double t = a[col, k]; a[col, k] = a[piv, k]; a[piv, k] = t; }
                double p = a[col, col];
                for (int k = col; k <= n; k++) a[col, k] /= p;
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = a[r, col];
                    if (f == 0) continue;
                    for (int k = col; k <= n; k++) a[r, k] -= f * a[col, k];
                }
            }
            return true;
        }

        static void SampleBilinear(RapidImage src, double sx, double sy, byte[] dst, int di)
        {
            int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
            double fx = sx - x0, fy = sy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
            if (x1 < 0) x1 = 0; if (y1 < 0) y1 = 0;
            if (x0 >= src.W) x0 = src.W - 1; if (x1 >= src.W) x1 = src.W - 1;
            if (y0 >= src.H) y0 = src.H - 1; if (y1 >= src.H) y1 = src.H - 1;
            int i00 = (y0 * src.W + x0) * 3, i01 = (y0 * src.W + x1) * 3;
            int i10 = (y1 * src.W + x0) * 3, i11 = (y1 * src.W + x1) * 3;
            byte[] s = src.Rgb;
            for (int c = 0; c < 3; c++)
            {
                double top = s[i00 + c] * (1 - fx) + s[i01 + c] * fx;
                double bot = s[i10 + c] * (1 - fx) + s[i11 + c] * fx;
                double v = top * (1 - fy) + bot * fy;
                dst[di + c] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
        }

        // ---------------------------------------------------------------------
        // recognition
        // ---------------------------------------------------------------------
        List<string> RecognizeCrops(List<RapidImage> crops)
        {
            var result = new List<string>(crops.Count);
            for (int i = 0; i < crops.Count; i++) result.Add("");
            if (crops.Count == 0) return result;

            // Bucket by canvas width: the recognizer degrades when a short line is
            // padded into a much wider canvas, so keep padding close to the content.
            var buckets = new Dictionary<int, List<int>>();
            for (int i = 0; i < crops.Count; i++)
            {
                int canvas = CanvasFor(crops[i].W);
                List<int> list;
                if (!buckets.TryGetValue(canvas, out list))
                {
                    list = new List<int>();
                    buckets[canvas] = list;
                }
                list.Add(i);
            }

            foreach (var kv in buckets)
            {
                int canvas = kv.Key;
                var ids = kv.Value;
                for (int s = 0; s < ids.Count; s += BatchSize)
                {
                    int n = Math.Min(BatchSize, ids.Count - s);
                    var data = new float[n * 3 * RecHeight * canvas];
                    for (int k = 0; k < n; k++) FillRecInput(crops[ids[s + k]], data, canvas, k, n);
                    var tensor = new DenseTensor<float>(data, new[] { n, 3, RecHeight, canvas });
                    using (var results = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recInput, tensor) }))
                    {
                        var outT = results.First().AsTensor<float>();
                        int[] outDims = outT.Dimensions.ToArray();
                        int T = outDims[outDims.Length - 2];
                        int C = outDims[outDims.Length - 1];
                        float[] pred = outT.ToArray();
                        for (int k = 0; k < n; k++)
                            result[ids[s + k]] = CtcDecode(pred, k * T * C, T, C);
                    }
                }
            }
            return result;
        }

        static int CanvasFor(int width)
        {
            for (int i = 0; i < CanvasBuckets.Length; i++)
                if (width <= CanvasBuckets[i]) return CanvasBuckets[i];
            return CanvasBuckets[CanvasBuckets.Length - 1];
        }

        void FillRecInput(RapidImage crop, float[] buf, int canvas, int index, int batch)
        {
            // resized to 48 px tall, then right-padded with zeros to the canvas width
            int nw = crop.W;
            if (nw > canvas) nw = canvas;
            int plane = canvas * RecHeight;
            int baseIdx = index * 3 * plane;
            byte[] s = crop.Rgb;
            int rows = Math.Min(RecHeight, crop.H);
            for (int y = 0; y < rows; y++)
            {
                int srow = y * crop.W * 3;
                int drow = baseIdx + y * canvas;
                for (int x = 0; x < nw; x++)
                {
                    int si = srow + x * 3;
                    // BGR, scaled to [-1, 1]
                    buf[drow + x] = s[si + 2] / 127.5f - 1f;
                    buf[drow + plane + x] = s[si + 1] / 127.5f - 1f;
                    buf[drow + plane * 2 + x] = s[si] / 127.5f - 1f;
                }
            }
        }

        string CtcDecode(float[] data, int offset, int T, int C)
        {
            var sb = new System.Text.StringBuilder();
            int prev = -1;
            for (int t = 0; t < T; t++)
            {
                int best = 0;
                float bestV = data[offset + t * C];
                for (int c = 1; c < C; c++)
                {
                    float v = data[offset + t * C + c];
                    if (v > bestV) { bestV = v; best = c; }
                }
                if (best != 0 && best != prev)
                {
                    int ci = best - 1;
                    if (ci < _charset.Length) sb.Append(_charset[ci]);
                    else sb.Append(' ');
                }
                prev = best;
            }
            return sb.ToString().Trim();
        }

        // ---------------------------------------------------------------------
        // reading order
        // ---------------------------------------------------------------------
        static List<DetBox> OrderQuads(List<DetBox> quads)
        {
            var items = new List<RowItem>();
            foreach (var d in quads)
            {
                var q = d.Quad;
                var it = new RowItem();
                it.Det = d;
                it.Quad = q;
                float cy = (q[1] + q[3] + q[5] + q[7]) / 4f;
                float cx = (q[0] + q[2] + q[4] + q[6]) / 4f;
                float y0 = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7]));
                float y1 = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
                it.Cy = cy; it.Cx = cx; it.H = y1 - y0;
                items.Add(it);
            }
            items.Sort(delegate (RowItem a, RowItem b) { return a.Cy.CompareTo(b.Cy); });

            var rows = new List<List<RowItem>>();
            var rowCy = new List<float>();
            var rowH = new List<float>();
            foreach (var it in items)
            {
                int target = -1;
                float bestDiff = float.MaxValue;
                for (int r = 0; r < rows.Count; r++)
                {
                    float tol = Math.Max(4f, 0.5f * Math.Max(rowH[r], it.H));
                    float diff = Math.Abs(it.Cy - rowCy[r]);
                    if (diff <= tol && diff < bestDiff) { bestDiff = diff; target = r; }
                }
                if (target < 0)
                {
                    rows.Add(new List<RowItem>());
                    rowCy.Add(it.Cy);
                    rowH.Add(it.H);
                    target = rows.Count - 1;
                }
                rows[target].Add(it);
                float sum = 0;
                for (int k = 0; k < rows[target].Count; k++) sum += rows[target][k].Cy;
                rowCy[target] = sum / rows[target].Count;
                if (it.H > rowH[target]) rowH[target] = it.H;
            }

            var order = new List<int>();
            for (int r = 0; r < rows.Count; r++) order.Add(r);
            order.Sort(delegate (int a, int b) { return rowCy[a].CompareTo(rowCy[b]); });

            var outp = new List<DetBox>();
            for (int oi = 0; oi < order.Count; oi++)
            {
                var row = rows[order[oi]];
                row.Sort(delegate (RowItem a, RowItem b) { return a.Cx.CompareTo(b.Cx); });
                foreach (var it in row)
                {
                    it.Det.Row = oi;
                    outp.Add(it.Det);
                }
            }
            return outp;
        }

        sealed class RowItem
        {
            public DetBox Det;
            public float[] Quad;
            public float Cy, Cx, H;
        }
    }

    /// <summary>A detected text box: ordered quad plus DB confidence.</summary>
    sealed class DetBox
    {
        public float[] Quad;
        public float Score;
        public int Row;
    }

    /// <summary>Bilinear resize on RGB24 buffers (cv2.INTER_LINEAR style centres).</summary>
    static class RapidResize
    {
        public static RapidImage Resize(RapidImage src, int dw, int dh)
        {
            if (dw == src.W && dh == src.H)
            {
                var copy = new RapidImage(src.W, src.H);
                Buffer.BlockCopy(src.Rgb, 0, copy.Rgb, 0, src.Rgb.Length);
                return copy;
            }
            var dst = new RapidImage(dw, dh);
            double sx = src.W / (double)dw;
            double sy = src.H / (double)dh;
            byte[] s = src.Rgb, d = dst.Rgb;
            int sw = src.W, sh = src.H;
            for (int y = 0; y < dh; y++)
            {
                double fy = (y + 0.5) * sy - 0.5;
                if (fy < 0) fy = 0;
                int y0 = (int)fy;
                double wy = fy - y0;
                int y1 = y0 + 1;
                if (y1 >= sh) { y1 = sh - 1; if (y0 >= sh) y0 = sh - 1; }
                int drow = y * dw * 3;
                for (int x = 0; x < dw; x++)
                {
                    double fx = (x + 0.5) * sx - 0.5;
                    if (fx < 0) fx = 0;
                    int x0 = (int)fx;
                    double wx = fx - x0;
                    int x1 = x0 + 1;
                    if (x1 >= sw) { x1 = sw - 1; if (x0 >= sw) x0 = sw - 1; }
                    int i00 = (y0 * sw + x0) * 3, i01 = (y0 * sw + x1) * 3;
                    int i10 = (y1 * sw + x0) * 3, i11 = (y1 * sw + x1) * 3;
                    int di = drow + x * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        double top = s[i00 + c] * (1 - wx) + s[i01 + c] * wx;
                        double bot = s[i10 + c] * (1 - wx) + s[i11 + c] * wx;
                        double v = top * (1 - wy) + bot * wy;
                        d[di + c] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                    }
                }
            }
            return dst;
        }
    }
}


