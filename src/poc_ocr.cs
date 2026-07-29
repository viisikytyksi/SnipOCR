using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

class PocOcr
{
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1) synthesize a test image with JP + EN text
        var bmp = new Bitmap(700, 200, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var f1 = new Font("Yu Gothic UI", 28f))
            using (var f2 = new Font("Segoe UI", 26f))
            {
                g.DrawString("経費精算 请求 テスト", f1, Brushes.Black, 20, 20);
                g.DrawString("Invoice No. A-12345", f2, Brushes.Black, 20, 90);
            }
        }
        bmp.Save(@"C:\Users\goichi\Documents\SnipOCR\build\_ocr_sample.png", ImageFormat.Png);

        // 2) run the same pipeline the app uses
        var eng = OcrEngine.TryCreateFromUserProfileLanguages();
        if (eng == null) { Console.WriteLine("NO ENGINE"); return; }
        Console.WriteLine("engine lang = " + eng.RecognizerLanguage.LanguageTag);

        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            IRandomAccessStream ras = ms.AsRandomAccessStream();
            var decoder = BitmapDecoder.CreateAsync(ras).AsTask().Result;
            using (var sb = decoder.GetSoftwareBitmapAsync().AsTask().Result)
            {
                var result = eng.RecognizeAsync(sb).AsTask().Result;
                Console.WriteLine("--- raw .Text ---");
                Console.WriteLine(result.Text);
                Console.WriteLine("--- cleaned (CJK spaces removed) ---");
                Console.WriteLine(Clean(result));
            }
        }
    }

    static string Clean(OcrResult r)
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

    static bool IsCjk(char c)
    {
        return (c >= 0x3000 && c <= 0x30FF) || (c >= 0x3400 && c <= 0x4DBF)
            || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xFF00 && c <= 0xFFEF);
    }
}
