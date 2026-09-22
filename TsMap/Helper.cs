using System;
using System.Drawing;

namespace TsMap
{
    public static class RenderHelper
    {
        public static PointF RotatePoint(float x, float z, float angle, float rotX, float rotZ)
        {
            var s = Math.Sin(angle);
            var c = Math.Cos(angle);
            double newX = x - rotX;
            double newZ = z - rotZ;
            return new PointF((float)((newX * c) - (newZ * s) + rotX), (float)((newX * s) + (newZ * c) + rotZ));
        }

        public static PointF GetCornerCoords(float x, float z, float width, double angle)
        {
            return new PointF(
                (float)(x + width * Math.Cos(angle)),
                (float)(z + width * Math.Sin(angle))
            );
        }
        public static double Hypotenuse(float x, float y)
        {
            return Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2));
        }

        // https://stackoverflow.com/a/45881662
        public static Tuple<PointF, PointF> GetBezierControlNodes(float startX, float startZ, double startRot, float endX, float endZ, double endRot)
        {
            var len = Hypotenuse(endX - startX, endZ - startZ);
            var ax1 = (float)(Math.Cos(startRot) * len * (1 / 3f));
            var az1 = (float)(Math.Sin(startRot) * len * (1 / 3f));
            var ax2 = (float)(Math.Cos(endRot) * len * (1 / 3f));
            var az2 = (float)(Math.Sin(endRot) * len * (1 / 3f));
            return new Tuple<PointF, PointF>(new PointF(ax1, az1), new PointF(ax2, az2));
        }

        public static int GetZoomIndex(Rectangle clip, float scale)
        {
            var smallestSize = (clip.Width > clip.Height) ? clip.Height / scale : clip.Width / scale;
            if (smallestSize < 1000) return 0;
            if (smallestSize < 5000) return 1;
            if (smallestSize < 18500) return 2;
            return 3;
        }
    }

    public static class SiiHelper
    {
        private const string CharNotToTrim = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

        /// <summary>
        /// Removes C-style block comments ("/* ... */") from decoded .sii text, so that
        /// commented-out data (e.g. an old/unused set of connection_positions in a ferry
        /// connection file) never gets misread as real key/value lines by the simple
        /// line-based parser used elsewhere. Newlines inside a stripped comment are kept
        /// so that anything relying on line numbers/positions afterwards is unaffected.
        /// Unterminated comments (missing closing "*/") are stripped to the end of the text.
        /// </summary>
        public static string StripBlockComments(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("/*", StringComparison.Ordinal) == -1) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            var i = 0;
            var inQuotes = false;
            while (i < text.Length)
            {
                var c = text[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (!inQuotes && i + 1 < text.Length && c == '/' && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var commentEnd = end == -1 ? text.Length : end + 2;
                    for (var j = i; j < commentEnd; j++)
                    {
                        if (text[j] == '\n') sb.Append('\n');
                    }
                    i = commentEnd;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Decodes raw .sii file bytes as UTF8, strips block comments, and splits into
        /// lines. Use this instead of Encoding.UTF8.GetString(data).Split('\n') directly
        /// when reading any .sii-format file, so block comments are handled everywhere
        /// consistently.
        /// </summary>
        public static string[] GetLines(byte[] data)
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            text = StripBlockComments(text);
            return text.Split('\n');
        }

        public static string Trim(string src)
        {
            var startTrimIndex = 0;
            var endTrimIndex = src.Length;
            for (var i = 0; i < src.Length; i++)
            {
                if (!CharNotToTrim.Contains(src[i].ToString()))
                {
                    startTrimIndex = i + 1;
                }
                else break;
            }

            for (var i = src.Length - 1; i >= 0; i--)
            {
                if (!CharNotToTrim.Contains(src[i].ToString()))
                {
                    endTrimIndex = i;
                }
                else break;
            }

            if (startTrimIndex == src.Length || startTrimIndex >= endTrimIndex) return "";
            return src.Substring(startTrimIndex, endTrimIndex - startTrimIndex);
        }

        public static (bool Valid, string Key, string Value) ParseLine(string line)
        {
            line = Trim(line);
            if (!line.Contains(":") || line.StartsWith("#") || line.StartsWith("//")) return (false, line, line);
            var key = Trim(line.Split(':')[0]);
            var val = line.Split(':')[1];
            
            val = StripComments(val);
            val = Trim(val);
            return (true, key, val);
        }

        private static string StripComments(string val)
        {
            var inQuote = false;
            for (var i = 0; i < val.Length; i++)
            {
                if (val[i] == '"')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote)
                {
                    if (val[i] == '#')
                    {
                        return val.Substring(0, i);
                    }
                    if (i + 1 < val.Length && val[i] == '/' && val[i + 1] == '/')
                    {
                        return val.Substring(0, i);
                    }
                }
            }
            return val;
        }
    }
}