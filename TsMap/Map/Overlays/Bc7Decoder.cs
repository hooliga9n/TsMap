using System;

namespace TsMap.Map.Overlays
{
    // ----------------------------------------------------------------------
    // BC7 (BPTC) texture block decoder.
    //
    // Some newer icon textures (used e.g. by large map mods) are compressed
    // with BC7 instead of the older BC1/BC2/BC3 formats ts-map already
    // supported. This file adds decode-only support for BC7 so those icons
    // can be rendered instead of failing with "Could not find dds format".
    //
    // Ported (decode path only - no encoding) from the open-source
    // BCnEncoder.NET project (MIT License):
    // https://github.com/Nominom/BCnEncoder.NET
    // ----------------------------------------------------------------------

    internal struct Bc7Color
    {
        public byte r, g, b, a;

        public Bc7Color(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        private static byte Clamp(int v) => (byte) (v < 0 ? 0 : v > 255 ? 255 : v);

        public static Bc7Color operator <<(Bc7Color c, int amount) =>
            new Bc7Color(Clamp(c.r << amount), Clamp(c.g << amount), Clamp(c.b << amount), Clamp(c.a << amount));

        public static Bc7Color operator >>(Bc7Color c, int amount) =>
            new Bc7Color(Clamp(c.r >> amount), Clamp(c.g >> amount), Clamp(c.b >> amount), Clamp(c.a >> amount));

        public static Bc7Color operator |(Bc7Color c, int value) =>
            new Bc7Color(Clamp(c.r | value), Clamp(c.g | value), Clamp(c.b | value), Clamp(c.a | value));
    }

    internal static class Bc7ByteHelper
    {
        public static byte Extract1(ulong source, int index) => (byte) ((source >> index) & 0b1UL);
        public static byte Extract2(ulong source, int index) => (byte) ((source >> index) & 0b11UL);
        public static byte Extract4(ulong source, int index) => (byte) ((source >> index) & 0b1111UL);
        public static byte Extract6(ulong source, int index) => (byte) ((source >> index) & 0b11_1111UL);

        public static ulong Extract(ulong source, int index, int bitCount)
        {
            unchecked
            {
                var mask = (0b1UL << bitCount) - 1;
                return (source >> index) & mask;
            }
        }

        public static ulong ExtractFrom128(ulong low, ulong high, int index, int bitCount)
        {
            if (index + bitCount <= 64)
            {
                return Extract(low, index, bitCount);
            }

            if (index >= 64)
            {
                return Extract(high, index - 64, bitCount);
            }

            var lowBitCount = 64 - index;
            var highBitCount = bitCount - lowBitCount;
            var value = Extract(low, index, lowBitCount);
            var hVal = Extract(high, 0, highBitCount);
            value |= hVal << lowBitCount;
            return value;
        }
    }

    internal enum Bc7BlockType : uint
    {
        Type0,
        Type1,
        Type2,
        Type3,
        Type4,
        Type5,
        Type6,
        Type7,
        Type8Reserved
    }

    internal struct Bc7Block
    {
        public ulong lowBits;
        public ulong highBits;

        private static readonly byte[] ColorInterpolationWeights2 = { 0, 21, 43, 64 };
        private static readonly byte[] ColorInterpolationWeights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly byte[] ColorInterpolationWeights4 =
            { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        private static readonly int[][] Subsets2PartitionTable = {
            new[] {0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1},
            new[] {0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1},
            new[] {0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1},
            new[] {0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1},
            new[] {0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1},
            new[] {0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1},
            new[] {0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1},
            new[] {0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1},
            new[] {0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0},
            new[] {0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0},
            new[] {0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0},
            new[] {0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0},
            new[] {0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1},
            new[] {0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0},
            new[] {0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0},
            new[] {0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0},
            new[] {0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0},
            new[] {0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0},
            new[] {0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0},
            new[] {0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0},
            new[] {0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1},
            new[] {0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0},
            new[] {0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0},
            new[] {0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0},
            new[] {0, 1, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0},
            new[] {0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1},
            new[] {0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 1},
            new[] {0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 0},
            new[] {0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 0, 0},
            new[] {0, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0},
            new[] {0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0},
            new[] {0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0},
            new[] {0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1},
            new[] {0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1},
            new[] {0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0},
            new[] {0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0},
            new[] {0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0},
            new[] {0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0},
            new[] {0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0},
            new[] {0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1},
            new[] {0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1},
            new[] {0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0},
            new[] {0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0},
            new[] {0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 1},
            new[] {0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1},
            new[] {0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1},
            new[] {0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1},
            new[] {0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0},
            new[] {0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0},
            new[] {0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1}
        };

        private static readonly int[][] Subsets3PartitionTable = {
            new[] {0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2},
            new[] {0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1},
            new[] {0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1},
            new[] {0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2},
            new[] {0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2},
            new[] {0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1},
            new[] {0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2},
            new[] {0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2},
            new[] {0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2},
            new[] {0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2},
            new[] {0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2},
            new[] {0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0},
            new[] {0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2},
            new[] {0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0},
            new[] {0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2},
            new[] {0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1},
            new[] {0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2},
            new[] {0, 0, 0, 1, 0, 0, 0, 1, 2, 2, 2, 1, 2, 2, 2, 1},
            new[] {0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2},
            new[] {0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0},
            new[] {0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0},
            new[] {0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2},
            new[] {0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0},
            new[] {0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1},
            new[] {0, 0, 2, 2, 1, 1, 0, 2, 1, 1, 0, 2, 0, 0, 2, 2},
            new[] {0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2},
            new[] {0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1},
            new[] {0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1},
            new[] {0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2},
            new[] {0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1},
            new[] {0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2},
            new[] {0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0},
            new[] {0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0},
            new[] {0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0},
            new[] {0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0},
            new[] {0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1},
            new[] {0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1},
            new[] {0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1},
            new[] {0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2},
            new[] {0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1},
            new[] {0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1},
            new[] {0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1},
            new[] {0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1},
            new[] {0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2},
            new[] {0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 1},
            new[] {0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2},
            new[] {0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2},
            new[] {0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2},
            new[] {0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2},
            new[] {0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2},
            new[] {0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2},
            new[] {0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2},
            new[] {0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2},
            new[] {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2},
            new[] {0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1},
            new[] {0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2},
            new[] {0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2},
            new[] {0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0},
        };

        private static readonly int[] Subsets2AnchorIndices = {
            15, 15, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 15, 15, 15, 15,
            15, 2, 8, 2, 2, 8, 8, 15,
            2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15,
            2, 8, 2, 2, 2, 15, 15, 6,
            6, 2, 6, 8, 15, 15, 2, 2,
            15, 15, 15, 15, 15, 2, 2, 15
        };

        private static readonly int[] Subsets3AnchorIndices2 = {
            3, 3, 15, 15, 8, 3, 15, 15,
            8, 8, 6, 6, 6, 5, 3, 3,
            3, 3, 8, 15, 3, 3, 6, 10,
            5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15,
            15, 3, 15, 5, 15, 15, 15, 15,
            3, 15, 5, 5, 5, 8, 5, 10,
            5, 10, 8, 13, 15, 12, 3, 3
        };

        private static readonly int[] Subsets3AnchorIndices3 = {
            15, 8, 8, 3, 15, 15, 3, 8,
            15, 15, 15, 15, 15, 15, 15, 8,
            15, 8, 15, 3, 15, 8, 15, 8,
            3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10,
            6, 15, 8, 15, 3, 6, 6, 8,
            15, 3, 15, 15, 15, 15, 15, 15,
            15, 15, 15, 15, 3, 15, 15, 8
        };

        public Bc7BlockType Type
        {
            get
            {
                for (var i = 0; i < 8; i++)
                {
                    var mask = (ulong) (1 << i);
                    if ((lowBits & mask) == mask) return (Bc7BlockType) i;
                }
                return Bc7BlockType.Type8Reserved;
            }
        }

        public int NumSubsets
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type0: return 3;
                    case Bc7BlockType.Type1: return 2;
                    case Bc7BlockType.Type2: return 3;
                    case Bc7BlockType.Type3: return 2;
                    case Bc7BlockType.Type7: return 2;
                    default: return 1;
                }
            }
        }

        public bool HasSubsets
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type0:
                    case Bc7BlockType.Type1:
                    case Bc7BlockType.Type2:
                    case Bc7BlockType.Type3:
                    case Bc7BlockType.Type7:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public int PartitionSetId
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type0: return Bc7ByteHelper.Extract4(lowBits, 1);
                    case Bc7BlockType.Type1: return Bc7ByteHelper.Extract6(lowBits, 2);
                    case Bc7BlockType.Type2: return Bc7ByteHelper.Extract6(lowBits, 3);
                    case Bc7BlockType.Type3: return Bc7ByteHelper.Extract6(lowBits, 4);
                    case Bc7BlockType.Type7: return Bc7ByteHelper.Extract6(lowBits, 8);
                    default: return -1;
                }
            }
        }

        public byte RotationBits
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type4: return Bc7ByteHelper.Extract2(lowBits, 5);
                    case Bc7BlockType.Type5: return Bc7ByteHelper.Extract2(lowBits, 6);
                    default: return 0;
                }
            }
        }

        public int ColorComponentPrecision
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type0: return 5;
                    case Bc7BlockType.Type1: return 7;
                    case Bc7BlockType.Type2: return 5;
                    case Bc7BlockType.Type3: return 8;
                    case Bc7BlockType.Type4: return 5;
                    case Bc7BlockType.Type5: return 7;
                    case Bc7BlockType.Type6: return 8;
                    case Bc7BlockType.Type7: return 6;
                    default: return 0;
                }
            }
        }

        public int AlphaComponentPrecision
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type4: return 6;
                    case Bc7BlockType.Type5: return 8;
                    case Bc7BlockType.Type6: return 8;
                    case Bc7BlockType.Type7: return 6;
                    default: return 0;
                }
            }
        }

        public bool HasRotationBits => Type == Bc7BlockType.Type4 || Type == Bc7BlockType.Type5;

        public bool HasPBits
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type0:
                    case Bc7BlockType.Type1:
                    case Bc7BlockType.Type3:
                    case Bc7BlockType.Type6:
                    case Bc7BlockType.Type7:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public bool HasAlpha
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type4:
                    case Bc7BlockType.Type5:
                    case Bc7BlockType.Type6:
                    case Bc7BlockType.Type7:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public int Type4IndexMode => Type == Bc7BlockType.Type4 ? Bc7ByteHelper.Extract1(lowBits, 7) : 0;

        public int ColorIndexBitCount
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type0: return 3;
                    case Bc7BlockType.Type1: return 3;
                    case Bc7BlockType.Type2: return 2;
                    case Bc7BlockType.Type3: return 2;
                    case Bc7BlockType.Type4: return Type4IndexMode == 0 ? 2 : 3;
                    case Bc7BlockType.Type5: return 2;
                    case Bc7BlockType.Type6: return 4;
                    case Bc7BlockType.Type7: return 2;
                    default: return 0;
                }
            }
        }

        public int AlphaIndexBitCount
        {
            get
            {
                switch (Type)
                {
                    case Bc7BlockType.Type4: return Type4IndexMode == 0 ? 3 : 2;
                    case Bc7BlockType.Type5: return 2;
                    case Bc7BlockType.Type6: return 4;
                    case Bc7BlockType.Type7: return 2;
                    default: return 0;
                }
            }
        }

        private void ExtractRawEndpoints(Bc7Color[] endpoints)
        {
            switch (Type)
            {
                case Bc7BlockType.Type0:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 5, 4);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 9, 4);
                    endpoints[2].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 13, 4);
                    endpoints[3].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 17, 4);
                    endpoints[4].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 21, 4);
                    endpoints[5].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 25, 4);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 29, 4);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 33, 4);
                    endpoints[2].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 37, 4);
                    endpoints[3].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 41, 4);
                    endpoints[4].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 45, 4);
                    endpoints[5].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 49, 4);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 53, 4);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 57, 4);
                    endpoints[2].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 61, 4);
                    endpoints[3].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 65, 4);
                    endpoints[4].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 69, 4);
                    endpoints[5].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 73, 4);
                    break;
                case Bc7BlockType.Type1:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 8, 6);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 14, 6);
                    endpoints[2].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 20, 6);
                    endpoints[3].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 26, 6);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 32, 6);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 38, 6);
                    endpoints[2].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 44, 6);
                    endpoints[3].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 50, 6);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 56, 6);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 62, 6);
                    endpoints[2].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 68, 6);
                    endpoints[3].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 74, 6);
                    break;
                case Bc7BlockType.Type2:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 9, 5);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 14, 5);
                    endpoints[2].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 19, 5);
                    endpoints[3].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 24, 5);
                    endpoints[4].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 29, 5);
                    endpoints[5].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 34, 5);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 39, 5);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 44, 5);
                    endpoints[2].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 49, 5);
                    endpoints[3].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 54, 5);
                    endpoints[4].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 59, 5);
                    endpoints[5].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 64, 5);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 69, 5);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 74, 5);
                    endpoints[2].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 79, 5);
                    endpoints[3].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 84, 5);
                    endpoints[4].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 89, 5);
                    endpoints[5].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 94, 5);
                    break;
                case Bc7BlockType.Type3:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 10, 7);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 17, 7);
                    endpoints[2].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 24, 7);
                    endpoints[3].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 31, 7);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 38, 7);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 45, 7);
                    endpoints[2].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 52, 7);
                    endpoints[3].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 59, 7);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 66, 7);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 73, 7);
                    endpoints[2].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 80, 7);
                    endpoints[3].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 87, 7);
                    break;
                case Bc7BlockType.Type4:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 8, 5);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 13, 5);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 18, 5);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 23, 5);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 28, 5);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 33, 5);

                    endpoints[0].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 38, 6);
                    endpoints[1].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 44, 6);
                    break;
                case Bc7BlockType.Type5:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 8, 7);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 15, 7);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 22, 7);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 29, 7);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 36, 7);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 43, 7);

                    endpoints[0].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 50, 8);
                    endpoints[1].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 58, 8);
                    break;
                case Bc7BlockType.Type6:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 7, 7);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 14, 7);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 21, 7);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 28, 7);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 35, 7);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 42, 7);

                    endpoints[0].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 49, 7);
                    endpoints[1].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 56, 7);
                    break;
                case Bc7BlockType.Type7:
                    endpoints[0].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 14, 5);
                    endpoints[1].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 19, 5);
                    endpoints[2].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 24, 5);
                    endpoints[3].r = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 29, 5);

                    endpoints[0].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 34, 5);
                    endpoints[1].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 39, 5);
                    endpoints[2].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 44, 5);
                    endpoints[3].g = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 49, 5);

                    endpoints[0].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 54, 5);
                    endpoints[1].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 59, 5);
                    endpoints[2].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 64, 5);
                    endpoints[3].b = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 69, 5);

                    endpoints[0].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 74, 5);
                    endpoints[1].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 79, 5);
                    endpoints[2].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 84, 5);
                    endpoints[3].a = (byte) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, 89, 5);
                    break;
            }
        }

        private byte[] ExtractPBitArray()
        {
            switch (Type)
            {
                case Bc7BlockType.Type0:
                    return new[]
                    {
                        Bc7ByteHelper.Extract1(highBits, 77 - 64), Bc7ByteHelper.Extract1(highBits, 78 - 64),
                        Bc7ByteHelper.Extract1(highBits, 79 - 64), Bc7ByteHelper.Extract1(highBits, 80 - 64),
                        Bc7ByteHelper.Extract1(highBits, 81 - 64), Bc7ByteHelper.Extract1(highBits, 82 - 64)
                    };
                case Bc7BlockType.Type1:
                    return new[]
                    {
                        Bc7ByteHelper.Extract1(highBits, 80 - 64), Bc7ByteHelper.Extract1(highBits, 81 - 64)
                    };
                case Bc7BlockType.Type3:
                    return new[]
                    {
                        Bc7ByteHelper.Extract1(highBits, 94 - 64), Bc7ByteHelper.Extract1(highBits, 95 - 64),
                        Bc7ByteHelper.Extract1(highBits, 96 - 64), Bc7ByteHelper.Extract1(highBits, 97 - 64)
                    };
                case Bc7BlockType.Type6:
                    return new[] { Bc7ByteHelper.Extract1(lowBits, 63), Bc7ByteHelper.Extract1(highBits, 0) };
                case Bc7BlockType.Type7:
                    return new[]
                    {
                        Bc7ByteHelper.Extract1(highBits, 94 - 64), Bc7ByteHelper.Extract1(highBits, 95 - 64),
                        Bc7ByteHelper.Extract1(highBits, 96 - 64), Bc7ByteHelper.Extract1(highBits, 97 - 64)
                    };
                default:
                    return Array.Empty<byte>();
            }
        }

        private void FinalizeEndpoints(Bc7Color[] endpoints)
        {
            if (HasPBits)
            {
                for (var i = 0; i < endpoints.Length; i++) endpoints[i] <<= 1;
                var pBits = ExtractPBitArray();

                if (Type == Bc7BlockType.Type1)
                {
                    endpoints[0] |= pBits[0];
                    endpoints[1] |= pBits[0];
                    endpoints[2] |= pBits[1];
                    endpoints[3] |= pBits[1];
                }
                else
                {
                    for (var i = 0; i < endpoints.Length; i++) endpoints[i] |= pBits[i];
                }
            }

            var colorPrecision = ColorComponentPrecision;
            var alphaPrecision = AlphaComponentPrecision;
            for (var i = 0; i < endpoints.Length; i++)
            {
                endpoints[i].r = (byte) (endpoints[i].r << (8 - colorPrecision));
                endpoints[i].g = (byte) (endpoints[i].g << (8 - colorPrecision));
                endpoints[i].b = (byte) (endpoints[i].b << (8 - colorPrecision));
                endpoints[i].a = (byte) (endpoints[i].a << (8 - alphaPrecision));

                endpoints[i].r = (byte) (endpoints[i].r | (endpoints[i].r >> colorPrecision));
                endpoints[i].g = (byte) (endpoints[i].g | (endpoints[i].g >> colorPrecision));
                endpoints[i].b = (byte) (endpoints[i].b | (endpoints[i].b >> colorPrecision));
                endpoints[i].a = (byte) (endpoints[i].a | (endpoints[i].a >> alphaPrecision));
            }

            if (!HasAlpha)
            {
                for (var i = 0; i < endpoints.Length; i++) endpoints[i].a = 255;
            }
        }

        public Bc7Color[] ExtractEndpoints()
        {
            var endpoints = new Bc7Color[NumSubsets * 2];
            ExtractRawEndpoints(endpoints);
            FinalizeEndpoints(endpoints);
            return endpoints;
        }

        private int GetPartitionIndex(int numSubsets, int partitionSetId, int i)
        {
            switch (numSubsets)
            {
                case 1: return 0;
                case 2: return Subsets2PartitionTable[partitionSetId][i];
                case 3: return Subsets3PartitionTable[partitionSetId][i];
                default: throw new ArgumentOutOfRangeException(nameof(numSubsets));
            }
        }

        private int GetIndexOffset(int numSubsets, int partitionIndex, int bitCount, int index)
        {
            if (index == 0) return 0;
            if (numSubsets == 1) return bitCount * index - 1;
            if (numSubsets == 2)
            {
                var anchorIndex = Subsets2AnchorIndices[partitionIndex];
                return index <= anchorIndex ? bitCount * index - 1 : bitCount * index - 2;
            }
            if (numSubsets == 3)
            {
                var anchor2Index = Subsets3AnchorIndices2[partitionIndex];
                var anchor3Index = Subsets3AnchorIndices3[partitionIndex];
                if (index <= anchor2Index && index <= anchor3Index) return bitCount * index - 1;
                if (index > anchor2Index && index > anchor3Index) return bitCount * index - 3;
                return bitCount * index - 2;
            }
            throw new ArgumentOutOfRangeException(nameof(numSubsets));
        }

        private int GetIndexBitCount(int numSubsets, int partitionIndex, int bitCount, int index)
        {
            if (index == 0) return bitCount - 1;
            if (numSubsets == 2)
            {
                if (index == Subsets2AnchorIndices[partitionIndex]) return bitCount - 1;
            }
            else if (numSubsets == 3)
            {
                if (index == Subsets3AnchorIndices2[partitionIndex]) return bitCount - 1;
                if (index == Subsets3AnchorIndices3[partitionIndex]) return bitCount - 1;
            }
            return bitCount;
        }

        private int GetIndexBegin(Bc7BlockType type, int bitCount, bool isAlpha)
        {
            switch (type)
            {
                case Bc7BlockType.Type0: return 83;
                case Bc7BlockType.Type1: return 82;
                case Bc7BlockType.Type2: return 99;
                case Bc7BlockType.Type3: return 98;
                case Bc7BlockType.Type4: return bitCount == 2 ? 50 : 81;
                case Bc7BlockType.Type5: return isAlpha ? 97 : 66;
                case Bc7BlockType.Type6: return 65;
                case Bc7BlockType.Type7: return 98;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private int GetAlphaIndex(Bc7BlockType type, int numSubsets, int partitionIndex, int bitCount, int index)
        {
            if (bitCount == 0) return 0;
            var indexOffset = GetIndexOffset(numSubsets, partitionIndex, bitCount, index);
            var indexBitCount = GetIndexBitCount(numSubsets, partitionIndex, bitCount, index);
            var indexBegin = GetIndexBegin(type, bitCount, true);
            return (int) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, indexBegin + indexOffset, indexBitCount);
        }

        private int GetColorIndex(Bc7BlockType type, int numSubsets, int partitionIndex, int bitCount, int index)
        {
            var indexOffset = GetIndexOffset(numSubsets, partitionIndex, bitCount, index);
            var indexBitCount = GetIndexBitCount(numSubsets, partitionIndex, bitCount, index);
            var indexBegin = GetIndexBegin(type, bitCount, false);
            return (int) Bc7ByteHelper.ExtractFrom128(lowBits, highBits, indexBegin + indexOffset, indexBitCount);
        }

        private static byte InterpolateByte(byte e0, byte e1, int index, int indexPrecision)
        {
            if (indexPrecision == 0) return e0;
            byte[] weights;
            switch (indexPrecision)
            {
                case 2: weights = ColorInterpolationWeights2; break;
                case 3: weights = ColorInterpolationWeights3; break;
                default: weights = ColorInterpolationWeights4; break;
            }
            return (byte) (((64 - weights[index]) * e0 + weights[index] * e1 + 32) >> 6);
        }

        private static Bc7Color InterpolateColor(Bc7Color s, Bc7Color e, int colorIndex, int alphaIndex,
            int colorBitCount, int alphaBitCount)
        {
            return new Bc7Color(
                InterpolateByte(s.r, e.r, colorIndex, colorBitCount),
                InterpolateByte(s.g, e.g, colorIndex, colorBitCount),
                InterpolateByte(s.b, e.b, colorIndex, colorBitCount),
                InterpolateByte(s.a, e.a, alphaIndex, alphaBitCount));
        }

        /// <summary>
        /// 00 - no swapping, 01 - swap A/R, 10 - swap A/G, 11 - swap A/B
        /// </summary>
        private static Bc7Color SwapChannels(Bc7Color c, int rotation)
        {
            switch (rotation)
            {
                case 0b01: return new Bc7Color(c.a, c.g, c.b, c.r);
                case 0b10: return new Bc7Color(c.r, c.a, c.b, c.g);
                case 0b11: return new Bc7Color(c.r, c.g, c.a, c.b);
                default: return c;
            }
        }

        /// <summary>
        /// Decodes this 16-byte BC7 block into 16 RGBA colors, in row-major order
        /// (index = x + y * 4, matching the layout used elsewhere in ts-map).
        /// </summary>
        public Bc7Color[] Decode()
        {
            var output = new Bc7Color[16];
            var type = Type;

            if (type == Bc7BlockType.Type8Reserved)
            {
                for (var i = 0; i < 16; i++) output[i] = new Bc7Color(255, 0, 255, 255);
                return output;
            }

            var numSubsets = 1;
            var partitionIndex = 0;
            if (HasSubsets)
            {
                numSubsets = NumSubsets;
                partitionIndex = PartitionSetId;
            }

            var hasRotationBits = HasRotationBits;
            var rotation = RotationBits;
            var endpoints = ExtractEndpoints();

            var colorBitCount = ColorIndexBitCount;
            var alphaBitCount = AlphaIndexBitCount;

            for (var i = 0; i < 16; i++)
            {
                var subsetIndex = GetPartitionIndex(numSubsets, partitionIndex, i);
                var endPointStart = endpoints[2 * subsetIndex];
                var endPointEnd = endpoints[2 * subsetIndex + 1];

                var alphaIndex = GetAlphaIndex(type, numSubsets, partitionIndex, alphaBitCount, i);
                var colorIndex = GetColorIndex(type, numSubsets, partitionIndex, colorBitCount, i);

                var outputColor = InterpolateColor(endPointStart, endPointEnd, colorIndex, alphaIndex,
                    colorBitCount, alphaBitCount);

                if (hasRotationBits) outputColor = SwapChannels(outputColor, rotation);

                output[i] = outputColor;
            }

            return output;
        }
    }
}
