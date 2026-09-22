using System;
using System.Drawing;

namespace TsMap
{
    public class MapPaletteSettings
    {
        public string Background { get; set; } = "#303030";
        public string CityName { get; set; } = "#DEDEDE";
        public string Error { get; set; } = "#303030";
        public string FerryLines { get; set; } = "#FFFFFF";
        public string PrefabDark { get; set; } = "#E1A338";
        public string PrefabGreen { get; set; } = "#AACB96";
        public string PrefabLight { get; set; } = "#ECCB99";
        public string PrefabRoad { get; set; } = "#FFDC50";
        public string Road { get; set; } = "#FFDC50";

        public static Color ParseHex(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try
            {
                return ColorTranslator.FromHtml(hex.StartsWith("#") ? hex : "#" + hex);
            }
            catch
            {
                return fallback;
            }
        }

        public static string ColorToHex(Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        public MapPalette ToMapPalette()
        {
            return new MapPalette
            {
                Background = new SolidBrush(ParseHex(Background, Color.FromArgb(0x30, 0x30, 0x30))),
                CityName = new SolidBrush(ParseHex(CityName, Color.FromArgb(0xDE, 0xDE, 0xDE))),
                Error = new SolidBrush(ParseHex(Error, Color.FromArgb(0x30, 0x30, 0x30))),
                FerryLines = new SolidBrush(ParseHex(FerryLines, Color.White)),
                PrefabDark = new SolidBrush(ParseHex(PrefabDark, Color.FromArgb(0xE1, 0xA3, 0x38))),
                PrefabGreen = new SolidBrush(ParseHex(PrefabGreen, Color.FromArgb(0xAA, 0xCB, 0x96))),
                PrefabLight = new SolidBrush(ParseHex(PrefabLight, Color.FromArgb(0xEC, 0xCB, 0x99))),
                PrefabRoad = new SolidBrush(ParseHex(PrefabRoad, Color.FromArgb(0xFF, 0xDC, 0x50))),
                Road = new SolidBrush(ParseHex(Road, Color.FromArgb(0xFF, 0xDC, 0x50)))
            };
        }
    }
}
