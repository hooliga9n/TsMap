using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TsMap.Common;
using TsMap.Helpers.Logger;
using TsMap.TsItem;

namespace TsMap.Export
{
    /// <summary>
    /// Exports every ferry/train port and its connections as a single JSON file, e.g.
    /// "usa-ferries.json" for ATS or "europe-ferries.json" for ETS2 - same naming
    /// convention as <see cref="RouteAdvisorExporter"/>.
    ///
    /// Everything is read straight off the loaded <see cref="TsMapper"/>, which means whatever
    /// mods were ticked in the mod list are already merged into the output - no extra handling
    /// needed here (same as RouteAdvisorExporter).
    /// </summary>
    public static class FerryExporter
    {
        private const Formatting JsonFormatting = Formatting.Indented;

        public static void Export(TsMapper mapper, string path)
        {
            if (!Directory.Exists(path))
            {
                Logger.Instance.Error($"Ferry export: directory '{path}' does not exist.");
                return;
            }

            // "usa" for ATS, "europe" for ETS2 - taken from the .mbd file name so map-replacing
            // mods keep their own name (same convention as RouteAdvisorExporter).
            var prefix = string.IsNullOrEmpty(mapper.MapName)
                ? (mapper.IsEts2 ? "europe" : "usa")
                : mapper.MapName;

            var file = Path.Combine(path, $"{prefix}-ferries.json");
            var startTime = DateTime.Now.Ticks;
            var count = 0;

            using (var sw = new StreamWriter(file, false))
            using (var w = new JsonTextWriter(sw)
            {
                Formatting = JsonFormatting,
                Culture = System.Globalization.CultureInfo.InvariantCulture
            })
            {
                w.WriteStartArray();
                foreach (var port in mapper.FerryConnections)
                {
                    WritePort(w, mapper, port);
                    count++;
                }
                w.WriteEndArray();
            }

            Logger.Instance.Info($"Exported {count} ferry/train ports to '{Path.GetFileName(file)}' in " +
                                 $"{(DateTime.Now.Ticks - startTime) / TimeSpan.TicksPerMillisecond} ms");
        }

        private static void WritePort(JsonTextWriter w, TsMapper mapper, TsFerryItem port)
        {
            var node = port.Nodes != null && port.Nodes.Count > 0 ? mapper.GetNodeByUid(port.Nodes[0]) : null;

            w.WriteStartObject();

            w.WritePropertyName("token");
            w.WriteValue(ScsToken.TokenToString(port.FerryPortId));

            w.WritePropertyName("name");
            w.WriteValue(GetDisplayName(mapper, port));

            w.WritePropertyName("connections");
            w.WriteStartArray();
            foreach (var edge in mapper.GetFerryConnectionsForPort(port.FerryPortId))
            {
                var isReversed = edge.StartPortToken != port.FerryPortId; // this port is the "end" side of the edge
                var otherToken = isReversed ? edge.StartPortToken : edge.EndPortToken;
                var otherPort = mapper.FerryConnections.FirstOrDefault(p => p.FerryPortId == otherToken);
                WriteConnection(w, mapper, edge, otherPort, otherToken, isReversed);
            }
            w.WriteEndArray();

            w.WritePropertyName("dlcGuard");
            w.WriteValue((int)port.DlcGuard);

            w.WritePropertyName("uid");
            w.WriteValue(Hex(port.Uid));

            w.WritePropertyName("train");
            w.WriteValue(port.Train);

            w.WritePropertyName("nodeUid");
            w.WriteValue(node == null ? "0" : Hex(node.Uid));

            w.WritePropertyName("prefabUid");
            w.WriteValue(Hex(port.PrefabUid));

            w.WritePropertyName("x");
            w.WriteValue(port.X);

            w.WritePropertyName("y");
            w.WriteValue(port.Z);

            w.WriteEndObject();
        }

        private static void WriteConnection(JsonTextWriter w, TsMapper mapper, TsFerryConnection edge, TsFerryItem otherPort, ulong otherToken, bool isReversed)
        {
            var otherNode = otherPort?.Nodes != null && otherPort.Nodes.Count > 0 ? mapper.GetNodeByUid(otherPort.Nodes[0]) : null;

            w.WriteStartObject();

            w.WritePropertyName("token");
            w.WriteValue(ScsToken.TokenToString(otherToken));

            w.WritePropertyName("price");
            w.WriteValue(edge.Price);

            w.WritePropertyName("time");
            w.WriteValue(edge.Time);

            w.WritePropertyName("distance");
            w.WriteValue(edge.Distance);

            w.WritePropertyName("intermediatePoints");
            w.WriteStartArray();
            foreach (var p in edge.GetIntermediatePoints(isReversed))
            {
                w.WriteStartObject();
                w.WritePropertyName("x");
                w.WriteValue(p.X);
                w.WritePropertyName("y");
                w.WriteValue(p.Z);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WritePropertyName("dlcGuard");
            w.WriteValue(otherPort != null ? (int)otherPort.DlcGuard : 0);

            w.WritePropertyName("name");
            w.WriteValue(otherPort != null ? GetDisplayName(mapper, otherPort) : ScsToken.TokenToString(otherToken));

            w.WritePropertyName("nodeUid");
            w.WriteValue(otherNode == null ? "0" : Hex(otherNode.Uid));

            w.WritePropertyName("x");
            w.WriteValue(otherPort?.X ?? 0);

            w.WritePropertyName("y");
            w.WriteValue(otherPort?.Z ?? 0);

            w.WriteEndObject();
        }

        private static string Hex(ulong uid)
        {
            return uid.ToString("x");
        }

        /// <summary>
        /// SCS doesn't attach a human-readable name to the port item itself, so this
        /// approximates one from the closest city on the map (same idea as the "Fourchon"/
        /// "Cameron"/"Galveston" names in usa-ferries.json). Falls back to the raw port
        /// token, title-cased, if no city is close enough (e.g. a modded/standalone port).
        /// NOTE: if the loaded mods/base map expose a proper port-name lookup (e.g. a
        /// "def/ferry/*.sii" registry with name/name_localized per port token), prefer
        /// wiring that in here instead - this is a best-effort fallback only.
        /// </summary>
        private static string GetDisplayName(TsMapper mapper, TsFerryItem port)
        {
            const double maxDistance = 5000; // world units; ports are usually right next to their city

            TsCityItem closest = null;
            var closestDistSq = double.MaxValue;
            foreach (var city in mapper.Cities)
            {
                var dx = city.X - port.X;
                var dz = city.Z - port.Z;
                var distSq = dx * dx + dz * dz;
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closest = city;
                }
            }

            if (closest != null && closestDistSq <= maxDistance * maxDistance)
            {
                var localized = mapper.Localization.GetPreferredLocaleValue(closest.City.LocalizationToken);
                if (!string.IsNullOrWhiteSpace(localized)) return localized;
                if (!string.IsNullOrWhiteSpace(closest.City.Name)) return closest.City.Name;
            }

            var token = ScsToken.TokenToString(port.FerryPortId);
            return string.IsNullOrEmpty(token) ? "" : char.ToUpperInvariant(token[0]) + token.Substring(1);
        }
    }
}
