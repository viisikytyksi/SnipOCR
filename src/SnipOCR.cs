using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SnipOCR
{
    // ---------------------------------------------------------------------
    // Entry point
    // ---------------------------------------------------------------------
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        static readonly IntPtr PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);
        const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        static void Main(string[] args)
        {
            // Per-Monitor DPI aware so screen capture uses true physical pixels.
            try { if (!SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)) SetProcessDPIAware(); }
            catch { try { SetProcessDPIAware(); } catch { } }

            // Diagnostics for locked-down work PCs: SnipOCR.exe --selftest <image>
            if (args != null && args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                try { Console.WriteLine(AppInfo.Title); } catch { }
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                Environment.ExitCode = SelfTest.Run(args);
                return;
            }

            bool createdNew;
            using (var mtx = new Mutex(true, "SnipOCR_SingleInstance_v1", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("SnipOCR は既に起動しています。", "SnipOCR",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    // ---------------------------------------------------------------------
    // Headless self-test: verifies that the OCR stack works on this PC.
    //   SnipOCR.exe --selftest               ... engine availability only
    //   SnipOCR.exe --selftest shot.png      ... also OCR one image to stdout
    // ---------------------------------------------------------------------
    static class SelfTest
    {
        public static int Run(string[] args)
        {
            int code = 0;
            try
            {
                // a winexe has no console of its own; write UTF-8 bytes straight to
                // the inherited handle so Japanese output survives redirection.
                try
                {
                    var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                    stdout.AutoFlush = true;
                    Console.SetOut(stdout);
                }
                catch { }
                var cfg = Settings.Load();
                Console.WriteLine(AppInfo.Title + " self-test");
                Console.WriteLine("  base dir      : " + AppDomain.CurrentDomain.BaseDirectory);
                Console.WriteLine("  engine setting: " + cfg.Engine);
                Console.WriteLine("  rapidocr dir  : " + RapidOcr.Dir);
                Console.WriteLine("  det model     : " + RapidOcr.DetModelPath + " (" + (File.Exists(RapidOcr.DetModelPath) ? "found" : "MISSING") + ")");
                Console.WriteLine("  rec model     : " + RapidOcr.RecModelPath + " (" + (File.Exists(RapidOcr.RecModelPath) ? "found" : "MISSING") + ")");
                Console.WriteLine("  rapidocr ready: " + RapidOcr.IsAvailable());
                Console.WriteLine("  active engine : " + OcrService.EngineInfo(cfg));
                string warn = OcrService.SelfCheck(cfg);
                if (warn != null) Console.WriteLine("  warning       : " + warn);
                if (!RapidOcr.IsAvailable()) code = 2;

                if (args.Length > 1)
                {
                    string path = args[1];
                    if (!File.Exists(path))
                    {
                        Console.WriteLine("  image not found: " + path);
                        return 3;
                    }
                    using (var bmp = new Bitmap(path))
                    {
                        var sw = Stopwatch.StartNew();
                        var res = OcrService.RecognizeWithFormats(bmp, cfg);
                        sw.Stop();
                        Console.WriteLine("  image         : " + path + " " + bmp.Width + "x" + bmp.Height);
                        Console.WriteLine("  result engine : " + res.Engine + " in " + sw.ElapsedMilliseconds + "ms");
                        Console.WriteLine("  --- text ---");
                        Console.WriteLine(res.Text);
                        if (res.ExcelTsv.Length > 0)
                        {
                            Console.WriteLine("  --- excel tsv ---");
                            Console.WriteLine(res.ExcelTsv);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Console.WriteLine("  FAILED: " + ex.GetType().Name + ": " + ex.Message); } catch { }
                return 4;
            }
            return code;
        }
    }

    // ---------------------------------------------------------------------
    // Product identity (version comes from AssemblyInfo.cs)
    // ---------------------------------------------------------------------
    static class AppInfo
    {
        public static readonly string Version = ReadVersion();

        static string ReadVersion()
        {
            try
            {
                var v = typeof(AppInfo).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "1.1.0"; }
        }

        public static string Title { get { return "SnipOCR " + Version; } }
    }

    // ---------------------------------------------------------------------
    // App icon (embedded app.ico; falls back to the system icon)
    // ---------------------------------------------------------------------
    static class AppIcons
    {
        const string ResourceName = "SnipOCR.app.ico";

        // tray needs the small (usually 16x16) frame; windows want a larger one
        public static Icon Tray { get { return Load(SystemInformation.SmallIconSize); } }
        public static Icon Window { get { return Load(new Size(32, 32)); } }

        static Icon Load(Size size)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(ResourceName))
                {
                    if (s != null) return new Icon(s, size);
                }
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            return SystemIcons.Application;
        }
    }

    // ---------------------------------------------------------------------
    // Settings (simple key=value file next to the exe, or %LOCALAPPDATA%)
    // ---------------------------------------------------------------------
    class Settings
    {
        public uint Modifiers = MOD_CONTROL | MOD_SHIFT; // Ctrl+Shift
        public uint Key = 0x53;                          // 'S'
        public string HotkeyText = "Ctrl+Shift+S";
        public string Language = "auto";                 // auto | ja | en | ...
        public string Mode = "normal";                  // normal | line | table | sparse | small
        public string CopyFormat = "auto";              // auto | plain | excel | markdown | oneline
        public bool ShowWindow = true;                   // show result window
        public bool CopyToClipboard = true;

        // OCR engine selection
        public string Engine = "auto";                   // auto | rapidocr | winrt | tesseract
        public string TesseractLangs = "jpn+eng";        // tesseract -l value
        public int TesseractPsm = 6;                      // page segmentation mode
        public bool Binarize = false;                    // grayscale+Otsu before OCR (winrt)

        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8;

        static string PathPrimary
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt"); }
        }
        static string PathFallback
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SnipOCR", "settings.txt");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            string file = File.Exists(PathPrimary) ? PathPrimary :
                          (File.Exists(PathFallback) ? PathFallback : null);
            if (file != null)
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(file))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "language": s.Language = v; break;
                            case "mode": s.Mode = v.ToLowerInvariant(); break;
                            case "copyformat": s.CopyFormat = v.ToLowerInvariant(); break;
                            case "showwindow": s.ShowWindow = ParseBool(v, s.ShowWindow); break;
                            case "copytoclipboard": s.CopyToClipboard = ParseBool(v, s.CopyToClipboard); break;
                            case "hotkey": s.ParseHotkey(v); break;
                            case "engine": s.Engine = v.ToLowerInvariant(); break;
                            case "tesseractlangs": if (v.Length > 0) s.TesseractLangs = v; break;
                            case "tesseractpsm": { int psm; if (int.TryParse(v, out psm)) s.TesseractPsm = psm; break; }
                            case "binarize": s.Binarize = ParseBool(v, s.Binarize); break;
                        }
                    }
                }
                catch { /* tolerate any settings error, keep defaults */ }
            }
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + AppInfo.Title + " settings");
            sb.AppendLine("hotkey=" + HotkeyText);
            sb.AppendLine("engine=" + Engine);
            sb.AppendLine("language=" + Language);
            sb.AppendLine("mode=" + Mode);
            sb.AppendLine("copyformat=" + CopyFormat);
            sb.AppendLine("tesseractlangs=" + TesseractLangs);
            sb.AppendLine("tesseractpsm=" + TesseractPsm);
            sb.AppendLine("binarize=" + (Binarize ? "true" : "false"));
            sb.AppendLine("showwindow=" + (ShowWindow ? "true" : "false"));
            sb.AppendLine("copytoclipboard=" + (CopyToClipboard ? "true" : "false"));
            // Try next-to-exe first (portable), fall back to LOCALAPPDATA.
            try { File.WriteAllText(PathPrimary, sb.ToString()); return; }
            catch { }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathFallback));
                File.WriteAllText(PathFallback, sb.ToString());
            }
            catch { }
        }

        static bool ParseBool(string v, bool def)
        {
            v = v.ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
            if (v == "false" || v == "0" || v == "no" || v == "off") return false;
            return def;
        }

        // hotkey string like "Ctrl+Shift+S"
        void ParseHotkey(string v)
        {
            try
            {
                uint mods = 0, key = 0;
                foreach (var partRaw in v.Split('+'))
                {
                    var p = partRaw.Trim().ToLowerInvariant();
                    if (p == "ctrl" || p == "control") mods |= MOD_CONTROL;
                    else if (p == "shift") mods |= MOD_SHIFT;
                    else if (p == "alt") mods |= MOD_ALT;
                    else if (p == "win") mods |= MOD_WIN;
                    else if (p.Length == 1) key = (uint)char.ToUpperInvariant(p[0]);
                }
                if (key != 0) { Modifiers = mods; Key = key; HotkeyText = v.Trim(); }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------
    // Tray application context: holds hotkey window + NotifyIcon
    // ---------------------------------------------------------------------
    class TrayContext : ApplicationContext
    {
        readonly Settings _cfg;
        readonly HotkeyWindow _hk;
        readonly NotifyIcon _tray;
        bool _capturing;

        public TrayContext()
        {
            _cfg = Settings.Load();

            _tray = new NotifyIcon
            {
                Icon = AppIcons.Tray,
                Text = "SnipOCR  " + AppInfo.Version + "  (" + _cfg.HotkeyText + " でキャプチャ)",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("キャプチャ (" + _cfg.HotkeyText + ")", null, (s, e) => StartCapture());
            var modeMenu = new ToolStripMenuItem("OCRモード");
            AddModeMenu(modeMenu, "normal", "通常文章");
            AddModeMenu(modeMenu, "line", "1行");
            AddModeMenu(modeMenu, "table", "表 / Excel");
            AddModeMenu(modeMenu, "sparse", "散在テキスト");
            AddModeMenu(modeMenu, "small", "小さい文字");
            menu.Items.Add(modeMenu);
            var engineMenu = new ToolStripMenuItem("OCRエンジン");
            AddEngineMenu(engineMenu, "auto", "自動（RapidOCR 優先）");
            AddEngineMenu(engineMenu, "rapidocr", "RapidOCR (PP-OCRv6)");
            AddEngineMenu(engineMenu, "tesseract", "Tesseract");
            AddEngineMenu(engineMenu, "winrt", "Windows 標準");
            menu.Items.Add(engineMenu);
            var miWin = new ToolStripMenuItem("結果ウィンドウを表示")
            { Checked = _cfg.ShowWindow, CheckOnClick = true };
            miWin.CheckedChanged += (s, e) => { _cfg.ShowWindow = miWin.Checked; _cfg.Save(); };
            menu.Items.Add(miWin);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("バージョン情報", null, (s, e) =>
                MessageBox.Show(
                    AppInfo.Title + " (portable)\nOCR engine: " + OcrService.EngineInfo(_cfg) +
                    "\nHotkey: " + _cfg.HotkeyText +
                    "\nModels: " + (RapidOcr.IsAvailable() ? "rapidocr (PP-OCRv6 small)" : "rapidocr なし (WinRT/Tesseract)"),
                    AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information));
            menu.Items.Add("終了", null, (s, e) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => StartCapture();

            _hk = new HotkeyWindow(_cfg);
            _hk.HotkeyPressed += (s, e) => StartCapture();
            if (!_hk.Register())
            {
                _tray.ShowBalloonTip(4000, "SnipOCR",
                    "ホットキー " + _cfg.HotkeyText + " を登録できませんでした。トレイメニューからキャプチャできます。",
                    ToolTipIcon.Warning);
            }

            // Startup self-check for OCR availability.
            string info = OcrService.SelfCheck(_cfg);
            if (info != null)
                _tray.ShowBalloonTip(6000, "SnipOCR", info, ToolTipIcon.Warning);
        }

        void AddModeMenu(ToolStripMenuItem parent, string mode, string label)
        {
            var item = new ToolStripMenuItem(label);
            item.Checked = string.Equals(_cfg.Mode, mode, StringComparison.OrdinalIgnoreCase);
            item.Click += (s, e) =>
            {
                _cfg.Mode = mode;
                _cfg.Save();
                foreach (ToolStripItem it in parent.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null) mi.Checked = false;
                }
                item.Checked = true;
            };
            parent.DropDownItems.Add(item);
        }

        void AddEngineMenu(ToolStripMenuItem parent, string engine, string label)
        {
            var item = new ToolStripMenuItem(label);
            item.Checked = string.Equals(_cfg.Engine, engine, StringComparison.OrdinalIgnoreCase);
            item.Click += (s, e) =>
            {
                _cfg.Engine = engine;
                _cfg.Save();
                foreach (ToolStripItem it in parent.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi != null) mi.Checked = false;
                }
                item.Checked = true;
            };
            parent.DropDownItems.Add(item);
        }

        void StartCapture()
        {
            if (_capturing) return;
            _capturing = true;
            try
            {
                Rectangle vs = Overlay.VirtualScreenBounds();
                using (Bitmap shot = ScreenCapture.CaptureVirtualScreen(vs))
                {
                    Rectangle sel;
                    using (var ov = new Overlay(shot, vs))
                    {
                        if (ov.ShowDialog() != DialogResult.OK) return;
                        sel = ov.Selection; // in screenshot pixel coordinates
                    }
                    if (sel.Width < 3 || sel.Height < 3) return;

                    using (var crop = ScreenCapture.Crop(shot, sel))
                    {
                        OcrOutput result;
                        try { result = OcrService.RecognizeWithFormats(crop, _cfg); }
                        catch (Exception ex)
                        {
                            _tray.ShowBalloonTip(4000, "SnipOCR", "OCR エラー: " + ex.Message, ToolTipIcon.Error);
                            return;
                        }
                        Present(result, (Bitmap)crop.Clone());
                    }
                }
            }
            finally { _capturing = false; }
        }

        void Present(OcrOutput result, Bitmap image)
        {
            string text = result != null ? result.Text : "";
            if (string.IsNullOrWhiteSpace(text) && (result == null || string.IsNullOrWhiteSpace(result.ExcelTsv)))
            {
                if (image != null) image.Dispose();
                _tray.ShowBalloonTip(3000, "SnipOCR", "テキストを検出できませんでした。", ToolTipIcon.Info);
                return;
            }
            string clip = OcrService.SelectClipboardText(result, _cfg);
            if (_cfg.CopyToClipboard && !string.IsNullOrEmpty(clip))
            {
                for (int i = 0; i < 3; i++)
                {
                    try { Clipboard.SetText(clip); break; }
                    catch { Thread.Sleep(60); }
                }
            }
            string preview = clip.Replace("\r", " ").Replace("\n", " ").Replace("\t", " | ");
            if (preview.Length > 40) preview = preview.Substring(0, 40) + "…";
            _tray.ShowBalloonTip(2500, "コピーしました", preview, ToolTipIcon.Info);

            if (_cfg.ShowWindow)
            {
                var win = new ResultForm(result, image, _cfg);
                win.Show();
                win.Activate();
            }
            else if (image != null) image.Dispose();
        }

        void ExitApp()
        {
            _tray.Visible = false;
            _hk.Dispose();
            _tray.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _hk.Dispose(); _tray.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------------
    // Hidden window that owns the global hotkey
    // ---------------------------------------------------------------------
    class HotkeyWindow : NativeWindow, IDisposable
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 0xB001;

        readonly Settings _cfg;
        bool _registered;
        public event EventHandler HotkeyPressed;

        public HotkeyWindow(Settings cfg)
        {
            _cfg = cfg;
            CreateHandle(new CreateParams()); // message-only-ish window
        }

        public bool Register()
        {
            _registered = RegisterHotKey(Handle, HOTKEY_ID, _cfg.Modifiers, _cfg.Key);
            return _registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                var h = HotkeyPressed;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { if (_registered) UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
            try { DestroyHandle(); } catch { }
        }
    }

    // ---------------------------------------------------------------------
    // Screen capture helpers
    // ---------------------------------------------------------------------
    static class ScreenCapture
    {
        public static Bitmap CaptureVirtualScreen(Rectangle vs)
        {
            var bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.X, vs.Y, 0, 0, new Size(vs.Width, vs.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static Bitmap Crop(Bitmap src, Rectangle rect)
        {
            rect = Rectangle.Intersect(rect, new Rectangle(0, 0, src.Width, src.Height));
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(src, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            return bmp;
        }
    }

    // ---------------------------------------------------------------------
    // Selection overlay (covers all monitors)
    // ---------------------------------------------------------------------
    class Overlay : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetrics(int n);
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        public static Rectangle VirtualScreenBounds()
        {
            return new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        readonly Bitmap _shot;
        readonly Rectangle _vs;
        Point _start;
        bool _dragging;
        Rectangle _selClient = Rectangle.Empty; // client (=screenshot) pixel coords

        public Rectangle Selection { get; private set; }

        public Overlay(Bitmap shot, Rectangle vs)
        {
            _shot = shot;
            _vs = vs;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Bounds = vs;                 // physical pixels (process is DPI aware)
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            KeyPreview = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(_shot, 0, 0);
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(dim, ClientRectangle);

            if (_selClient.Width > 0 && _selClient.Height > 0)
            {
                // show the crisp original inside the selection
                g.DrawImage(_shot, _selClient, _selClient, GraphicsUnit.Pixel);
                using (var pen = new Pen(Color.FromArgb(0, 174, 239), 2))
                    g.DrawRectangle(pen, _selClient);
                string label = _selClient.Width + " x " + _selClient.Height;
                var sz = g.MeasureString(label, Font);
                float lx = _selClient.X;
                float ly = _selClient.Y - sz.Height - 2;
                if (ly < 0) ly = _selClient.Y + 2;
                using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                    g.FillRectangle(bg, lx, ly, sz.Width + 6, sz.Height + 2);
                g.DrawString(label, Font, Brushes.White, lx + 3, ly + 1);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button == MouseButtons.Left) { _dragging = true; _start = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            int x = Math.Min(_start.X, e.X), y = Math.Min(_start.Y, e.Y);
            int w = Math.Abs(e.X - _start.X), h = Math.Abs(e.Y - _start.Y);
            _selClient = new Rectangle(x, y, w, h);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !_dragging) return;
            _dragging = false;
            if (_selClient.Width < 3 || _selClient.Height < 3) { Cancel(); return; }
            Selection = _selClient; // client coords == screenshot pixel coords
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
        }

        void Cancel() { DialogResult = DialogResult.Cancel; Close(); }
    }

    class OcrOutput
    {
        public string Text = "";
        public string ExcelTsv = "";
        public string MarkdownTable = "";
        public string OneLine = "";
        public string Engine = "";
        public string Mode = "";
    }

    // ---------------------------------------------------------------------
    // OCR service (WinRT Windows.Media.Ocr)
    // ---------------------------------------------------------------------
    static class OcrService
    {
        static OcrEngine _cached;
        static string _cachedLang;

        // decide which concrete engine to use for this request
        static bool UseTesseract(Settings cfg)
        {
            string e = (cfg.Engine ?? "auto").ToLowerInvariant();
            if (e == "winrt") return false;
            if (e == "tesseract") return true;          // forced; falls back below if missing
            if (UseRapidOcr(cfg)) return false;
            return TesseractEngine.IsAvailable();       // auto: prefer bundled tesseract
        }

        // RapidOCR (PP-OCRv6) is the default when its model folder is present:
        // it is markedly more accurate than WinRT/Tesseract on small Japanese text.
        public static bool UseRapidOcr(Settings cfg)
        {
            string e = (cfg.Engine ?? "auto").ToLowerInvariant();
            if (e == "winrt" || e == "tesseract") return false;
            return RapidOcr.IsAvailable();
        }

        static bool UseRapidOcrForced(Settings cfg)
        {
            return string.Equals((cfg.Engine ?? "").Trim(), "rapidocr", StringComparison.OrdinalIgnoreCase);
        }

        public static string Recognize(Bitmap bmp, Settings cfg)
        {
            if (UseRapidOcr(cfg))
            {
                try { return RecognizeRapid(bmp, cfg); }
                catch { /* fall through to the other engines */ }
            }
            if (UseTesseract(cfg) && TesseractEngine.IsAvailable())
            {
                try { return TesseractEngine.Recognize(bmp, cfg); }
                catch { /* fall back to WinRT on any tesseract failure */ }
            }
            return RecognizeWinRt(bmp, cfg);
        }

        public static OcrOutput RecognizeWithFormats(Bitmap bmp, Settings cfg)
        {
            var o = new OcrOutput();
            o.Mode = cfg != null ? (cfg.Mode ?? "normal") : "normal";
            if (UseRapidOcr(cfg))
            {
                try
                {
                    var lines = RapidOcr.Recognize(bmp);
                    o.Text = OcrService.CleanText(RapidOcr.ToText(lines));
                    if (string.Equals(o.Mode, "table", StringComparison.OrdinalIgnoreCase))
                    {
                        o.ExcelTsv = RapidOcr.ToTsv(lines);
                        o.MarkdownTable = TableFormatter.ToMarkdownTable(o.ExcelTsv);
                    }
                    o.Engine = "RapidOCR";
                    o.OneLine = ToOneLine(o.Text);
                    return o;
                }
                catch { /* fall through */ }
            }
            if (UseTesseract(cfg) && TesseractEngine.IsAvailable())
            {
                try
                {
                    o.Text = TesseractEngine.Recognize(bmp, cfg);
                    if (string.Equals(o.Mode, "table", StringComparison.OrdinalIgnoreCase))
                    {
                        var words = TesseractEngine.RecognizeWords(bmp, cfg);
                        o.ExcelTsv = TableFormatter.ToExcelTsv(words);
                        o.MarkdownTable = TableFormatter.ToMarkdownTable(o.ExcelTsv);
                    }
                    o.Engine = "Tesseract";
                    o.OneLine = ToOneLine(o.Text);
                    return o;
                }
                catch { /* fall back to WinRT */ }
            }
            o.Text = RecognizeWinRt(bmp, cfg);
            o.Engine = "WinRT";
            o.OneLine = ToOneLine(o.Text);
            return o;
        }

        static string RecognizeRapid(Bitmap bmp, Settings cfg)
        {
            var lines = RapidOcr.Recognize(bmp);
            return CleanText(RapidOcr.ToText(lines));
        }

        public static string SelectClipboardText(OcrOutput o, Settings cfg)
        {
            if (o == null) return "";
            string f = cfg != null ? (cfg.CopyFormat ?? "auto").ToLowerInvariant() : "auto";
            if (f == "excel") return !string.IsNullOrEmpty(o.ExcelTsv) ? o.ExcelTsv : o.Text;
            if (f == "markdown") return !string.IsNullOrEmpty(o.MarkdownTable) ? o.MarkdownTable : o.Text;
            if (f == "oneline") return !string.IsNullOrEmpty(o.OneLine) ? o.OneLine : ToOneLine(o.Text);
            if (f == "plain") return o.Text;
            if (cfg != null && string.Equals(cfg.Mode, "table", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(o.ExcelTsv)) return o.ExcelTsv;
            return o.Text;
        }

        public static int EffectivePsm(Settings cfg)
        {
            int psm = cfg != null ? cfg.TesseractPsm : 6;
            string mode = cfg != null ? (cfg.Mode ?? "normal").ToLowerInvariant() : "normal";
            if (mode == "line") return 7;
            if (mode == "table") return 3;
            if (mode == "sparse") return 11;
            return psm;
        }

        static string ToOneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var parts = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var sb = new StringBuilder();
            foreach (var raw in parts)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);
            }
            return sb.ToString();
        }

        static OcrEngine GetEngine(string language)
        {
            if (_cached != null && _cachedLang == language) return _cached;
            OcrEngine eng = null;
            if (!string.IsNullOrEmpty(language) && language != "auto")
            {
                try { eng = OcrEngine.TryCreateFromLanguage(new Language(language)); } catch { }
            }
            if (eng == null) eng = OcrEngine.TryCreateFromUserProfileLanguages();
            if (eng == null)
            {
                try { eng = OcrEngine.TryCreateFromLanguage(new Language("ja")); } catch { }
            }
            _cached = eng; _cachedLang = language;
            return eng;
        }

        public static string EngineInfo(Settings cfg)
        {
            if (cfg != null && UseRapidOcr(cfg)) return RapidOcr.EngineInfo;
            if (cfg != null && UseTesseract(cfg) && TesseractEngine.IsAvailable())
                return "Tesseract (" + cfg.TesseractLangs + ")";
            var eng = GetEngine(cfg != null ? cfg.Language : "auto");
            return eng != null ? "WinRT (" + eng.RecognizerLanguage.LanguageTag + ")" : "(利用不可)";
        }

        // returns a warning string if OCR is not usable, else null
        public static string SelfCheck(Settings cfg)
        {
            if (cfg != null && UseRapidOcrForced(cfg) && !RapidOcr.IsAvailable())
                return "engine=rapidocr ですが rapidocr フォルダ（モデル）が見つかりません。"
                     + "dist/rapidocr を exe と同じ場所に置いてください。";
            if (cfg != null && (cfg.Engine ?? "").ToLowerInvariant() == "tesseract" && !TesseractEngine.IsAvailable())
                return "engine=tesseract ですが tesseract フォルダが見つかりません。WinRT で動作します。";
            if (RapidOcr.IsAvailable()) return null;   // self-contained, no OS language pack needed
            try
            {
                var langs = OcrEngine.AvailableRecognizerLanguages;
                bool tess = TesseractEngine.IsAvailable();
                if ((langs == null || langs.Count == 0) && !tess)
                    return "この PC には OCR 言語リソースが見つかりません。rapidocr フォルダの同梱、"
                         + "日本語言語機能の追加、または tesseract フォルダの同梱が必要です。";
                return null;
            }
            catch (Exception ex)
            {
                if (TesseractEngine.IsAvailable()) return null; // tesseract can still work
                return "OCR 初期化に失敗しました: " + ex.Message;
            }
        }

        static string RecognizeWinRt(Bitmap bmp, Settings cfg)
        {
            var eng = GetEngine(cfg.Language);
            if (eng == null) throw new Exception("利用可能な OCR エンジンがありません。");

            using (Bitmap prepared = Preprocess(bmp, cfg.Binarize, cfg.Mode))
            using (var ms = new MemoryStream())
            {
                prepared.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                IRandomAccessStream ras = ms.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(ras).AsTask().Result;
                using (SoftwareBitmap sb = decoder.GetSoftwareBitmapAsync().AsTask().Result)
                {
                    OcrResult result = eng.RecognizeAsync(sb).AsTask().Result;
                    return CleanResult(result);
                }
            }
        }

        // upscale captures for better accuracy on small on-screen text; clamp to engine max.
        // Screen text is rendered small (often 12-16px tall), so we upscale even wide
        // strips of text — not just tiny captures — which is what fixes garbling like
        // "プレビュー" -> "プレ乙-" on narrow rows.
        public static Bitmap Preprocess(Bitmap src, bool binarize)
        {
            return Preprocess(src, binarize, "normal");
        }

        public static Bitmap Preprocess(Bitmap src, bool binarize, string mode)
        {
            int max = Math.Max(src.Width, src.Height);
            double scale;
            string m = (mode ?? "normal").ToLowerInvariant();
            if (m == "small" && max < 1200) scale = 4.0;
            else if (max < 900) scale = 3.0;
            else if (max < 1800) scale = 2.5;
            else if (max < 3000) scale = 2.0;
            else scale = 1.5;
            int limit = (int)OcrEngine.MaxImageDimension;
            int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
            if (w > limit || h > limit)
            {
                double d = Math.Min((double)limit / w, (double)limit / h);
                w = (int)(w * d); h = (int)(h * d);
            }
            Bitmap scaled;
            if (w == src.Width && h == src.Height)
            {
                scaled = (Bitmap)src.Clone();
            }
            else
            {
                scaled = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(src, 0, 0, scaled.Width, scaled.Height);
                }
            }
            if (!binarize) return scaled;
            using (scaled) { return Binarize(scaled); }
        }

        // grayscale + Otsu global threshold -> black text on white
        static Bitmap Binarize(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            var rect = new Rectangle(0, 0, w, h);
            var bd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = bd.Stride;
            var buf = new byte[stride * h];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            src.UnlockBits(bd);

            var gray = new byte[w * h];
            var hist = new int[256];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;            // BGRA
                    int lum = (buf[i + 2] * 299 + buf[i + 1] * 587 + buf[i] * 114) / 1000;
                    if (lum > 255) lum = 255;
                    gray[y * w + x] = (byte)lum;
                    hist[lum]++;
                }
            }
            int total = w * h;
            long sum = 0;
            for (int t = 0; t < 256; t++) sum += (long)t * hist[t];
            long sumB = 0; int wB = 0; double maxVar = -1; int threshold = 127;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t]; if (wB == 0) continue;
                int wF = total - wB; if (wF == 0) break;
                sumB += (long)t * hist[t];
                double mB = (double)sumB / wB;
                double mF = (double)(sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar) { maxVar = between; threshold = t; }
            }

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int dstride = dd.Stride;
            var outBuf = new byte[dstride * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * dstride;
                for (int x = 0; x < w; x++)
                {
                    byte v = gray[y * w + x] > threshold ? (byte)255 : (byte)0;
                    int i = row + x * 4;
                    outBuf[i] = v; outBuf[i + 1] = v; outBuf[i + 2] = v; outBuf[i + 3] = 255;
                }
            }
            Marshal.Copy(outBuf, 0, dd.Scan0, outBuf.Length);
            dst.UnlockBits(dd);
            return dst;
        }

        // join words, removing spaces between CJK characters
        static string CleanResult(OcrResult r)
        {
            var sb = new StringBuilder();
            foreach (var line in r.Lines)
            {
                var ls = new StringBuilder();
                foreach (var w in line.Words)
                {
                    if (w.Text.Length == 0) continue;
                    if (ls.Length > 0)
                    {
                        char prev = ls[ls.Length - 1];
                        char cur = w.Text[0];
                        if (!(IsCjk(prev) || IsCjk(cur))) ls.Append(' ');
                    }
                    ls.Append(w.Text);
                }
                sb.AppendLine(ls.ToString());
            }
            return sb.ToString().TrimEnd();
        }

        // clean plain text (Tesseract): drop spaces adjacent to CJK, normalise newlines
        public static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\f", "");
            var lines = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                var ls = new StringBuilder();
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == ' ')
                    {
                        char prev = ls.Length > 0 ? ls[ls.Length - 1] : '\0';
                        char next = (i + 1 < line.Length) ? line[i + 1] : '\0';
                        if (IsCjk(prev) || IsCjk(next)) continue; // drop space touching CJK
                    }
                    ls.Append(c);
                }
                lines.Add(ls.ToString().TrimEnd());
            }
            // collapse trailing blank lines
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines);
        }

        static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x30FF)   // punctuation + kana
                || (c >= 0x3400 && c <= 0x4DBF)   // CJK ext A
                || (c >= 0x4E00 && c <= 0x9FFF)   // CJK unified
                || (c >= 0xFF00 && c <= 0xFFEF);  // fullwidth forms
        }
    }

    class TsvWord
    {
        public int Left, Top, Width, Height, Conf;
        public string Text = "";
        public int Right { get { return Left + Width; } }
        public int CenterY { get { return Top + Height / 2; } }
    }

    static class TableFormatter
    {
        public static string ToExcelTsv(List<TsvWord> words)
        {
            if (words == null || words.Count == 0) return "";
            words = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.CenterY).ThenBy(w => w.Left).ToList();
            double avgH = words.Average(w => Math.Max(1, w.Height));
            int lineTol = Math.Max(8, (int)(avgH * 0.65));
            var lines = new List<List<TsvWord>>();
            foreach (var w in words)
            {
                List<TsvWord> best = null;
                int bestDiff = int.MaxValue;
                foreach (var line in lines)
                {
                    int cy = (int)line.Average(x => x.CenterY);
                    int diff = Math.Abs(w.CenterY - cy);
                    if (diff < bestDiff) { bestDiff = diff; best = line; }
                }
                if (best == null || bestDiff > lineTol)
                {
                    best = new List<TsvWord>();
                    lines.Add(best);
                }
                best.Add(w);
            }
            lines = lines.OrderBy(l => l.Average(w => w.CenterY)).ToList();
            double avgW = words.Average(w => Math.Max(1, w.Width));
            int gapTab = Math.Max(18, (int)(avgW * 1.7));
            var outLines = new List<string>();
            foreach (var line in lines)
            {
                var row = line.OrderBy(w => w.Left).ToList();
                var cells = new List<string>();
                var cell = new StringBuilder();
                int prevRight = -1;
                foreach (var w in row)
                {
                    if (prevRight >= 0)
                    {
                        int gap = w.Left - prevRight;
                        if (gap > gapTab)
                        {
                            cells.Add(CleanCell(cell.ToString()));
                            cell.Length = 0;
                        }
                        else if (cell.Length > 0 && !TouchesCjk(cell[cell.Length - 1], w.Text.Length > 0 ? w.Text[0] : ' '))
                        {
                            cell.Append(' ');
                        }
                    }
                    cell.Append(w.Text);
                    prevRight = Math.Max(prevRight, w.Right);
                }
                cells.Add(CleanCell(cell.ToString()));
                outLines.Add(string.Join("\t", cells.ToArray()));
            }
            return string.Join("\r\n", outLines.ToArray());
        }

        public static string ToMarkdownTable(string tsv)
        {
            if (string.IsNullOrWhiteSpace(tsv)) return "";
            var rows = tsv.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
                .Where(l => l.Trim().Length > 0)
                .Select(l => l.Split('\t'))
                .ToList();
            if (rows.Count == 0) return "";
            int cols = rows.Max(r => r.Length);
            var sb = new StringBuilder();
            AppendMdRow(sb, rows[0], cols);
            var sep = new string[cols];
            for (int i = 0; i < cols; i++) sep[i] = "---";
            AppendMdRow(sb, sep, cols);
            for (int r = 1; r < rows.Count; r++) AppendMdRow(sb, rows[r], cols);
            return sb.ToString().TrimEnd();
        }

        static void AppendMdRow(StringBuilder sb, string[] cells, int cols)
        {
            sb.Append('|');
            for (int i = 0; i < cols; i++)
            {
                string c = i < cells.Length ? cells[i] : "";
                c = c.Replace("|", "\\|").Trim();
                sb.Append(' ').Append(c).Append(' ').Append('|');
            }
            sb.AppendLine();
        }

        static string CleanCell(string s)
        {
            if (s == null) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        static bool TouchesCjk(char a, char b)
        {
            return IsCjkLocal(a) || IsCjkLocal(b);
        }
        static bool IsCjkLocal(char c)
        {
            return (c >= 0x3000 && c <= 0x30FF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xFF00 && c <= 0xFFEF);
        }
    }

    // ---------------------------------------------------------------------
    // Tesseract engine (bundled portable tesseract.exe, called as a process)
    // ---------------------------------------------------------------------
    static class TesseractEngine
    {
        static string Dir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tesseract"); }
        }
        static string Exe { get { return Path.Combine(Dir, "tesseract.exe"); } }
        static string TessData { get { return Path.Combine(Dir, "tessdata"); } }

        public static bool IsAvailable()
        {
            try { return File.Exists(Exe) && Directory.Exists(TessData); }
            catch { return false; }
        }

        public static string Recognize(Bitmap bmp, Settings cfg)
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                "snipocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                // upscale only; tesseract does its own binarisation internally
                using (Bitmap prepared = OcrService.Preprocess(bmp, false, cfg.Mode))
                    prepared.Save(tmp, ImageFormat.Png);

                string langs = string.IsNullOrEmpty(cfg.TesseractLangs) ? "jpn+eng" : cfg.TesseractLangs;
                var psi = new ProcessStartInfo
                {
                    FileName = Exe,
                    Arguments = "\"" + tmp + "\" stdout -l " + langs +
                                " --psm " + OcrService.EffectivePsm(cfg) +
                                " --tessdata-dir \"" + TessData + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Dir
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd(); // drain to avoid deadlock
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                    return OcrService.CleanText(outp);
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        public static List<TsvWord> RecognizeWords(Bitmap bmp, Settings cfg)
        {
            var result = new List<TsvWord>();
            string tmp = Path.Combine(Path.GetTempPath(),
                "snipocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (Bitmap prepared = OcrService.Preprocess(bmp, false, cfg.Mode))
                    prepared.Save(tmp, ImageFormat.Png);

                string langs = string.IsNullOrEmpty(cfg.TesseractLangs) ? "jpn+eng" : cfg.TesseractLangs;
                var psi = new ProcessStartInfo
                {
                    FileName = Exe,
                    Arguments = "\"" + tmp + "\" stdout -l " + langs +
                                " --psm " + OcrService.EffectivePsm(cfg) +
                                " --tessdata-dir \"" + TessData + "\" tsv",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Dir
                };
                string outp = "";
                using (var p = Process.Start(psi))
                {
                    outp = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                }
                var lines = outp.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                if (lines.Length < 2) return result;
                var header = lines[0].Split('\t');
                int idxLevel = Array.IndexOf(header, "level");
                int idxLeft = Array.IndexOf(header, "left");
                int idxTop = Array.IndexOf(header, "top");
                int idxWidth = Array.IndexOf(header, "width");
                int idxHeight = Array.IndexOf(header, "height");
                int idxConf = Array.IndexOf(header, "conf");
                int idxText = Array.IndexOf(header, "text");
                if (idxText < 0 || idxLeft < 0 || idxTop < 0 || idxWidth < 0 || idxHeight < 0) return result;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    var cols = lines[i].Split('\t');
                    if (cols.Length <= idxText) continue;
                    int level = 0;
                    if (idxLevel >= 0 && idxLevel < cols.Length) int.TryParse(cols[idxLevel], out level);
                    if (idxLevel >= 0 && level != 5) continue;
                    string text = cols[idxText].Trim();
                    if (text.Length == 0) continue;
                    var w = new TsvWord();
                    int.TryParse(cols[idxLeft], out w.Left);
                    int.TryParse(cols[idxTop], out w.Top);
                    int.TryParse(cols[idxWidth], out w.Width);
                    int.TryParse(cols[idxHeight], out w.Height);
                    if (idxConf >= 0 && idxConf < cols.Length) int.TryParse(cols[idxConf], out w.Conf);
                    w.Text = text;
                    result.Add(w);
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return result;
        }
    }

    // ---------------------------------------------------------------------
    // Result window
    // ---------------------------------------------------------------------
    class ResultForm : Form
    {
        public ResultForm(OcrOutput output, Bitmap image, Settings cfg)
        {
            Text = "SnipOCR 結果 - " + (output != null ? output.Mode : "normal");
            Width = 900; Height = 520;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            try { Icon = AppIcons.Window; } catch { }

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterDistance = 360;

            var pic = new PictureBox();
            pic.Dock = DockStyle.Fill;
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.FromArgb(245, 245, 245);
            pic.Image = image;
            split.Panel1.Controls.Add(pic);

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            var tabText = new TabPage("テキスト");
            var tabExcel = new TabPage("Excel TSV");
            var tabMd = new TabPage("Markdown表");
            var tabOne = new TabPage("1行");
            var boxText = MakeBox(output != null ? output.Text : "");
            var boxExcel = MakeBox(output != null ? output.ExcelTsv : "");
            var boxMd = MakeBox(output != null ? output.MarkdownTable : "");
            var boxOne = MakeBox(output != null ? output.OneLine : "");
            tabText.Controls.Add(boxText);
            tabExcel.Controls.Add(boxExcel);
            tabMd.Controls.Add(boxMd);
            tabOne.Controls.Add(boxOne);
            tabs.TabPages.Add(tabText);
            tabs.TabPages.Add(tabExcel);
            tabs.TabPages.Add(tabMd);
            tabs.TabPages.Add(tabOne);
            split.Panel2.Controls.Add(tabs);

            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Bottom;
            panel.FlowDirection = FlowDirection.RightToLeft;
            panel.Height = 42;
            panel.Padding = new Padding(6);
            var btnClose = new Button { Text = "閉じる", Width = 90, Height = 28 };
            btnClose.Click += (s, e) => Close();
            var btnCopyCurrent = new Button { Text = "表示タブをコピー", Width = 120, Height = 28 };
            btnCopyCurrent.Click += (s, e) => CopyText(CurrentBox(tabs));
            var btnCopyExcel = new Button { Text = "Excel用コピー", Width = 110, Height = 28 };
            btnCopyExcel.Click += (s, e) => CopyText(boxExcel.Text.Length > 0 ? boxExcel.Text : boxText.Text);
            var btnCopyMd = new Button { Text = "Markdownコピー", Width = 120, Height = 28 };
            btnCopyMd.Click += (s, e) => CopyText(boxMd.Text.Length > 0 ? boxMd.Text : boxText.Text);
            var btnRe = new Button { Text = "再OCR", Width = 90, Height = 28 };
            btnRe.Click += (s, e) =>
            {
                try
                {
                    if (pic.Image == null) return;
                    using (var bmp = new Bitmap(pic.Image))
                    {
                        var next = OcrService.RecognizeWithFormats(bmp, cfg);
                        boxText.Text = next.Text;
                        boxExcel.Text = next.ExcelTsv;
                        boxMd.Text = next.MarkdownTable;
                        boxOne.Text = next.OneLine;
                    }
                }
                catch (Exception ex) { MessageBox.Show("再OCR エラー: " + ex.Message, "SnipOCR"); }
            };
            panel.Controls.Add(btnClose);
            panel.Controls.Add(btnCopyCurrent);
            panel.Controls.Add(btnCopyExcel);
            panel.Controls.Add(btnCopyMd);
            panel.Controls.Add(btnRe);

            Controls.Add(split);
            Controls.Add(panel);

            FormClosed += (s, e) => { try { if (pic.Image != null) pic.Image.Dispose(); } catch { } };
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        TextBox MakeBox(string text)
        {
            var box = new TextBox();
            box.Multiline = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Dock = DockStyle.Fill;
            box.Text = text ?? "";
            box.Font = new Font("Yu Gothic UI", 11f);
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            return box;
        }

        string CurrentBox(TabControl tabs)
        {
            if (tabs.SelectedTab != null && tabs.SelectedTab.Controls.Count > 0)
            {
                var tb = tabs.SelectedTab.Controls[0] as TextBox;
                if (tb != null) return tb.Text;
            }
            return "";
        }

        void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }
    }
}
