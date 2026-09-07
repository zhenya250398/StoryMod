using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Rope hoist: hangs a rope straight down and moves whatever is on the end of it,
    /// continuously, for as long as the shaft turns.
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

        /// <summary>Which way the load travels on this run.</summary>
        public BlockFacing TravelFacing => Lowering ? BlockFacing.DOWN : BlockFacing.UP;

        /// <summary>
        /// Cells this run may still travel, worked out once when it starts: up until the
        /// load reaches the hoist, down until it reaches the end of the rope.
        ///
        /// The rope needs saying out loud now. It used to be enforced by accident — every
        /// stroke re-ran FindLoad, which only looks MaxRopeLength down, so a load could
        /// never get further from the hoist than that. A run is decided once, so a
        /// descent that is never re-checked simply carried on into the ground.
        /// </summary>
        int runSteps = int.MaxValue;

        protected override int MaxRunSteps => runSteps;

        protected override bool TryStartRun(float dt)
        {
            IBlockAccessor ba = Api.World.BlockAccessor;

            BlockPos load = FindLoad(ba);
            if (load == null) return false;

            // Glue turns the single hanging block into a platform. The base class checks
            // that every cell of the group has somewhere to go, step by step.
            List<BlockPos> group = ExpandThroughGlue(new List<BlockPos> { load });
            if (group == null) return false;

            runSteps = Lowering ? RopeLeft(group) : Headroom(group);
            if (runSteps < 1) return false;

            return StartMove(group, TravelFacing);
        }

        /// <summary>
        /// How many cells the load can rise before its top reaches the hoist. Measured
        /// from the highest cell of the group, so a tall platform stops in time.
        /// </summary>
        int Headroom(List<BlockPos> group)
        {
            int top = int.MinValue;
            foreach (BlockPos cell in group) top = System.Math.Max(top, cell.InternalY);

            return Pos.InternalY - HeadroomCells - top;
        }

        /// <summary>
        /// How many cells of rope are left to pay out. Measured to the top of the load,
        /// the end the rope is actually tied to, and against the same reach FindLoad uses
        /// to look for one — so the hoist can always find again what it just let down.
        /// </summary>
        int RopeLeft(List<BlockPos> group)
        {
            int top = int.MinValue;
            foreach (BlockPos cell in group) top = System.Math.Max(top, cell.InternalY);

            return top - (Pos.InternalY - MaxRopeLength);
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
