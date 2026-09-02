using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Piston: moves the run of blocks in front of it one cell per stroke, away or back.
    ///
    /// The run does not teleport — <see cref="BEMoverBase"/> lifts it into an
    /// <see cref="EntityMovingBlocks"/> which flies it across and puts it back down.
    /// </summary>
    public class BEPiston : BEMoverBase
    {
        /// <summary>Longest run of blocks one stroke may shift. Refuses beyond this.</summary>
        public const int MaxPushedBlocks = 12;

        protected override string StrokeNoun => Reversed ? "pull" : "push";

        BlockFacing PushFacing => (Block as BlockPiston)?.PushFacing ?? BlockFacing.NORTH;

        protected override bool TryMove()
        {
            // Which way the shaft turns decides whether we shove or drag.
            return Reversed ? TryPull() : TryPush();
        }

        bool TryPush()
        {
            BlockFacing facing = PushFacing;
            List<BlockPos> chain = CollectPushChain(Api.World.BlockAccessor, facing);
            if (chain == null) return false;

            // Anything glued to the chain comes along, so a piston can shove a structure
            // and not just the line of blocks directly ahead of it.
            List<BlockPos> group = ExpandThroughGlue(chain);
            if (group == null) return false;

            return StartMove(group, facing);
        }

        bool TryPull()
        {
            BlockFacing facing = PushFacing;
            List<BlockPos> chain = CollectPullChain(Api.World.BlockAccessor, facing);
            if (chain == null) return false;

            List<BlockPos> group = ExpandThroughGlue(chain);
            if (group == null) return false;

            return StartMove(group, facing.Opposite);
        }

        /// <summary>
        /// Walks forward from the piston collecting the contiguous run of blocks to move,
        /// stopping at the first free cell that the run will be shifted into.
        /// Returns null when the push is not legal at all: nothing in front, an immovable
        /// block in the way, the run longer than <see cref="MaxPushedBlocks"/>, or the
        /// path leaving loaded chunks.
        /// </summary>
        List<BlockPos> CollectPushChain(IBlockAccessor ba, BlockFacing facing)
        {
            List<BlockPos> chain = new List<BlockPos>();
            BlockPos cur = Pos.AddCopy(facing);

            while (true)
            {
                // Never push into terrain that is not loaded — GetBlock would report air
                // there and the write would be lost when the chunk loads for real.
                if (ba.GetChunkAtBlockPos(cur) == null) return null;

                Block block = ba.GetBlock(cur);
                if (IsFree(block)) break;                  // found the landing cell
                if (!IsMovable(block)) return null;        // bedrock and friends
                if (chain.Count >= MaxPushedBlocks) return null;

                chain.Add(cur.Copy());
                cur = cur.AddCopy(facing);
            }

            return chain.Count == 0 ? null : chain;
        }

        /// <summary>
        /// Finds the single block a pull would grab: the one sitting just beyond the free
        /// cell in front of the piston.
        ///
        /// Deliberately no reaching across a wider gap, and deliberately just one block —
        /// pushing shoves a whole contiguous run, pulling takes hold of one thing. Blocks
        /// merely resting against each other are not attached; glue is what makes a group,
        /// and ExpandThroughGlue adds it afterwards.
        /// </summary>
        List<BlockPos> CollectPullChain(IBlockAccessor ba, BlockFacing facing)
        {
            BlockPos landing = Pos.AddCopy(facing);
            if (ba.GetChunkAtBlockPos(landing) == null) return null;
            if (!IsFree(ba.GetBlock(landing))) return null;    // nothing to pull it into

            BlockPos target = landing.AddCopy(facing);
            if (ba.GetChunkAtBlockPos(target) == null) return null;

            Block block = ba.GetBlock(target);
            if (IsFree(block)) return null;                    // nothing there to grab
            if (!IsMovable(block)) return null;

            return new List<BlockPos> { target };
        }

    }
}
