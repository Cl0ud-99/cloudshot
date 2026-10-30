using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Cloudshot
{
    /// <summary>
    /// 用 Windows 自带的 OCR 引擎识别位图文字（离线、免费、不打包模型）。
    /// 除了文本，还会返回每个词的坐标，供"框选文字"模式使用。
    /// </summary>
    internal static class Ocr
    {
        public sealed class WordBox
        {
            public string Text = string.Empty;
            public double X;
            public double Y;
            public double W;
            public double H;
            public bool NewLine;   // 该词是否是某一行开头
        }

        public sealed class Result
        {
            public string Text = string.Empty;
            public string[] Lines = new string[0];
            public List<WordBox> Words = new List<WordBox>();
            public string LanguageTag = string.Empty;
            public bool IsChinese;
            public int ElapsedMs;
        }

        public static string Diagnostics = string.Empty;

        public static Result Recognize(BitmapSource source, double upscale = 2.0)
        {
            var result = new Result();
            if (source == null || source.PixelWidth < 2 || source.PixelHeight < 2) return result;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var candidates = new List<Candidate>();

            try
            {
                var input = DownscaleIfNeeded(source, upscale, out double scaleX, out double scaleY);
                int width = input.PixelWidth;
                int height = input.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];

                var converted = input.Format == PixelFormats.Bgra32
                    ? input
                    : new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
                converted.CopyPixels(pixels, stride, 0);

                using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied))
                {
                    foreach (var language in InstalledLanguages())
                    {
                        var lines = RunEngine(bitmap, language);
                        if (lines.Count == 0) continue;

                        // OCR 是在缩放后的图上做的，把词框映射回原图坐标
                        if (scaleX != 1 || scaleY != 1)
                        {
                            foreach (var line in lines)
                            {
                                foreach (var word in line)
                                {
                                    word.X *= scaleX;
                                    word.Y *= scaleY;
                                    word.W *= scaleX;
                                    word.H *= scaleY;
                                }
                            }
                        }

                        candidates.Add(new Candidate { Tag = language, Lines = lines });
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics = "ocr exception: " + ex.GetType().Name + " " + ex.Message;
            }

            sw.Stop();
            result.ElapsedMs = (int)sw.ElapsedMilliseconds;

            var best = PickBest(candidates);
            if (best == null) return result;

            result.LanguageTag = best.Tag;
            var allText = new StringBuilder();
            foreach (var line in best.Lines)
            {
                foreach (var word in line)
                {
                    allText.Append(word.Text).Append(' ');
                }
                allText.Append('\n');
            }
            result.IsChinese = ContainsCjk(allText.ToString());
            result.Lines = BuildLines(best);
            result.Text = string.Join(" ", result.Lines).Trim();
            result.Words = BuildWords(best);
            return result;
        }

        private sealed class Candidate
        {
            public string Tag;
            public List<List<WordBox>> Lines;   // 已按原图坐标还原
        }

        private static List<string> InstalledLanguages()
        {
            var list = new List<string>();
            try
            {
                foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                {
                    list.Add(language.LanguageTag);
                }
            }
            catch
            {
            }
            if (list.Count == 0) list.Add("en-US");
            return list;
        }

        private static Candidate PickBest(List<Candidate> candidates)
        {
            if (candidates.Count == 0) return null;

            Candidate cjk = null;
            int cjkCount = 0;
            Candidate other = null;
            int otherCount = 0;

            foreach (var candidate in candidates)
            {
                int letters = 0;
                bool hasCjk = false;
                foreach (var line in candidate.Lines)
                {
                    foreach (var word in line)
                    {
                        if (ContainsCjk(word.Text)) hasCjk = true;
                        letters += CountLetters(word.Text);
                    }
                }

                if (hasCjk && letters >= cjkCount)
                {
                    cjk = candidate;
                    cjkCount = letters;
                }
                if (letters > otherCount)
                {
                    other = candidate;
                    otherCount = letters;
                }
            }

            return cjk ?? other;
        }

        private static string[] BuildLines(Candidate candidate)
        {
            var lines = new List<string>();
            foreach (var line in candidate.Lines)
            {
                var sb = new StringBuilder();
                foreach (var word in line)
                {
                    if (sb.Length > 0 && NeedsSpace(sb[sb.Length - 1], word.Text[0])) sb.Append(' ');
                    sb.Append(word.Text);
                }
                var text = sb.ToString().Trim();
                if (text.Length > 0) lines.Add(text);
            }
            return lines.ToArray();
        }

        private static List<WordBox> BuildWords(Candidate candidate)
        {
            var words = new List<WordBox>();
            foreach (var line in candidate.Lines)
            {
                bool first = true;
                foreach (var word in line)
                {
                    word.NewLine = first;
                    first = false;
                    words.Add(word);
                }
            }
            return words;
        }

        private static BitmapSource DownscaleIfNeeded(BitmapSource source, double upscale,
            out double scaleX, out double scaleY)
        {
            scaleX = 1;
            scaleY = 1;

            double max = 4000;
            try
            {
                if (OcrEngine.MaxImageDimension > 0) max = OcrEngine.MaxImageDimension;
            }
            catch
            {
            }

            double scale = 1.0;

            // 小字号是 OCR 的主要失分点：先放大再识别（倍率可由用户用 +/- 调整）
            if (source.PixelWidth <= 1600 && source.PixelHeight <= 1000)
            {
                scale = Math.Max(1.0, upscale);
            }

            // 超大图必须缩小
            if (source.PixelWidth * scale > max || source.PixelHeight * scale > max)
            {
                scale = Math.Min(max / (double)source.PixelWidth, max / (double)source.PixelHeight);
            }

            if (Math.Abs(scale - 1.0) < 0.01) return source;

            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            scaleX = 1 / scale;
            scaleY = 1 / scale;
            return transformed;
        }

        private static List<List<WordBox>> RunEngine(SoftwareBitmap bitmap, string languageTag)
        {
            var lines = new List<List<WordBox>>();
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                if (engine == null) return lines;

                var task = engine.RecognizeAsync(bitmap);
                if (!task.AsTask().Wait(4000)) return lines;

                var ocrResult = task.GetResults();
                if (ocrResult == null) return lines;

                foreach (var line in ocrResult.Lines)
                {
                    var words = new List<WordBox>();
                    foreach (var word in line.Words)
                    {
                        var rect = word.BoundingRect;
                        words.Add(new WordBox
                        {
                            Text = word.Text ?? string.Empty,
                            X = rect.X,
                            Y = rect.Y,
                            W = rect.Width,
                            H = rect.Height
                        });
                    }
                    if (words.Count > 0) lines.Add(words);
                }
            }
            catch (Exception ex)
            {
                Diagnostics += languageTag + ": " + ex.GetType().Name + "; ";
            }
            return lines;
        }

        private static bool ContainsCjk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (IsCjk(c)) return true;
            }
            return false;
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) ||
                   (c >= 0x3040 && c <= 0x30FF);
        }

        private static int CountLetters(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0;
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c)) n++;
            }
            return n;
        }

        private static bool IsCjkPunctuation(char c)
        {
            return c == '\u3002' || c == '\uFF0C' || c == '\uFF01' || c == '\uFF1F' ||
                   c == '\uFF1B' || c == '\uFF1A' || c == '\u201C' || c == '\u201D' ||
                   c == '\u3001' || c == '\uFF08' || c == '\uFF09' || c == '\u2026';
        }

        private static bool NeedsSpace(char prev, char next)
        {
            if (IsCjkPunctuation(prev) || IsCjkPunctuation(next)) return false;
            if (IsCjk(prev) && IsCjk(next)) return false;
            return true;
        }

        private static bool EndsWithCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return IsCjk(s[s.Length - 1]);
        }

        private static bool StartsWithCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return IsCjk(s[0]);
        }
    }
}
