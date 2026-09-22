using System;
using System.Linq;

namespace TsMap.Export
{
    /// <summary>Geometry helpers shared between RouteAdvisorExporter and GraphExporter.</summary>
    internal static class RoadGeometry
    {
        /// <summary>Number of samples used to approximate the length of a curved road.</summary>
        private const int LengthSamples = 32;

        /// <summary>
        /// Approximates a road segment's length (in map units, i.e. meters) by sampling the
        /// same Hermite curve the renderer draws.
        /// </summary>
        public static double GetRoadLength(TsNode startNode, TsNode endNode)
        {
            if (startNode == null || endNode == null) return 0;

            var sx = startNode.X;
            var sz = startNode.Z;
            var ex = endNode.X;
            var ez = endNode.Z;

            var radius = Math.Sqrt(Math.Pow(sx - ex, 2) + Math.Pow(sz - ez, 2));

            var tanSx = Math.Cos(-(Math.PI * 0.5f - startNode.Rotation)) * radius;
            var tanEx = Math.Cos(-(Math.PI * 0.5f - endNode.Rotation)) * radius;
            var tanSz = Math.Sin(-(Math.PI * 0.5f - startNode.Rotation)) * radius;
            var tanEz = Math.Sin(-(Math.PI * 0.5f - endNode.Rotation)) * radius;

            double length = 0;
            double prevX = sx;
            double prevZ = sz;

            for (var i = 1; i < LengthSamples; i++)
            {
                var s = i / (float)(LengthSamples - 1);
                var x = TsRoadLook.Hermite(s, sx, ex, tanSx, tanEx);
                var z = TsRoadLook.Hermite(s, sz, ez, tanSz, tanEz);
                length += Math.Sqrt((x - prevX) * (x - prevX) + (z - prevZ) * (z - prevZ));
                prevX = x;
                prevZ = z;
            }

            return length;
        }

        public static double Distance(float x1, float z1, float x2, float z2)
        {
            return Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(z1 - z2, 2));
        }

        /// <summary>
        /// Exact port of truckermudgeon/maps' getLaneSpeedClass (@truckermudgeon/map/roads.ts):
        /// classifies a road by substring-matching its lane look tokens. Returns snake_case
        /// names matching the keys TsCountry.TruckSpeedLimits stores (parsed directly from
        /// speed_limits.sii's own lane_speed_class[] values), rather than the original's
        /// camelCase LaneSpeedClass - purely an internal naming choice, same effect.
        /// </summary>
        public static string GetLaneSpeedClass(TsRoadLook look)
        {
            var lanes = look.LanesLeft.Concat(look.LanesRight).ToList();
            if (lanes.Any(l => l.Contains("freeway"))) return "freeway";
            if (lanes.Any(l => l.Contains("motorway"))) return "motorway";
            if (lanes.Any(l => l.Contains("expressway"))) return "expressway";
            if (lanes.Any(l => l.Contains("divided"))) return "divided_road";
            if (lanes.Any(l => l.Contains("slow_road"))) return "slow_road";
            return "local_road";
        }
    }
}
