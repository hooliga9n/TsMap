using System.Collections.Generic;
using System.IO;
using TsMap.Common;
using TsMap.Helpers;
using TsMap.Helpers.Logger;
using TsMap.Map.Overlays;

namespace TsMap.TsItem
{
    public class TsFerryItem : TsItem
    {
        public ulong FerryPortId { get; private set; }

        /// <summary>Uid of the prefab this ferry/train port is attached to (m_prefab_uid in the base format).</summary>
        public ulong PrefabUid { get; private set; }

        public bool Train { get; private set; }

        public TsFerryItem(TsSector sector, int startOffset) : base(sector, startOffset)
        {
            Valid = true;
            TsFerryItem825(startOffset);
        }

        public void TsFerryItem825(int startOffset)
        {
            var fileOffset = startOffset + 0x34; // Set position at start of flags

            Train = MemoryHelper.ReadUint8(Sector.Stream, fileOffset) != 0;

            FerryPortId = MemoryHelper.ReadUInt64(Sector.Stream, fileOffset += 0x05);

            PrefabUid = MemoryHelper.ReadUInt64(Sector.Stream, fileOffset += 0x08); // skip FerryPortId, read prefab_uid

            Nodes = new List<ulong>(1)
            {
                MemoryHelper.ReadUInt64(Sector.Stream, fileOffset += 0x08) // skip prefab_uid, read node_uid
            };
            Sector.Mapper.AddFerryPortLocation(FerryPortId, X, Z);
            fileOffset += 0x08 + 0x0C; // 0x08(node_uid) + 0x0C(unloadoffset)
            BlockSize = fileOffset - startOffset;
        }

        internal override void Update()
        {
            var node = Sector.Mapper.GetNodeByUid(Nodes[0]);

            if (node == null)
            {
                Logger.Instance.Error(
                    $"Could not find node ({Nodes[0]:X}) for item uid: 0x{Uid:X}, " +
                    $"in {Path.GetFileName(Sector.FilePath)} from '{Sector.GetUberFile().Entry.GetArchiveFile().GetPath()}'");
                return;
            }

            var overlayName = Train ? "train_ico" : "port_overlay";
            if (!Sector.Mapper.OverlayManager.AddOverlay(overlayName, OverlayType.Road, node.X, node.Z, Train ? "Train" : "Ferry", DlcGuard))
            {
                Logger.Instance.Error(
                    $"Could not find Overlay: '{overlayName}'({ScsToken.StringToToken(overlayName):X}), item uid: 0x{Uid:X}, " +
                    $"in {Path.GetFileName(Sector.FilePath)} from '{Sector.GetUberFile().Entry.GetArchiveFile().GetPath()}'");
            }
        }
    }
}
