using System;
using TsMap.Helpers;

namespace TsMap
{
    public class TsNode
    {
        public ulong Uid { get; }

        /// <summary>Game X (horizontal).</summary>
        public float X { get; }

        /// <summary>Game Y (elevation). Exported as "z" in nodes.json.</summary>
        public float Y { get; }

        /// <summary>Game Z (horizontal). Exported as "y" in nodes.json.</summary>
        public float Z { get; }

        public float Rotation { get; }

        /// <summary>
        /// The four raw rotation floats as stored in the .base file, in file order.
        /// Exported verbatim as "rotationQuat".
        /// </summary>
        public float RotationQuat0 { get; }
        public float RotationQuat1 { get; }
        public float RotationQuat2 { get; }
        public float RotationQuat3 { get; }

        public ulong BackwardItemUid { get; }
        public ulong ForwardItemUid { get; }

        /// <summary>Raw node flags (u32 at +0x34).</summary>
        public uint Flags { get; }

        // NOTE: the exact bit layout of the country ids inside the node flags is not
        // officially documented. If the exported forward/backwardCountryId values look
        // wrong for your game version, adjust the two shifts/masks below (or just
        // return 0 from both - nothing else in TsMap depends on them).
        public byte ForwardCountryId => (byte)((Flags >> 8) & 0xFF);
        public byte BackwardCountryId => (byte)((Flags >> 16) & 0xFF);

        public TsNode(TsSector sector, int fileOffset)
        {
            var b = fileOffset;

            Uid = MemoryHelper.ReadUInt64(sector.Stream, b);

            X = MemoryHelper.ReadInt32(sector.Stream, b + 0x08) / 256f;
            Y = MemoryHelper.ReadInt32(sector.Stream, b + 0x0C) / 256f;
            Z = MemoryHelper.ReadInt32(sector.Stream, b + 0x10) / 256f;

            RotationQuat0 = MemoryHelper.ReadSingle(sector.Stream, b + 0x14);
            RotationQuat1 = MemoryHelper.ReadSingle(sector.Stream, b + 0x18);
            RotationQuat2 = MemoryHelper.ReadSingle(sector.Stream, b + 0x1C);
            RotationQuat3 = MemoryHelper.ReadSingle(sector.Stream, b + 0x20);

            // If forward/backward come out swapped for your map version, swap these two lines.
            BackwardItemUid = MemoryHelper.ReadUInt64(sector.Stream, b + 0x24);
            ForwardItemUid = MemoryHelper.ReadUInt64(sector.Stream, b + 0x2C);

            Flags = MemoryHelper.ReadUInt32(sector.Stream, b + 0x34);

            // Unchanged from the original implementation: rX = quat[0], rZ = quat[2].
            var rot = Math.PI - Math.Atan2(RotationQuat2, RotationQuat0);
            Rotation = (float)(rot % Math.PI * 2);
        }
    }
}
