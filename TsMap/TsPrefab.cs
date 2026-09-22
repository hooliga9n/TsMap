// Portions of the .ppd navigation-curve parsing below (TsPrefabNavCurve, the InputLines/
// OutputLines fields on TsPrefabNode, and their byte layout) are ported from TruckLib.Models
// (https://github.com/sk-zk/TruckLib.Models), licensed under the GNU General Public License
// v2.0. Per that license, this file (and anything statically combined with it) is licensed
// under GPL v2 as well - see /LICENSE-GPL-2.0 (or the upstream project) for the full text.
using System;
using System.Collections.Generic;
using TsMap.FileSystem;
using TsMap.Helpers;
using TsMap.Helpers.Logger;

namespace TsMap
{
    public struct TsPrefabNode
    {
        public float X;
        public float Z;
        public float RotX;
        public float RotZ;
        public int LaneCount;

        /// <summary>Indices into <see cref="TsPrefab.NavCurves"/> that START at this node (-1 = unused slot). Ported from TruckLib.Models' ControlNode.InputLines.</summary>
        public int[] InputLines;

        /// <summary>Indices into <see cref="TsPrefab.NavCurves"/> that END at this node (-1 = unused slot). Ported from TruckLib.Models' ControlNode.OutputLines.</summary>
        public int[] OutputLines;
    }

    /// <summary>
    /// A navigation curve (AI traffic path) inside a prefab, as used for real lane-level routing
    /// through an intersection. Ported from TruckLib.Models' NavCurve (Ppd/NavCurve.cs); only the
    /// fields GraphExporter actually needs are read (Length, NextLines, and whether this curve has
    /// any PreviousLines, i.e. whether it's a "starting" curve for calculateNodeConnections-style
    /// traversal).
    /// </summary>
    public struct TsPrefabNavCurve
    {
        public float Length;
        public List<int> NextLines;
        public bool HasPreviousLines;
    }

    public struct TsMapPoint
    {
        public float X;
        public float Z;
        public int LaneOffset;
        public int LaneCount;
        public bool Hidden;
        public byte PrefabColorFlags;
        public int NeighbourCount;
        public List<int> Neighbours;
        public sbyte ControlNodeIndex;
    }

    public class TsSpawnPoint
    {
        public float X;
        public float Z;
        public TsSpawnPointType Type;
        public uint Unk; /* from version 24 */
    }

    public class TsTriggerPoint
    {
        public uint TriggerId;
        public ulong TriggerActionToken;
        public float X;
        public float Z;
    }

    public class TsPrefab
    {
        private const int NodeBlockSize = 0x68;
        private const int MapPointBlockSize = 0x30;
        private const int SpawnPointBlockSize = 0x20;
        private const int SpawnPointV24BlockSize = 0x24;
        private const int TriggerPointBlockSize = 0x30;

        /// <summary>Size of a NavCurve struct for descriptor versions 0x16-0x19 (has NavNodeIndex). Ported from TruckLib.Models' NavCurve.Deserialize16to19.</summary>
        private const int NavCurveBlockSize = 0x84;
        /// <summary>Size of a NavCurve struct for descriptor version 0x15 (no NavNodeIndex field). Ported from TruckLib.Models' NavCurve.Deserialize15.</summary>
        private const int NavCurveV15BlockSize = 0x80;

        public string FilePath { get; }
        public ulong Token { get; }
        public string Category { get; }

        private byte[] _stream;

        public bool ValidRoad { get; private set; }

        public List<TsPrefabNode> PrefabNodes { get; private set; }
        public List<TsSpawnPoint> SpawnPoints { get; private set; }
        public List<TsMapPoint> MapPoints { get; private set; }
        public List<TsTriggerPoint> TriggerPoints { get; private set; }

        /// <summary>Navigation curves (AI traffic paths) - see <see cref="TsPrefabNavCurve"/>. Empty when the descriptor has none (ValidRoad == false).</summary>
        public List<TsPrefabNavCurve> NavCurves { get; private set; }

        public TsPrefab(string filePath, ulong token, string category)
        {
            FilePath = filePath;
            Token = token;
            Category = category;

            var file = UberFileSystem.Instance.GetFile(FilePath);

            if (file == null) return;

            _stream = file.Entry.Read();

            Parse();
        }

        private void Parse()
        {
            PrefabNodes = new List<TsPrefabNode>();
            SpawnPoints = new List<TsSpawnPoint>();
            MapPoints = new List<TsMapPoint>();
            TriggerPoints = new List<TsTriggerPoint>();
            NavCurves = new List<TsPrefabNavCurve>();

            var fileOffset = 0x0;

            var version = MemoryHelper.ReadInt32(_stream, fileOffset);

            if (version < 0x15)
            {
                Logger.Instance.Error($"{FilePath} file version ({version}) too low, min. is {0x15}");
                return;
            }

            var nodeCount = BitConverter.ToInt32(_stream, fileOffset += 0x04);
            var navCurveCount = BitConverter.ToInt32(_stream, fileOffset += 0x04);
            ValidRoad = navCurveCount != 0;
            var spawnPointCount = BitConverter.ToInt32(_stream, fileOffset += 0x0C);
            var mapPointCount = BitConverter.ToInt32(_stream, fileOffset += 0x0C);
            var triggerPointCount = BitConverter.ToInt32(_stream, fileOffset += 0x04);

            if (version > 0x15) fileOffset += 0x04; // http://modding.scssoft.com/wiki/Games/ETS2/Modding_guides/1.30#Prefabs

            // Offset table (ported against TruckLib.Models' PrefabDescriptor.Serialize field
            // order: nodes, navCurves, signs, semaphores, spawnPoints, ..., mapPoints,
            // triggerPoints, ...). Only navCurveOffset is new here; the rest is unchanged from
            // before, with spawnPointOffset's jump shortened by 0x04 to account for it.
            var nodeOffset = MemoryHelper.ReadInt32(_stream, fileOffset += 0x08);
            var navCurveOffset = MemoryHelper.ReadInt32(_stream, fileOffset += 0x04);
            var spawnPointOffset = MemoryHelper.ReadInt32(_stream, fileOffset += 0x0C);
            var mapPointOffset = MemoryHelper.ReadInt32(_stream, fileOffset += 0x10);
            var triggerPointOffset = MemoryHelper.ReadInt32(_stream, fileOffset += 0x04);

            for (var i = 0; i < nodeCount; i++)
            {
                var nodeBaseOffset = nodeOffset + (i * NodeBlockSize);
                var node = new TsPrefabNode
                {
                    X = MemoryHelper.ReadSingle(_stream, nodeBaseOffset + 0x10),
                    Z = MemoryHelper.ReadSingle(_stream, nodeBaseOffset + 0x18),
                    RotX = MemoryHelper.ReadSingle(_stream, nodeBaseOffset + 0x1C),
                    RotZ = MemoryHelper.ReadSingle(_stream, nodeBaseOffset + 0x24),
                    InputLines = new int[8],
                    OutputLines = new int[8]
                };

                int laneCount = 0;
                var nodeFileOffset = nodeBaseOffset + 0x24;
                for (var j = 0; j < 8; j++)
                {
                    var v = MemoryHelper.ReadInt32(_stream, nodeFileOffset += 0x04);
                    node.InputLines[j] = v;
                    if (v != -1) laneCount++;
                }

                for (var j = 0; j < 8; j++)
                {
                    var v = MemoryHelper.ReadInt32(_stream, nodeFileOffset += 0x04);
                    node.OutputLines[j] = v;
                    if (v != -1) laneCount++;
                }
                node.LaneCount = laneCount;

                PrefabNodes.Add(node);
            }

            var navCurveBlockSize = version >= 0x16 ? NavCurveBlockSize : NavCurveV15BlockSize;
            for (var i = 0; i < navCurveCount; i++)
            {
                var navCurveBaseOffset = navCurveOffset + (i * navCurveBlockSize);

                // Byte offsets below match NavCurve.Deserialize16to19 field order: Name(8, token),
                // flags(4), LeadsToNodes(4), StartPosition(12), EndPosition(12),
                // StartRotation(16, quaternion), EndRotation(16), Length(4) @0x48,
                // nextLines[4](16) @0x4C, previousLines[4](16) @0x5C, nextUsed(4) @0x6C,
                // previousUsed(4) @0x70, ...
                var length = MemoryHelper.ReadSingle(_stream, navCurveBaseOffset + 0x48);
                var nextUsed = MemoryHelper.ReadUInt32(_stream, navCurveBaseOffset + 0x6C);
                var previousUsed = MemoryHelper.ReadUInt32(_stream, navCurveBaseOffset + 0x70);

                var nextLines = new List<int>((int)nextUsed);
                for (var j = 0; j < nextUsed && j < 4; j++)
                {
                    nextLines.Add(MemoryHelper.ReadInt32(_stream, navCurveBaseOffset + 0x4C + j * 0x04));
                }

                NavCurves.Add(new TsPrefabNavCurve
                {
                    Length = length,
                    NextLines = nextLines,
                    HasPreviousLines = previousUsed > 0
                });
            }

            var spawnPointBlockSize = version >= 24 ? SpawnPointV24BlockSize : SpawnPointBlockSize;

            for (var i = 0; i < spawnPointCount; i++)
            {
                var spawnPointBaseOffset = spawnPointOffset + (i * spawnPointBlockSize);
                var spawnPoint = new TsSpawnPoint
                {
                    X = MemoryHelper.ReadSingle(_stream, spawnPointBaseOffset),
                    Z = MemoryHelper.ReadSingle(_stream, spawnPointBaseOffset + 0x08),
                    Type = (TsSpawnPointType)MemoryHelper.ReadUInt32(_stream, spawnPointBaseOffset + 0x1C)
                };
                SpawnPoints.Add(spawnPoint);
                // Log.Msg($"Spawn point of type: {spawnPoint.Type} in {_filePath}");
            }

            for (var i = 0; i < mapPointCount; i++)
            {
                var mapPointBaseOffset = mapPointOffset + (i * MapPointBlockSize);
                var roadLookFlags = MemoryHelper.ReadUint8(_stream, mapPointBaseOffset + 0x01);
                var laneTypeFlags = (byte) (roadLookFlags & 0x0F);
                var laneOffsetFlags = (byte)(roadLookFlags >> 4);
                var controlNodeIndexFlags = MemoryHelper.ReadInt8(_stream, mapPointBaseOffset + 0x04);
                int laneOffset;
                switch (laneOffsetFlags)
                {
                    case 1: laneOffset = 1; break;
                    case 2: laneOffset = 2; break;
                    case 3: laneOffset = 5; break;
                    case 4: laneOffset = 10; break;
                    case 5: laneOffset = 15; break;
                    case 6: laneOffset = 20; break;
                    case 7: laneOffset = 25; break;
                    default: laneOffset = 0; break;

                }
                int laneCount;
                switch (laneTypeFlags) // TODO: Change these (not really used atm)
                {
                    case 0: laneCount = 1; break;
                    case 1: laneCount = 2; break;
                    case 2: laneCount = 4; break;
                    case 3: laneCount = 6; break;
                    case 4: laneCount = 8; break;
                    case 5: laneCount = 5; break;
                    case 6: laneCount = 7; break;
                    case 8: laneCount = 3; break;
                    case 13: laneCount = -1; break;
                    case 14: laneCount = -2; break; // auto
                    default:
                        laneCount = 1;
                        // Log.Msg($"Unknown LaneType: {laneTypeFlags}");
                        break;
                }
                sbyte controlNodeIndex = -1;
                switch (controlNodeIndexFlags)
                {
                    case 1: controlNodeIndex = 0; break;
                    case 2: controlNodeIndex = 1; break;
                    case 4: controlNodeIndex = 2; break;
                    case 8: controlNodeIndex = 3; break;
                    case 16: controlNodeIndex = 4; break;
                    case 32: controlNodeIndex = 5; break;
                }
                var prefabColorFlags = MemoryHelper.ReadUint8(_stream, mapPointBaseOffset + 0x02);

                var navFlags = MemoryHelper.ReadUint8(_stream, mapPointBaseOffset + 0x05);
                var hidden = (navFlags & 0x02) != 0; // Map Point is Control Node

                var point = new TsMapPoint
                {
                    LaneCount = laneCount,
                    LaneOffset = laneOffset,
                    Hidden = hidden,
                    PrefabColorFlags = prefabColorFlags,
                    X = MemoryHelper.ReadSingle(_stream, mapPointBaseOffset + 0x08),
                    Z = MemoryHelper.ReadSingle(_stream, mapPointBaseOffset + 0x10),
                    Neighbours = new List<int>(),
                    NeighbourCount = MemoryHelper.ReadInt32(_stream, mapPointBaseOffset + 0x14 + (0x04 * 6)),
                    ControlNodeIndex = controlNodeIndex
                };

                for (var x = 0; x < point.NeighbourCount; x++)
                {
                    point.Neighbours.Add(MemoryHelper.ReadInt32(_stream, mapPointBaseOffset + 0x14 + (x * 0x04)));
                }

                MapPoints.Add(point);
            }

            for (var i = 0; i < triggerPointCount; i++)
            {
                var triggerPointBaseOffset = triggerPointOffset + (i * TriggerPointBlockSize);
                var triggerPoint = new TsTriggerPoint
                {
                    TriggerId = MemoryHelper.ReadUInt32(_stream, triggerPointBaseOffset),
                    TriggerActionToken = MemoryHelper.ReadUInt64(_stream, triggerPointBaseOffset + 0x04),
                    X = MemoryHelper.ReadSingle(_stream, triggerPointBaseOffset + 0x1C),
                    Z = MemoryHelper.ReadSingle(_stream, triggerPointBaseOffset + 0x24),
                };
                TriggerPoints.Add(triggerPoint);
            }

            _stream = null;

        }
    }
}
