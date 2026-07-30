using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

class PocSmall
{
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        // small on-screen-like text (~13px), wide strip -> old code would NOT upscale it
        var bmp = new Bitmap(760, 90, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var f = new Font("Yu Gothic UI", 10f))
            {
                g.DrawString("上書き保存 プレビュー 拡張 元画像 前処理オプション", f, Brushes.Black, 8, 8);
                g.DrawString("Invoice No. A-12345  合計 301,297 円", f, Brushes.Black, 8, 44);
            }
        }
        bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "_small_sample.png"), ImageFormat.Png);

        Console.WriteLine("no upscale (旧挙動):");
        Console.WriteLine("  " + Ocr(bmp).Replace("\n", "\n  "));
        Console.WriteLine();
        using (var up = Scale(bmp, 3.0))
        {
            Console.WriteLine("3x upscale (新前処理):");
            Console.WriteLine("  " + Ocr(up).Replace("\n", "\n  "));
        }
    }

    static Bitmap Scale(Bitmap src, double s)
    {
        var dst = new Bitmap((int)(src.Width * s), (int)(src.Height * s), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        }
        return dst;
    }

    static string Ocr(Bitmap bmp)
    {
        var eng = OcrEngine.TryCreateFromUserProfileLanguages();
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png); ms.Position = 0;
            IRandomAccessStream ras = ms.AsRandomAccessStream();
            var dec = BitmapDecoder.CreateAsync(ras).AsTask().Result;
            using (var sb = dec.GetSoftwareBitmapAsync().AsTask().Result)
                return eng.RecognizeAsync(sb).AsTask().Result.Text;
        }
    }
}
