using System;
using System.IO;
using Newtonsoft.Json;
using TsMap.Common;
using TsMap.Helpers.Logger;

namespace TsMap.Export
{
    /// <summary>
    /// Exports the raw road network as JSON, using the same file layout as
    /// https://github.com/truckermudgeon/maps (roads / nodes / prefabs / roadLooks).
    ///
    /// Everything is read straight off the loaded <see cref="TsMapper"/>, which means whatever
    /// mods were ticked in the mod list (ProMods, ...) are already merged into the output -
    /// no extra handling needed here.
    /// </summary>
    public static class RouteAdvisorExporter
    {
        /// <summary>Set to <see cref="Formatting.Indented"/> if you want human-readable files (much bigger).</summary>
        private const Formatting JsonFormatting = Formatting.Indented;

        public static void Export(TsMapper mapper, string path)
        {
            if (!Directory.Exists(path))
            {
                Logger.Instance.Error($"RouteAdvisor export: directory '{path}' does not exist.");
                return;
            }

            // "usa" for ATS, "europe" for ETS2 - taken from the .mbd file name so map-replacing
            // mods keep their own name. Change this if you want plain roads.json / nodes.json / ...
            var prefix = string.IsNullOrEmpty(mapper.MapName)
                ? (mapper.IsEts2 ? "europe" : "usa")
                : mapper.MapName;

            var startTime = DateTime.Now.Ticks;

            ExportRoadLooks(mapper, Path.Combine(path, $"{prefix}-roadLooks.json"));
            ExportRoads(mapper, Path.Combine(path, $"{prefix}-roads.json"));
            ExportPrefabs(mapper, Path.Combine(path, $"{prefix}-prefabs.json"));
            ExportNodes(mapper, Path.Combine(path, $"{prefix}-nodes.json"));

            Logger.Instance.Info($"RouteAdvisor export ({prefix}) written to '{path}' in " +
                                 $"{(DateTime.Now.Ticks - startTime) / TimeSpan.TicksPerMillisecond} ms");
        }

        private static JsonTextWriter CreateWriter(StreamWriter sw)
        {
            return new JsonTextWriter(sw)
            {
                Formatting = JsonFormatting,
                Culture = System.Globalization.CultureInfo.InvariantCulture
            };
        }

        /// <summary>Uids are written as lowercase hex strings without leading zeros ("0" when unset).</summary>
        private static string Hex(ulong uid)
        {
            return uid.ToString("x");
        }

        private static double NormalizeAngle(double angle)
        {
            const double twoPi = Math.PI * 2;
            angle %= twoPi;
            if (angle > Math.PI) angle -= twoPi;
            else if (angle <= -Math.PI) angle += twoPi;
            return angle;
        }

        // ------------------------------------------------------------------ roadLooks

        private static void ExportRoadLooks(TsMapper mapper, string file)
        {
            var count = 0;
            using (var sw = new StreamWriter(file, false))
            using (var w = CreateWriter(sw))
            {
                w.WriteStartArray();
                foreach (var look in mapper.GetAllRoadLooks())
                {
                    w.WriteStartObject();

                    w.WritePropertyName("token");
                    w.WriteValue(ScsToken.TokenToString(look.Token));

                    w.WritePropertyName("name");
                    w.WriteValue(look.Name ?? "");

                    w.WritePropertyName("lanesLeft");
                    w.WriteStartArray();
                    foreach (var lane in look.LanesLeft) w.WriteValue(lane);
                    w.WriteEndArray();

                    w.WritePropertyName("lanesRight");
                    w.WriteStartArray();
                    foreach (var lane in look.LanesRight) w.WriteValue(lane);
                    w.WriteEndArray();

                    if (look.Offset != 0)
                    {
                        w.WritePropertyName("offset");
                        w.WriteValue(look.Offset);
                    }

                    w.WritePropertyName("shoulderSpaceLeft");
                    w.WriteValue(look.ShoulderSpaceLeft);

                    w.WritePropertyName("shoulderSpaceRight");
                    w.WriteValue(look.ShoulderSpaceRight);

                    w.WriteEndObject();
                    count++;
                }
                w.WriteEndArray();
            }
            Logger.Instance.Info($"Exported {count} road looks to '{Path.GetFileName(file)}'");
        }

        // ---------------------------------------------------------------------- roads

        private static void ExportRoads(TsMapper mapper, string file)
        {
            var count = 0;
            using (var sw = new StreamWriter(file, false))
            using (var w = CreateWriter(sw))
            {
                w.WriteStartArray();
                foreach (var road in mapper.AllRoads)
                {
                    if (!road.Valid || road.RoadLook == null) continue;

                    var startNode = road.GetStartNode();
                    var endNode = road.GetEndNode();

                    w.WriteStartObject();

                    w.WritePropertyName("uid");
                    w.WriteValue(Hex(road.Uid));

                    w.WritePropertyName("type");
                    w.WriteValue((int)road.Type);

                    w.WritePropertyName("x");
                    w.WriteValue(road.X);

                    w.WritePropertyName("y");
                    w.WriteValue(road.Z);

                    w.WritePropertyName("dlcGuard");
                    w.WriteValue(road.DlcGuard);

                    w.WritePropertyName("roadLookToken");
                    w.WriteValue(ScsToken.TokenToString(road.RoadLook.Token));

                    if (road.Hidden)
                    {
                        w.WritePropertyName("hidden");
                        w.WriteValue(true);
                    }

                    if (road.IsSecret)
                    {
                        w.WritePropertyName("secret");
                        w.WriteValue(true);
                    }

                    w.WritePropertyName("startNodeUid");
                    w.WriteValue(startNode == null ? "0" : Hex(startNode.Uid));

                    w.WritePropertyName("endNodeUid");
                    w.WriteValue(endNode == null ? "0" : Hex(endNode.Uid));

                    w.WritePropertyName("length");
                    w.WriteValue(RoadGeometry.GetRoadLength(startNode, endNode));

                    w.WriteEndObject();
                    count++;
                }
                w.WriteEndArray();
            }
            Logger.Instance.Info($"Exported {count} roads to '{Path.GetFileName(file)}'");
        }

        // -------------------------------------------------------------------- prefabs

        private static void ExportPrefabs(TsMapper mapper, string file)
        {
            var count = 0;
            using (var sw = new StreamWriter(file, false))
            using (var w = CreateWriter(sw))
            {
                w.WriteStartArray();
                foreach (var prefabItem in mapper.AllPrefabsByUid.Values)
                {
                    if (!prefabItem.Valid || prefabItem.Prefab == null) continue;

                    w.WriteStartObject();

                    w.WritePropertyName("uid");
                    w.WriteValue(Hex(prefabItem.Uid));

                    w.WritePropertyName("type");
                    w.WriteValue((int)prefabItem.Type);

                    w.WritePropertyName("x");
                    w.WriteValue(prefabItem.X);

                    w.WritePropertyName("y");
                    w.WriteValue(prefabItem.Z);

                    w.WritePropertyName("dlcGuard");
                    w.WriteValue(prefabItem.DlcGuard);

                    w.WritePropertyName("token");
                    w.WriteValue(ScsToken.TokenToString(prefabItem.Prefab.Token));

                    if (prefabItem.Hidden)
                    {
                        w.WritePropertyName("hidden");
                        w.WriteValue(true);
                    }

                    if (prefabItem.IsSecret)
                    {
                        w.WritePropertyName("secret");
                        w.WriteValue(true);
                    }

                    w.WritePropertyName("nodeUids");
                    w.WriteStartArray();
                    if (prefabItem.Nodes != null)
                    {
                        foreach (var nodeUid in prefabItem.Nodes) w.WriteValue(Hex(nodeUid));
                    }
                    w.WriteEndArray();

                    w.WritePropertyName("originNodeIndex");
                    w.WriteValue(prefabItem.Origin);

                    w.WriteEndObject();
                    count++;
                }
                w.WriteEndArray();
            }
            Logger.Instance.Info($"Exported {count} prefabs to '{Path.GetFileName(file)}'");
        }

        // ---------------------------------------------------------------------- nodes

        private static void ExportNodes(TsMapper mapper, string file)
        {
            var count = 0;
            using (var sw = new StreamWriter(file, false))
            using (var w = CreateWriter(sw))
            {
                w.WriteStartArray();
                foreach (var node in mapper.Nodes.Values)
                {
                    w.WriteStartObject();

                    w.WritePropertyName("uid");
                    w.WriteValue(Hex(node.Uid));

                    w.WritePropertyName("x");
                    w.WriteValue(node.X);

                    w.WritePropertyName("y");
                    w.WriteValue(node.Z);

                    w.WritePropertyName("z");
                    w.WriteValue(node.Y);

                    // Same convention as truckermudgeon: yaw derived from the raw quaternion,
                    // shifted by -pi/2 and normalised to (-pi, pi].
                    var yaw = (Math.PI - Math.Atan2(node.RotationQuat2, node.RotationQuat0)) % Math.PI * 2;
                    w.WritePropertyName("rotation");
                    w.WriteValue(NormalizeAngle(yaw - Math.PI / 2));

                    w.WritePropertyName("rotationQuat");
                    w.WriteStartArray();
                    w.WriteValue(node.RotationQuat0);
                    w.WriteValue(node.RotationQuat1);
                    w.WriteValue(node.RotationQuat2);
                    w.WriteValue(node.RotationQuat3);
                    w.WriteEndArray();

                    w.WritePropertyName("forwardItemUid");
                    w.WriteValue(Hex(node.ForwardItemUid));

                    w.WritePropertyName("backwardItemUid");
                    w.WriteValue(Hex(node.BackwardItemUid));

                    w.WritePropertyName("forwardCountryId");
                    w.WriteValue(node.ForwardCountryId);

                    w.WritePropertyName("backwardCountryId");
                    w.WriteValue(node.BackwardCountryId);

                    w.WriteEndObject();
                    count++;
                }
                w.WriteEndArray();
            }
            Logger.Instance.Info($"Exported {count} nodes to '{Path.GetFileName(file)}'");
        }
    }
}
