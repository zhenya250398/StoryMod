using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Rope hoist: hangs a rope straight down and moves whatever is on the end of it,
    /// one cell per stroke.
    ///
    /// Power still comes in horizontally through the usual MPConsumer, so this sidesteps
    /// the fact that the vanilla mechanical behaviour only understands the four compass
    /// directions — the axle is horizontal, only the load moves vertically.
    ///
    /// One block at a time for now. Lifting a whole platform needs the block-to-block
    /// glue that does not exist yet.
    /// </summary>
    public class BERopeHoist : BEMoverBase
    {
        /// <summary>How far down the rope reaches looking for its load.</summary>
        public const int MaxRopeLength = 24;

        /// <summary>Cells kept clear directly under the hoist, so the load never jams into it.</summary>
        const int HeadroomCells = 1;

        protected override string StrokeNoun => Lowering ? "lower" : "lift";

        /// <summary>Reversed rotation pays the rope out instead of hauling it in.</summary>
        public bool Lowering => Reversed;

        /// <summary>Which way the load travels this stroke.</summary>
        public BlockFacing TravelFacing => Lowering ? BlockFacing.DOWN : BlockFacing.UP;

        protected override bool TryMove()
        {
            IBlockAccessor ba = Api.World.BlockAccessor;

            BlockPos load = FindLoad(ba);
            if (load == null) return false;

            // Never haul the load into the hoist itself.
            BlockPos target = load.AddCopy(TravelFacing);
            if (!Lowering && target.InternalY >= Pos.InternalY - (HeadroomCells - 1)) return false;

            // Glue turns the single hanging block into a platform. StartMove checks that
            // every cell of the group has somewhere to land.
            List<BlockPos> group = ExpandThroughGlue(new List<BlockPos> { load });
            if (group == null) return false;

            return StartMove(group, TravelFacing);
        }

        /// <summary>
        /// Follows the rope down from the hoist and returns the first block hanging on it,
        /// or null if the rope runs out of length or leaves loaded chunks first.
        /// </summary>
        BlockPos FindLoad(IBlockAccessor ba)
        {
            BlockPos cur = Pos.DownCopy();

            for (int i = 0; i < MaxRopeLength; i++)
            {
                if (ba.GetChunkAtBlockPos(cur) == null) return null;

                Block block = ba.GetBlock(cur);
                if (!IsFree(block))
                {
                    return IsMovable(block) ? cur.Copy() : null;
                }

                cur = cur.DownCopy();
            }

            return null;
        }

        public override void GetBlockInfo(IPlayer forPlayer, System.Text.StringBuilder sb)
        {
            sb.AppendLine(Lowering ? "Lowering" : "Lifting");
            base.GetBlockInfo(forPlayer, sb);

            BlockPos load = FindLoad(Api.World.BlockAccessor);
            sb.AppendLine(load == null
                ? "Rope is empty"
                : string.Format("Load {0} blocks down", Pos.InternalY - load.InternalY));
        }
    }
}
