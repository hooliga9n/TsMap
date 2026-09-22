namespace TsMap
{
    public class Settings
    {
        public string LastTileMapPath { get; set; }
        public string AtsPath { get; set; }
        public string Ets2Path { get; set; }
        public string OutputPath { get; set; }
        public string FallbackGame { get; set; }
        public string LastGamePath { get; set; }
        public string LastModPath { get; set; }
        public MapPaletteSettings MapColor { get; set; } = new MapPaletteSettings();
    }
}