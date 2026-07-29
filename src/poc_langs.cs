using System;
using System.Linq;
using Windows.Media.Ocr;
using Windows.Globalization;

class PocLangs
{
    static void Main()
    {
        Console.WriteLine("=== WinRT OCR availability check ===");
        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages;
            Console.WriteLine("Available OCR recognizer languages: " + langs.Count);
            foreach (var l in langs)
                Console.WriteLine("  - " + l.LanguageTag + "  (" + l.DisplayName + ")");

            Console.WriteLine();
            bool ja = langs.Any(l => l.LanguageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
            bool en = langs.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("Japanese OCR available: " + ja);
            Console.WriteLine("English  OCR available: " + en);

            var eng = OcrEngine.TryCreateFromUserProfileLanguages();
            Console.WriteLine("User-profile engine: " + (eng != null ? eng.RecognizerLanguage.LanguageTag : "NULL"));
            Console.WriteLine("Max image dimension: " + OcrEngine.MaxImageDimension);
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
        }
    }
}
