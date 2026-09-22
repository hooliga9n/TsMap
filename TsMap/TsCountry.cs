using Newtonsoft.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TsMap.Common;
using TsMap.FileSystem;
using TsMap.Helpers.Logger;

namespace TsMap
{
    /// <summary>Truck speed limits (km/h) for one lane speed class, e.g. "local_road" or "motorway".</summary>
    public struct TsSpeedLimit
    {
        public double Limit;
        public double UrbanLimit;
        public double MaxLimit;
    }

    public class TsCountry
    {
        [JsonIgnore]
        public ulong Token { get; }

        public int CountryId { get; }
        public string Name { get; }
        [JsonIgnore]
        public string LocalizationToken { get; }
        public string CountryCode { get; }
        public float X { get; }
        public float Y { get; }

        /// <summary>
        /// Truck speed limits (km/h) by lane speed class ("local_road", "divided_road", "expressway",
        /// "motorway", "freeway", "slow_road" - matches truckermudgeon/maps' LaneSpeedClass names,
        /// after underscoring their camelCase). Parsed from the country's sibling "speed_limits.sii"
        /// file (introduced in game version 1.19; see modding.scssoft.com's 1.19/1.25 changelogs).
        /// Empty if that file is missing/unparseable for this country - callers should fall back to a
        /// flat assumption in that case, since a country without this data isn't necessarily wrong,
        /// it's just older/modded content that doesn't have the file.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, TsSpeedLimit> TruckSpeedLimits { get; } = new Dictionary<string, TsSpeedLimit>();

        public TsCountry(string path)
        {
            var file = UberFileSystem.Instance.GetFile(path);

            if (file == null) return;

            var fileContent = file.Entry.Read();

            var lines = SiiHelper.GetLines(fileContent);

            foreach (var line in lines)
            {
                var (validLine, key, value) = SiiHelper.ParseLine(line);
                if (!validLine) continue;

                if (key == "country_data")
                {
                    Token = ScsToken.StringToToken(value.Split('.').Last().Trim().TrimEnd('{').Trim());
                }
                else if (key == "country_id")
                {
                    CountryId = int.Parse(value);
                }
                else if (key == "name")
                {
                    Name = value.Split('"')[1];
                }
                else if (key == "name_localized")
                {
                    LocalizationToken = value.Split('"')[1];
                    LocalizationToken = LocalizationToken.Replace("@", "");
                }
                else if (key == "country_code")
                {
                    CountryCode = value.Split('"')[1];
                }
                else if (key == "pos")
                {
                    var vector = value.Split('(')[1].Split(')')[0];
                    var values = vector.Split(',');
                    X = float.Parse(values[0], CultureInfo.InvariantCulture);
                    Y = float.Parse(values[2], CultureInfo.InvariantCulture);
                }
            }

            ParseTruckSpeedLimits(path);
        }

        /// <summary>
        /// Best-effort parse of the sibling "speed_limits.sii" (same directory as the country's own
        /// "country.sii"). One .sii file can define limits for several vehicle types (car/truck/bus)
        /// as separate "unit : .speed_limit.&lt;type&gt; { vehicle_speed_class: &lt;type&gt; ... }"
        /// blocks, each with its own 0-based lane_speed_class[]/limit[]/urban_limit[]/max_limit[]
        /// index set - so this only collects values while inside a block whose
        /// vehicle_speed_class is "truck", tracked via brace depth.
        /// </summary>
        private void ParseTruckSpeedLimits(string countryFilePath)
        {
            var slashIndex = countryFilePath.LastIndexOf('/');
            if (slashIndex < 0) return;
            var speedLimitsPath = countryFilePath.Substring(0, slashIndex + 1) + "speed_limits.sii";

            var file = UberFileSystem.Instance.GetFile(speedLimitsPath);
            if (file == null) return; // no speed_limits.sii for this country/game version - caller falls back.

            try
            {
                var lines = SiiHelper.GetLines(file.Entry.Read());

                var depth = 0;
                var truckBlockDepth = -1; // brace depth at which the current truck unit started; -1 = not in one
                var classByIndex = new Dictionary<int, string>();
                var limitByIndex = new Dictionary<int, double>();
                var urbanByIndex = new Dictionary<int, double>();
                var maxByIndex = new Dictionary<int, double>();

                void FlushBlock()
                {
                    foreach (var kvp in classByIndex)
                    {
                        if (!limitByIndex.TryGetValue(kvp.Key, out var limit)) continue;
                        urbanByIndex.TryGetValue(kvp.Key, out var urban);
                        maxByIndex.TryGetValue(kvp.Key, out var max);
                        TruckSpeedLimits[kvp.Value] = new TsSpeedLimit
                        {
                            Limit = limit,
                            UrbanLimit = urban > 0 ? urban : limit,
                            MaxLimit = max > 0 ? max : limit
                        };
                    }
                    classByIndex.Clear();
                    limitByIndex.Clear();
                    urbanByIndex.Clear();
                    maxByIndex.Clear();
                }

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();

                    // Track brace depth ourselves - SiiHelper.ParseLine works line-by-line and
                    // doesn't track scope, but we need to know when a "unit { ... }" block ends.
                    var opens = line.Count(c => c == '{');
                    var closes = line.Count(c => c == '}');

                    var (validLine, key, value) = SiiHelper.ParseLine(rawLine);
                    if (validLine)
                    {
                        if (key == "vehicle_speed_class")
                        {
                            if (value.Trim() == "truck") truckBlockDepth = depth;
                            else if (truckBlockDepth == depth) truckBlockDepth = -1; // a non-truck unit at the same depth
                        }
                        else if (truckBlockDepth >= 0 && depth >= truckBlockDepth)
                        {
                            if (key.StartsWith("lane_speed_class") && key.Contains("["))
                            {
                                var idx = int.Parse(key.Split('[')[1].Split(']')[0]);
                                classByIndex[idx] = value.Trim();
                            }
                            else if (key.StartsWith("urban_limit") && key.Contains("["))
                            {
                                var idx = int.Parse(key.Split('[')[1].Split(']')[0]);
                                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) urbanByIndex[idx] = v;
                            }
                            else if (key.StartsWith("max_limit") && key.Contains("["))
                            {
                                var idx = int.Parse(key.Split('[')[1].Split(']')[0]);
                                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) maxByIndex[idx] = v;
                            }
                            else if (key.StartsWith("limit") && key.Contains("["))
                            {
                                var idx = int.Parse(key.Split('[')[1].Split(']')[0]);
                                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) limitByIndex[idx] = v;
                            }
                        }
                    }

                    depth += opens;
                    depth -= closes;

                    if (closes > 0 && truckBlockDepth >= 0 && depth < truckBlockDepth)
                    {
                        FlushBlock();
                        truckBlockDepth = -1;
                    }
                }
                if (truckBlockDepth >= 0) FlushBlock(); // file ended while still inside the truck block
            }
            catch (System.Exception ex)
            {
                Logger.Instance.Warning($"Could not parse truck speed limits from '{speedLimitsPath}': {ex.Message}");
            }
        }
    }
}
