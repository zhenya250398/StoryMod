using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Turntable: turns whatever is glued on top of it a quarter turn per stroke, about
    /// its own vertical axis.
    ///
    /// A quarter turn is the smallest step because it is the smallest angle that leaves
    /// every block back on the grid. Anything finer would have no resting position.
    ///
    /// Unlike the piston and the hoist this moves each block somewhere different, so it
    /// cannot use the shared carrier: that flies a snapshot along one offset. For now the
    /// turn is instant; animating it needs the carrier to learn about rotation.
    /// </summary>
    public class BETurntable : BEMoverBase
    {
        /// <summary>Largest structure one turntable will swing.</summary>
        public const int MaxTurnedBlocks = 64;

        protected override string StrokeNoun => "turn";

        /// <summary>Which way a stroke turns, as an angle vanilla understands.</summary>
        int TurnAngle => Reversed ? 270 : 90;

        protected override bool TryMove()
        {
            IBlockAccessor ba = Api.World.BlockAccessor;

            List<BlockPos> group = CollectLoad(ba);
            if (group == null) return false;

            int angle = TurnAngle;
            if (!CanLand(ba, group, angle)) return false;

            BlockSnapshot snapshot = BlockSnapshot.Capture(ba, group, Pos);

            GlueRegistry glue = Glue;
            if (glue != null)
            {
                snapshot.Glued = new bool[group.Count];
                for (int i = 0; i < group.Count; i++)
                {
                    snapshot.Glued[i] = glue.IsGlued(group[i]);
                    if (snapshot.Glued[i]) glue.Remove(group[i]);
                }
            }

            snapshot.ClearFromWorld(ba, Pos);
            snapshot.RestoreToWorld(Api.World, Pos, angle);
            RestoreGlue(glue, snapshot, angle);

            return true;
        }

        /// <summary>
        /// What sits on the turntable: the block directly above, plus everything glued to
        /// it. Only glue holds a structure together here, the same rule the other machines
        /// use — resting on something is not being attached to it.
        /// </summary>
        List<BlockPos> CollectLoad(IBlockAccessor ba)
        {
            BlockPos seat = Pos.UpCopy();
            if (ba.GetChunkAtBlockPos(seat) == null) return null;

            Block block = ba.GetBlock(seat);
            if (IsFree(block) || !IsMovable(block)) return null;

            List<BlockPos> group = ExpandThroughGlue(new List<BlockPos> { seat });
            if (group == null || group.Count > MaxTurnedBlocks) return null;

            return group;
        }

        /// <summary>
        /// Every cell the structure turns into has to be free, or be a cell the structure
        /// is vacating in the same turn.
        ///
        /// This checks where the blocks land, not the arc they sweep through. A block on
        /// the rim passes over its diagonal on the way round, and that diagonal is not
        /// tested — so a turntable can currently swing a structure past an obstacle it
        /// would have struck.
        /// </summary>
        bool CanLand(IBlockAccessor ba, List<BlockPos> group, int angle)
        {
            HashSet<BlockPos> vacated = new HashSet<BlockPos>(group);

            foreach (BlockPos from in group)
            {
                Vec3i offset = new Vec3i(
                    from.X - Pos.X,
                    from.InternalY - Pos.InternalY,
                    from.Z - Pos.Z);

                BlockPos to = BlockSnapshot.WorldPos(Pos, BlockSnapshot.Rotate(offset, angle));

                if (vacated.Contains(to)) continue;
                if (ba.GetChunkAtBlockPos(to) == null) return false;
                if (!IsFree(ba.GetBlock(to))) return false;
            }

            return true;
        }

        /// <summary>Glue marks turn with the blocks they belong to.</summary>
        void RestoreGlue(GlueRegistry glue, BlockSnapshot snapshot, int angle)
        {
            if (glue == null || snapshot.Glued == null) return;

            for (int i = 0; i < snapshot.Count && i < snapshot.Glued.Length; i++)
            {
                if (!snapshot.Glued[i]) continue;
                glue.Add(BlockSnapshot.WorldPos(Pos, BlockSnapshot.Rotate(snapshot.Offsets[i], angle)));
            }
        }
    }
}
