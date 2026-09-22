using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TsMap.Helpers.Logger;
using TsMap.Map.Overlays;
using TsMap.TsItem;

namespace TsMap.Export
{
    /// <summary>
    /// Exports a turn-by-turn navigation graph as "{prefix}-graph.json", ported from
    /// truckermudgeon/maps' packages/clis/generator/graph/graph.ts (generateGraph /
    /// getNeighborsInDirection / updateGraphWithFerries / toNeighbor), reading their actual
    /// TypeScript source plus @truckermudgeon/map's prefabs.ts (calculateNodeConnections) and
    /// roads.ts (getLaneSpeedClass).
    ///
    /// Faithful ports (same underlying data TsMap already parses, or newly wired up here):
    ///   - node.ForwardItemUid/BackwardItemUid-driven traversal (getNeighborsInDirection),
    ///   - road edges: lanesRight/lanesLeft gating direction, real sampled curve length,
    ///   - real speed classification (RoadGeometry.GetLaneSpeedClass, an exact port of
    ///     getLaneSpeedClass's lane-token substring rules) and real per-country truck speed
    ///     limits (parsed from the country's own speed_limits.sii by TsCountry), instead of a
    ///     flat guess,
    ///   - companies: a node whose forward/backward item is a "company prefab" gets a direct
    ///     edge to that company's own spawn node (same condition as the original: skipped when
    ///     the origin node has no item behind/ahead of it, matching gNID:Prefab:CompanyItem),
    ///   - intersection/prefab connectivity: derived from TsPrefab's already-parsed MapPoints/
    ///     Neighbours/ControlNodeIndex graph (the same underlying "which points inside this
    ///     prefab connect to which" data used for rendering), via a Dijkstra over that graph
    ///     from each boundary node - this is the actual physical road-shape connectivity, not an
    ///     "every boundary node connects to every other" guess. It is NOT the same as the
    ///     original's lane-level navCurve graph (see below) but only proposes an edge where a
    ///     real drivable path exists, with a real path length,
    ///   - the "dead end" post-processing pass, and the ferry linking pass (entrance/exit/dock/
    ///     connections, including the exact "distance * 100" ferry-crossing scaling and omitting
    ///     "duration" on sea crossings).
    ///
    /// Still NOT a byte-for-byte match, because the data isn't available:
    ///   - true lane-level turn restrictions - the original's `calculateNodeConnections` walks
    ///     the prefab's navCurves (with real per-lane prevLines/nextLines directionality,
    ///     semaphores, traffic rules). TsMap's own .ppd parser (TsPrefab.cs) never reads the
    ///     navCurve block at all (only nodes/spawnPoints/mapPoints/triggerPoints) - that byte
    ///     layout isn't in this project. The MapPoints-based Dijkstra above is a real geometric
    ///     stand-in, but treats the road graph as undirected (both ways), so it can't represent
    ///     a lane that's only legal one way through an intersection.
    ///   - the original picks a road's speed class from whichever road is spatially closest to
    ///     the EDGE'S DESTINATION node (a quadtree lookup) for every edge type, even prefab/
    ///     company/ferry-dock edges. For road-to-road edges we use the traversed road's own
    ///     RoadLook instead (exact, not an approximation - strictly better than their spatial
    ///     guess since we have ground truth). For prefab/company/dock edges, where there's no
    ///     "current road" at all, we use "local_road" directly rather than building a spatial
    ///     road index just for this - a reasonable simplification given how localized those
    ///     edges are.
    ///   - "dlcGuard" on an edge - the original looks it up from a DLC-guard spatial index
    ///     built from map-area polygons. Road/prefab edges here use the item's own DlcGuard
    ///     (exact ground truth, not an approximation); company/dock/ferry edges fall back to
    ///     whichever item is attached to the destination node.
    ///
    /// Everything is read straight off the loaded <see cref="TsMapper"/>, so whatever mods were
    /// ticked in the mod list are already merged into the output - no extra handling needed here.
    /// Unlike the original tool, this does NOT filter out content from un-selected DLCs.
    /// </summary>
    public static class GraphExporter
    {
        private const Formatting JsonFormatting = Formatting.Indented;

        /// <summary>Absolute last-resort speed (mph) if a country has no speed-limit data at all - matches the original's literal fallback of 30.</summary>
        private const double FallbackSpeedMph = 30;

        /// <summary>km/h -> mph, used for every country except the USA (whose own data is already in mph) - matches the original exactly.</summary>
        private const double KmhToMph = 0.6213712;

        private static readonly HashSet<string> FacilityIcons = new HashSet<string>
        {
            "weigh_station_ico",
            "dealer_ico",
            "garage_large_ico",
            "recruitment_ico",
            "parking_ico",
            "gas_ico",
            "service_ico"
        };

        private class Edge
        {
            public ulong TargetUid;
            public double Distance;
            public double? Duration; // null => omitted from JSON, matching the original's undefined
            public string Direction;
            public byte DlcGuard;
            public bool IsOneLaneRoad;
            public bool IsFerry;
        }

        private class Adjacency
        {
            public readonly List<Edge> Forward = new List<Edge>();
            public readonly List<Edge> Backward = new List<Edge>();
        }

        private class BuildState
        {
            public Dictionary<ulong, TsRoadItem> RoadsByUid;
            public Dictionary<ulong, TsCompanyItem> CompaniesByPrefabUid;
            public Dictionary<ulong, Dictionary<int, List<(int targetNodeIndex, double distance)>>> PrefabConnectivityByToken;
            public Dictionary<ulong, Adjacency> Graph;
        }

        public static void Export(TsMapper mapper, string path)
        {
            if (!Directory.Exists(path))
            {
                Logger.Instance.Error($"Graph export: directory '{path}' does not exist.");
                return;
            }

            var prefix = string.IsNullOrEmpty(mapper.MapName)
                ? (mapper.IsEts2 ? "europe" : "usa")
                : mapper.MapName;

            var startTime = DateTime.Now.Ticks;

            var state = new BuildState
            {
                RoadsByUid = mapper.AllRoads.Where(r => r.Valid).GroupBy(r => r.Uid).ToDictionary(g => g.Key, g => g.First()),
                CompaniesByPrefabUid = mapper.Companies.Where(c => c.Valid && c.PrefabUid != 0).GroupBy(c => c.PrefabUid).ToDictionary(g => g.Key, g => g.First()),
                PrefabConnectivityByToken = new Dictionary<ulong, Dictionary<int, List<(int targetNodeIndex, double distance)>>>(),
                Graph = new Dictionary<ulong, Adjacency>()
            };

            foreach (var node in mapper.Nodes.Values)
            {
                var forward = GetNeighborsInDirection(mapper, state, node, "forward");
                var backward = GetNeighborsInDirection(mapper, state, node, "backward");
                if (forward.Count == 0 && backward.Count == 0) continue;

                var adj = new Adjacency();
                adj.Forward.AddRange(forward);
                adj.Backward.AddRange(backward);
                state.Graph[node.Uid] = adj;
            }

            AddFerryEdges(mapper, state);

            var fudged = FudgeDeadEnds(mapper, state);
            Logger.Instance.Info($"{fudged} hacky dead-end edges added");

            var serviceAreas = BuildServiceAreas(mapper);

            var file = Path.Combine(path, $"{prefix}-graph.json");
            using (var sw = new StreamWriter(file, false))
            using (var w = new JsonTextWriter(sw)
            {
                Formatting = JsonFormatting,
                Culture = System.Globalization.CultureInfo.InvariantCulture
            })
            {
                w.WriteStartObject();

                w.WritePropertyName("graph");
                w.WriteStartArray();
                foreach (var kvp in state.Graph) WriteGraphEntry(w, kvp.Key, kvp.Value);
                w.WriteEndArray();

                w.WritePropertyName("serviceAreas");
                w.WriteStartArray();
                foreach (var kvp in serviceAreas) WriteServiceAreaEntry(w, kvp.Key, kvp.Value);
                w.WriteEndArray();

                w.WriteEndObject();
            }

            var edgeCount = state.Graph.Values.Sum(a => a.Forward.Count + a.Backward.Count);
            Logger.Instance.Info($"Exported graph with {state.Graph.Count} nodes, {edgeCount} edges to " +
                $"'{Path.GetFileName(file)}' in {(DateTime.Now.Ticks - startTime) / TimeSpan.TicksPerMillisecond} ms");
        }

        // ----------------------------------------------------- core traversal (roads + prefabs)

        private static List<Edge> GetNeighborsInDirection(TsMapper mapper, BuildState state, TsNode originNode, string direction)
        {
            var itemId = direction == "forward" ? originNode.ForwardItemUid : originNode.BackwardItemUid;
            if (itemId == 0) return new List<Edge>();

            if (state.RoadsByUid.TryGetValue(itemId, out var road))
            {
                return GetRoadNeighbor(mapper, originNode, direction, road);
            }

            if (mapper.AllPrefabsByUid.TryGetValue(itemId, out var prefab))
            {
                if (state.CompaniesByPrefabUid.TryGetValue(itemId, out var company))
                {
                    return GetCompanyNeighbor(mapper, originNode, direction, company);
                }
                return GetPrefabNeighbors(mapper, state, originNode, direction, prefab);
            }

            return new List<Edge>();
        }

        private static List<Edge> GetRoadNeighbor(TsMapper mapper, TsNode originNode, string direction, TsRoadItem road)
        {
            if (road.RoadLook == null) return new List<Edge>();

            var lanesInDirection = direction == "forward" ? road.RoadLook.LanesRight.Count : road.RoadLook.LanesLeft.Count;
            if (lanesInDirection == 0) return new List<Edge>();

            var startNode = road.GetStartNode();
            var endNode = road.GetEndNode();
            if (startNode == null || endNode == null) return new List<Edge>();

            var destNode = direction == "forward" ? endNode : startNode;
            var distance = RoadGeometry.GetRoadLength(startNode, endNode);
            var speedClass = RoadGeometry.GetLaneSpeedClass(road.RoadLook);

            return new List<Edge>
            {
                new Edge
                {
                    TargetUid = destNode.Uid,
                    Distance = distance,
                    Duration = CalculateDurationSeconds(GetSpeedMph(mapper, destNode, direction, speedClass), distance),
                    Direction = direction,
                    DlcGuard = road.DlcGuard,
                    IsOneLaneRoad = lanesInDirection == 1
                }
            };
        }

        private static List<Edge> GetCompanyNeighbor(TsMapper mapper, TsNode originNode, string direction, TsCompanyItem company)
        {
            // Matches the original's guard exactly: skip if the origin node has nothing behind/
            // ahead of it on the *other* side either (an isolated dead-end approach to the company).
            if ((direction == "forward" && originNode.BackwardItemUid == 0) ||
                (direction == "backward" && originNode.ForwardItemUid == 0))
            {
                return new List<Edge>();
            }

            if (company.Nodes == null || company.Nodes.Count == 0) return new List<Edge>();
            var nextNode = mapper.GetNodeByUid(company.Nodes[0]);
            if (nextNode == null) return new List<Edge>();

            var distance = RoadGeometry.Distance(originNode.X, originNode.Z, nextNode.X, nextNode.Z);
            return new List<Edge>
            {
                new Edge
                {
                    TargetUid = nextNode.Uid,
                    Distance = distance,
                    Duration = CalculateDurationSeconds(GetSpeedMph(mapper, nextNode, direction, "local_road"), distance),
                    Direction = direction,
                    DlcGuard = company.DlcGuard
                }
            };
        }

        private static List<Edge> GetPrefabNeighbors(TsMapper mapper, BuildState state, TsNode originNode, string direction, TsPrefabItem prefab)
        {
            var result = new List<Edge>();
            if (!prefab.Valid || prefab.Nodes == null || prefab.Prefab == null) return result;

            var nodeCount = prefab.Nodes.Count;
            var mapOrderIndex = prefab.Nodes.IndexOf(originNode.Uid);
            if (mapOrderIndex < 0) return result;

            // .ppd-local node index <-> map-order index conversion. A placed prefab item's
            // Nodes[] is rotated relative to the descriptor's own local node order by
            // TsPrefabItem.Origin (ported concept: TruckLib/@truckermudgeon's
            // `rotateRight(item.nodeUids, item.originNodeIndex)`) - Nodes[k] corresponds to
            // local .ppd index (Origin + k) mod N, not to local index k directly.
            int ToLocal(int mapIdx) => nodeCount == 0 ? mapIdx : (mapIdx + prefab.Origin) % nodeCount;
            int ToMapOrder(int localIdx) => nodeCount == 0 ? localIdx : (((localIdx - prefab.Origin) % nodeCount) + nodeCount) % nodeCount;

            var connectivity = GetOrComputePrefabConnectivity(state, prefab.Prefab);
            var originLocalIndex = ToLocal(mapOrderIndex);

            IEnumerable<(ulong targetUid, double distance)> candidates;
            if (connectivity.Count > 0 && connectivity.TryGetValue(originLocalIndex, out var reachable))
            {
                // Real lane-level connectivity (via NavCurves, or the MapPoints fallback - see
                // GetOrComputePrefabConnectivity) is available for this prefab - only propose
                // edges to boundary nodes actually reachable by a real path.
                candidates = reachable
                    .Select(r => (mapOrder: ToMapOrder(r.targetNodeIndex), r.distance))
                    .Where(t => t.mapOrder < prefab.Nodes.Count && prefab.Nodes[t.mapOrder] != 0)
                    .Select(t => (prefab.Nodes[t.mapOrder], t.distance));
            }
            else if (connectivity.Count > 0)
            {
                // This prefab has real connectivity data, but this particular boundary node isn't
                // reachable from any other one (a genuine dead end into this prefab) - no edges.
                return result;
            }
            else
            {
                // No usable NavCurve/MapPoints data at all for this prefab (e.g. a non-road/
                // scenery-connector prefab) - fall back to linking every pair of boundary nodes directly.
                candidates = prefab.Nodes
                    .Select((uid, idx) => (idx, uid))
                    .Where(t => t.uid != 0 && t.idx != mapOrderIndex)
                    .Select(t => (t.uid, (double)RoadGeometry.Distance(originNode.X, originNode.Z,
                        mapper.GetNodeByUid(t.uid)?.X ?? originNode.X, mapper.GetNodeByUid(t.uid)?.Z ?? originNode.Z)));
            }

            foreach (var candidate in candidates)
            {
                var targetUid = candidate.targetUid;
                var dist = candidate.distance;
                var other = mapper.GetNodeByUid(targetUid);
                if (other == null) continue;

                var originNeighborItem = direction == "forward" ? originNode.ForwardItemUid : originNode.BackwardItemUid;
                var otherNeighborItem = direction == "forward" ? other.ForwardItemUid : other.BackwardItemUid;
                var edgeDirection = originNeighborItem == otherNeighborItem
                    ? (direction == "forward" ? "backward" : "forward")
                    : direction;

                result.Add(new Edge
                {
                    TargetUid = other.Uid,
                    Distance = dist,
                    Duration = CalculateDurationSeconds(GetSpeedMph(mapper, other, edgeDirection, "local_road"), dist),
                    Direction = edgeDirection,
                    DlcGuard = prefab.DlcGuard
                });
            }

            return result;
        }

        /// <summary>
        /// Connectivity between a prefab's boundary nodes (by .ppd-local node index), used by
        /// GetPrefabNeighbors. Merges (union of) two sources:
        ///   - real, lane-level connectivity derived from the descriptor's NavCurves (ported from
        ///     TruckLib.Models/@truckermudgeon's calculateNodeConnections - see
        ///     ComputeNavCurveConnectivity),
        ///   - a geometric Dijkstra over MapPoints/Neighbours (ComputeMapPointConnectivity, the
        ///     previous approach).
        /// The two are merged rather than NavCurves simply taking priority, defensively: this is
        /// new binary-parsing code (see TsPrefab.cs) without a byte-layout reference to verify
        /// against, so a subtle bug there (e.g. for one specific prefab variant) could silently
        /// yield zero/partial connectivity for an otherwise perfectly normal road prefab. Losing a
        /// real intersection's connectivity fragments the whole road network around it, which is
        /// far worse than occasionally keeping the less-precise MapPoints-derived edge alongside
        /// the NavCurve one. When both sources report the same node pair, the smaller (more
        /// precise, presumably NavCurve-derived) distance wins. Cached per prefab description
        /// token, since many prefab *items* on the map share the same description.
        /// </summary>
        private static Dictionary<int, List<(int targetNodeIndex, double distance)>> GetOrComputePrefabConnectivity(BuildState state, TsPrefab desc)
        {
            if (state.PrefabConnectivityByToken.TryGetValue(desc.Token, out var cached)) return cached;

            var navCurveResult = desc.ValidRoad && desc.NavCurves != null && desc.NavCurves.Count > 0 && desc.PrefabNodes.Count > 0
                ? ComputeNavCurveConnectivity(desc)
                : new Dictionary<int, List<(int, double)>>();
            var mapPointResult = ComputeMapPointConnectivity(desc);

            var result = MergeConnectivity(navCurveResult, mapPointResult);

            state.PrefabConnectivityByToken[desc.Token] = result;
            return result;
        }

        private static Dictionary<int, List<(int, double)>> MergeConnectivity(
            Dictionary<int, List<(int, double)>> a, Dictionary<int, List<(int, double)>> b)
        {
            if (a.Count == 0) return b;
            if (b.Count == 0) return a;

            var merged = new Dictionary<int, Dictionary<int, double>>();
            void AddAll(Dictionary<int, List<(int, double)>> src)
            {
                foreach (var kvp in src)
                {
                    if (!merged.TryGetValue(kvp.Key, out var byTarget))
                    {
                        byTarget = new Dictionary<int, double>();
                        merged[kvp.Key] = byTarget;
                    }
                    foreach (var (target, dist) in kvp.Value)
                    {
                        if (!byTarget.TryGetValue(target, out var best) || dist < best) byTarget[target] = dist;
                    }
                }
            }
            AddAll(a);
            AddAll(b);

            return merged.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Select(t => (t.Key, t.Value)).ToList());
        }

        /// <summary>
        /// Real, lane-level connectivity between a prefab's boundary nodes. Ported from
        /// @truckermudgeon/map/prefabs.ts's calculateNodeConnections (getEndingNodeIndices /
        /// getPaths), operating on TsPrefab.NavCurves/PrefabNodes.InputLines/OutputLines - data
        /// that TsMap did not parse until now (see TsPrefab.cs; ported from TruckLib.Models,
        /// GPL v2). Distance is the sum of each traversed NavCurve's own precomputed Length,
        /// which is what the game itself uses for AI navigation - not a re-sampled polyline like
        /// the original's toSplinePoints/simplify (a close, much simpler equivalent).
        /// A curve that is itself an OutputLine of some node is treated as a terminal leaf (its
        /// own NextLines, if any, aren't followed further), matching the original exactly.
        /// </summary>
        private static Dictionary<int, List<(int, double)>> ComputeNavCurveConnectivity(TsPrefab desc)
        {
            var result = new Dictionary<int, List<(int, double)>>();

            var endingCurveIndexToNodeIndex = new Dictionary<int, int>();
            for (var nodeIndex = 0; nodeIndex < desc.PrefabNodes.Count; nodeIndex++)
            {
                var outputLines = desc.PrefabNodes[nodeIndex].OutputLines;
                if (outputLines == null) continue;
                foreach (var outputLane in outputLines)
                {
                    if (outputLane >= 0 && outputLane < desc.NavCurves.Count) endingCurveIndexToNodeIndex[outputLane] = nodeIndex;
                }
            }

            for (var nodeIndex = 0; nodeIndex < desc.PrefabNodes.Count; nodeIndex++)
            {
                var reachable = new Dictionary<int, double>();
                var inputLines = desc.PrefabNodes[nodeIndex].InputLines;
                if (inputLines != null)
                {
                    foreach (var inputLane in inputLines)
                    {
                        if (inputLane < 0 || inputLane >= desc.NavCurves.Count) continue;
                        WalkNavCurve(desc, endingCurveIndexToNodeIndex, inputLane, 0, new HashSet<int>(), reachable);
                    }
                }
                if (reachable.Count > 0) result[nodeIndex] = reachable.Select(r => (r.Key, r.Value)).ToList();
            }

            return result;
        }

        private static void WalkNavCurve(TsPrefab desc, Dictionary<int, int> endingCurveIndexToNodeIndex,
            int curveIndex, double accLength, HashSet<int> visited, Dictionary<int, double> reachable)
        {
            if (!visited.Add(curveIndex)) return; // cycle guard (roundabouts etc.)

            var curve = desc.NavCurves[curveIndex];
            var totalLength = accLength + curve.Length;

            if (endingCurveIndexToNodeIndex.TryGetValue(curveIndex, out var endNode))
            {
                if (!reachable.TryGetValue(endNode, out var best) || totalLength < best) reachable[endNode] = totalLength;
                return; // an ending curve is a terminal leaf - matches the original exactly.
            }

            if (curve.NextLines == null) return;
            foreach (var next in curve.NextLines)
            {
                if (next >= 0 && next < desc.NavCurves.Count)
                {
                    WalkNavCurve(desc, endingCurveIndexToNodeIndex, next, totalLength, visited, reachable);
                }
            }
        }

        /// <summary>
        /// Geometric stand-in for descriptors with no NavCurve data at all, derived from the
        /// descriptor's MapPoints/Neighbours/ControlNodeIndex graph (used for rendering) via
        /// Dijkstra. Not lane-accurate (undirected, no turn restrictions) - see class doc.
        /// </summary>
        private static Dictionary<int, List<(int, double)>> ComputeMapPointConnectivity(TsPrefab desc)
        {
            var result = new Dictionary<int, List<(int, double)>>();
            var mapPoints = desc.MapPoints;
            if (mapPoints == null || mapPoints.Count == 0) return result;

            var controlPointsByNodeIndex = new Dictionary<int, List<int>>();
            for (var mp = 0; mp < mapPoints.Count; mp++)
            {
                var ci = mapPoints[mp].ControlNodeIndex;
                if (ci < 0) continue;
                if (!controlPointsByNodeIndex.TryGetValue(ci, out var list))
                {
                    list = new List<int>();
                    controlPointsByNodeIndex[ci] = list;
                }
                list.Add(mp);
            }

            foreach (var kvp in controlPointsByNodeIndex)
            {
                var dist = new double[mapPoints.Count];
                for (var i = 0; i < dist.Length; i++) dist[i] = double.PositiveInfinity;
                var visited = new bool[mapPoints.Count];
                var pending = new SortedSet<(double d, int idx)>();
                foreach (var startMp in kvp.Value)
                {
                    dist[startMp] = 0;
                    pending.Add((0, startMp));
                }

                while (pending.Count > 0)
                {
                    var cur = pending.Min;
                    pending.Remove(cur);
                    if (visited[cur.idx]) continue;
                    visited[cur.idx] = true;

                    var p = mapPoints[cur.idx];
                    if (p.Neighbours == null) continue;
                    foreach (var v in p.Neighbours)
                    {
                        if (v < 0 || v >= mapPoints.Count || visited[v]) continue;
                        var np = mapPoints[v];
                        var w = RoadGeometry.Distance(p.X, p.Z, np.X, np.Z);
                        var nd = cur.d + w;
                        if (nd < dist[v])
                        {
                            dist[v] = nd;
                            pending.Add((nd, v));
                        }
                    }
                }

                var reached = new Dictionary<int, double>();
                foreach (var otherKvp in controlPointsByNodeIndex)
                {
                    if (otherKvp.Key == kvp.Key) continue;
                    foreach (var mp in otherKvp.Value)
                    {
                        if (double.IsPositiveInfinity(dist[mp])) continue;
                        if (!reached.TryGetValue(otherKvp.Key, out var best) || dist[mp] < best)
                        {
                            reached[otherKvp.Key] = dist[mp];
                        }
                    }
                }
                result[kvp.Key] = reached.Select(r => (r.Key, r.Value)).ToList();
            }

            return result;
        }

        // ------------------------------------------------------------------------- dead ends

        private static int FudgeDeadEnds(TsMapper mapper, BuildState state)
        {
            var fudged = 0;
            foreach (var kvp in state.Graph)
            {
                var node = mapper.GetNodeByUid(kvp.Key);
                if (node == null) continue;
                var edges = kvp.Value;

                var hasForwardItem = state.RoadsByUid.ContainsKey(node.ForwardItemUid) || mapper.AllPrefabsByUid.ContainsKey(node.ForwardItemUid);
                var hasBackwardItem = state.RoadsByUid.ContainsKey(node.BackwardItemUid) || mapper.AllPrefabsByUid.ContainsKey(node.BackwardItemUid);

                if (!hasForwardItem && edges.Forward.Count == 0 && edges.Backward.Count > 0)
                {
                    foreach (var edge in edges.Backward.ToList())
                    {
                        if (!state.Graph.TryGetValue(edge.TargetUid, out var targetAdj)) continue;
                        if (targetAdj.Forward.Concat(targetAdj.Backward).Any(re => re.TargetUid == kvp.Key))
                        {
                            edges.Forward.Add(edge);
                            fudged++;
                        }
                    }
                }
                else if (!hasBackwardItem && edges.Backward.Count == 0 && edges.Forward.Count > 0)
                {
                    foreach (var edge in edges.Forward.ToList())
                    {
                        if (!state.Graph.TryGetValue(edge.TargetUid, out var targetAdj)) continue;
                        if (targetAdj.Forward.Concat(targetAdj.Backward).Any(re => re.TargetUid == kvp.Key))
                        {
                            edges.Backward.Add(edge);
                            fudged++;
                        }
                    }
                }
            }
            return fudged;
        }

        // ------------------------------------------------------------------------- ferries

        private static void AddFerryEdges(TsMapper mapper, BuildState state)
        {
            foreach (var port in mapper.FerryConnections)
            {
                if (port.Nodes == null || port.Nodes.Count == 0) continue;
                var ferryNode = mapper.GetNodeByUid(port.Nodes[0]);
                if (ferryNode == null) continue;

                var ferryAdj = state.Graph.TryGetValue(ferryNode.Uid, out var fa) ? fa : GetOrAddGraphNode(state, ferryNode.Uid, false);

                // 1) ALWAYS add the actual sea/rail crossing edges first. A previous version
                //    nested this inside the dock/entrance/exit resolution below, so whenever that
                //    failed for any reason (no resolvable prefab, or - very commonly for USA -
                //    the "exit" node already being part of the graph, since it's a real
                //    intersection on the road network) the crossing edge silently never made it
                //    into the graph at all, even though the port itself was fine.
                foreach (var edge in mapper.GetFerryConnectionsForPort(port.FerryPortId))
                {
                    var otherToken = edge.StartPortToken == port.FerryPortId ? edge.EndPortToken : edge.StartPortToken;
                    var otherPort = mapper.FerryConnections.FirstOrDefault(p => p.FerryPortId == otherToken);
                    if (otherPort?.Nodes == null || otherPort.Nodes.Count == 0) continue;
                    var otherNode = mapper.GetNodeByUid(otherPort.Nodes[0]);
                    if (otherNode == null) continue;

                    ferryAdj.Forward.Add(new Edge
                    {
                        TargetUid = otherNode.Uid,
                        // km -> m. Verified against real in-game Route Advisor numbers (Everett<->
                        // PortTownsend distance=10 + PortTownsend<->Homer distance=1500 = 1510 km,
                        // matching the game's own displayed ferry length exactly) - the earlier
                        // *100 factor (guessed from an uncertain upstream comment) was off by 10x.
                        Distance = edge.Distance * 1000,
                        Duration = null,
                        Direction = "forward",
                        DlcGuard = port.DlcGuard,
                        IsFerry = true
                    });
                }

                // 2) Best-effort dock connectivity (ferryNode <-> nearest road network, via the
                //    ferry prefab's entrance/exit) - purely to help routing reach the dock; a
                //    failure here must never remove the crossing edges added above.
                if (port.PrefabUid == 0 || !mapper.AllPrefabsByUid.TryGetValue(port.PrefabUid, out var ferryPrefab) || !ferryPrefab.Valid)
                {
                    continue;
                }

                var prefabNodes = (ferryPrefab.Nodes ?? new List<ulong>())
                    .Where(uid => uid != 0)
                    .Select(mapper.GetNodeByUid)
                    .Where(n => n != null)
                    .Distinct()
                    .ToList();
                if (prefabNodes.Count == 0) continue;

                var nodesInGraph = prefabNodes.Where(n => state.Graph.ContainsKey(n.Uid)).ToList();

                TsNode entrance;
                TsNode exit;

                if (!mapper.IsEts2)
                {
                    if (nodesInGraph.Count == 0) continue;
                    entrance = nodesInGraph[0];

                    exit = prefabNodes
                        .Where(n => n.BackwardItemUid == 0)
                        .OrderBy(n => RoadGeometry.Distance(n.X, n.Z, ferryNode.X, ferryNode.Z))
                        .FirstOrDefault() ?? prefabNodes
                        .OrderBy(n => RoadGeometry.Distance(n.X, n.Z, ferryNode.X, ferryNode.Z))
                        .First();
                }
                else
                {
                    exit = prefabNodes
                        .Where(n => n.BackwardItemUid == 0 && !state.Graph.ContainsKey(n.Uid))
                        .OrderBy(n => RoadGeometry.Distance(n.X, n.Z, ferryNode.X, ferryNode.Z))
                        .FirstOrDefault() ?? prefabNodes
                        .OrderBy(n => RoadGeometry.Distance(n.X, n.Z, ferryNode.X, ferryNode.Z))
                        .First();

                    entrance = prefabNodes
                        .Where(n => n.Uid != exit.Uid && nodesInGraph.Contains(n))
                        .OrderByDescending(n => RoadGeometry.Distance(n.X, n.Z, ferryNode.X, ferryNode.Z))
                        .FirstOrDefault();
                    if (entrance == null) continue;
                }

                if (entrance.Uid == exit.Uid) continue;

                var exitAdj = GetOrAddGraphNode(state, exit.Uid, !mapper.IsEts2);
                if (exitAdj == null) continue;

                var entranceAdj = state.Graph.TryGetValue(entrance.Uid, out var e) ? e : GetOrAddGraphNode(state, entrance.Uid, false);

                entranceAdj.Forward.Add(CreateDockNeighbor(mapper, entrance, exit, "forward"));
                entranceAdj.Backward.Add(CreateDockNeighbor(mapper, entrance, exit, "forward"));

                exitAdj.Backward.Add(CreateDockNeighbor(mapper, exit, entrance, "forward"));
                exitAdj.Backward.Add(CreateDockNeighbor(mapper, exit, entrance, "backward"));

                exitAdj.Forward.Add(CreateDockNeighbor(mapper, exit, ferryNode, "forward"));

                ferryAdj.Forward.Add(CreateDockNeighbor(mapper, ferryNode, exit, "backward"));
                ferryAdj.Backward.Add(CreateDockNeighbor(mapper, ferryNode, exit, "backward"));
            }
        }

        private static Adjacency GetOrAddGraphNode(BuildState state, ulong uid, bool mustNotExist)
        {
            if (state.Graph.TryGetValue(uid, out var existing))
            {
                return mustNotExist ? null : existing;
            }
            var adj = new Adjacency();
            state.Graph[uid] = adj;
            return adj;
        }

        private static Edge CreateDockNeighbor(TsMapper mapper, TsNode from, TsNode to, string direction)
        {
            var distance = RoadGeometry.Distance(from.X, from.Z, to.X, to.Z);
            return new Edge
            {
                TargetUid = to.Uid,
                Distance = distance,
                Duration = CalculateDurationSeconds(GetSpeedMph(mapper, to, direction, "local_road"), distance),
                Direction = direction,
                DlcGuard = GetApproxDlcGuard(mapper, to)
            };
        }

        private static byte GetApproxDlcGuard(TsMapper mapper, TsNode node)
        {
            if (mapper.AllPrefabsByUid.TryGetValue(node.ForwardItemUid, out var fp)) return fp.DlcGuard;
            if (mapper.AllPrefabsByUid.TryGetValue(node.BackwardItemUid, out var bp)) return bp.DlcGuard;
            return 0;
        }

        /// <summary>
        /// Port of `toNeighbor`'s speed lookup: country comes from the *destination* node's
        /// country id on the opposite side of travel, with its truck speed limit for the given
        /// lane speed class (falling back to that country's own "local_road" limit, then to a
        /// flat 30). USA data is already in mph; every other country's is km/h and gets converted.
        /// </summary>
        private static double GetSpeedMph(TsMapper mapper, TsNode destNode, string direction, string speedClass)
        {
            var countryId = direction == "forward" ? destNode.BackwardCountryId : destNode.ForwardCountryId;
            var country = mapper.GetCountryById(countryId) ?? mapper.GetCountryById(1);

            double limit = FallbackSpeedMph;
            if (country != null)
            {
                if (country.TruckSpeedLimits.TryGetValue(speedClass, out var sl)) limit = sl.Limit;
                else if (country.TruckSpeedLimits.TryGetValue("local_road", out var local)) limit = local.Limit;
            }

            return limit * (mapper.IsEts2 ? KmhToMph : 1);
        }

        private static double CalculateDurationSeconds(double speedMph, double distanceMeters)
        {
            var metersPerSecond = speedMph * 0.44704;
            return distanceMeters / metersPerSecond;
        }

        // ------------------------------------------------------------------------- serviceAreas

        private class ServiceAreaEntry
        {
            public ulong ItemUid;
            public int ItemType;
            public readonly HashSet<string> Facilities = new HashSet<string>();
        }

        private static Dictionary<ulong, ServiceAreaEntry> BuildServiceAreas(TsMapper mapper)
        {
            const double maxLinkDistance = 250; // meters

            var byNode = new Dictionary<ulong, ServiceAreaEntry>();
            var candidates = mapper.OverlayManager.GetOverlays().Where(o => FacilityIcons.Contains(o.OverlayName));

            foreach (var overlay in candidates)
            {
                TsNode closest = null;
                var closestDistSq = double.MaxValue;
                foreach (var node in mapper.Nodes.Values)
                {
                    var dx = node.X - overlay.Position.X;
                    var dz = node.Z - overlay.Position.Y;
                    var distSq = dx * dx + dz * dz;
                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        closest = node;
                    }
                }

                if (closest == null || closestDistSq > maxLinkDistance * maxLinkDistance) continue;

                if (!byNode.TryGetValue(closest.Uid, out var entry))
                {
                    entry = new ServiceAreaEntry { ItemUid = overlay.ItemUid, ItemType = overlay.ItemType };
                    byNode[closest.Uid] = entry;
                }
                entry.Facilities.Add(overlay.OverlayName);
            }

            return byNode;
        }

        // -------------------------------------------------------------------------- writing

        private static void WriteGraphEntry(JsonTextWriter w, ulong nodeUid, Adjacency adj)
        {
            w.WriteStartArray();
            w.WriteValue(nodeUid.ToString("x"));

            w.WriteStartObject();
            w.WritePropertyName("forward");
            w.WriteStartArray();
            foreach (var e in adj.Forward) WriteEdge(w, e);
            w.WriteEndArray();

            w.WritePropertyName("backward");
            w.WriteStartArray();
            foreach (var e in adj.Backward) WriteEdge(w, e);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndArray();
        }

        private static void WriteEdge(JsonTextWriter w, Edge e)
        {
            w.WriteStartObject();

            w.WritePropertyName("nodeUid");
            w.WriteValue(e.TargetUid.ToString("x"));

            w.WritePropertyName("distance");
            w.WriteValue(e.Distance);

            if (e.Duration.HasValue)
            {
                w.WritePropertyName("duration");
                w.WriteValue(e.Duration.Value);
            }

            w.WritePropertyName("direction");
            w.WriteValue(e.Direction);

            if (e.IsOneLaneRoad)
            {
                w.WritePropertyName("isOneLaneRoad");
                w.WriteValue(true);
            }

            w.WritePropertyName("dlcGuard");
            w.WriteValue((int)e.DlcGuard);

            if (e.IsFerry)
            {
                w.WritePropertyName("isFerry");
                w.WriteValue(true);
            }

            w.WriteEndObject();
        }

        private static void WriteServiceAreaEntry(JsonTextWriter w, ulong nodeUid, ServiceAreaEntry entry)
        {
            w.WriteStartArray();
            w.WriteValue(nodeUid.ToString("x"));

            w.WriteStartObject();

            w.WritePropertyName("facilities");
            w.WriteStartArray();
            foreach (var f in entry.Facilities) w.WriteValue(f);
            w.WriteEndArray();

            w.WritePropertyName("itemUid");
            w.WriteValue(entry.ItemUid.ToString("x"));

            w.WritePropertyName("itemType");
            w.WriteValue(entry.ItemType);

            w.WritePropertyName("description");
            w.WriteValue("");

            w.WriteEndObject();
            w.WriteEndArray();
        }
    }
}
