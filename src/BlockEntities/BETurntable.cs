using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Turntable: turns whatever is glued to its deck a quarter turn per stroke, about
    /// the axis the deck faces.
    ///
    /// A quarter turn is the smallest step because it is the smallest angle that leaves
    /// every block back on the grid. Anything finer would have no resting position.
    ///
    /// Unlike the piston and the hoist this moves each block somewhere different, so the
    /// carrier had to learn about rotation: it holds the load in place and spins it about
    /// the pivot rather than sliding it along an offset.
    ///
    /// The deck can face any of the six directions, so the axis is not always vertical.
    /// Mounted on a wall it turns a structure in a vertical plane like a waterwheel. Two
    /// things do not follow it there, both noted where they are handled: block codes,
    /// which vanilla can only turn about the vertical (BlockSnapshot.Turned), and riders,
    /// who have nothing to stand on halfway round (EntityMovingBlocks.TurnIsVertical).
    /// </summary>
    public class BETurntable : BEMoverBase
    {
        /// <summary>Largest structure one turntable will swing.</summary>
        public const int MaxTurnedBlocks = 64;

        protected override string StrokeNoun => "turn";

        /// <summary>
        /// Which way the deck faces, and so the axis it turns about. Read off the block,
        /// because the block entity outlives no placement of its own.
        /// </summary>
        public BlockFacing Facing =>
            (Block as BlockTurntable)?.Facing ?? BlockFacing.UP;

        /// <summary>
        /// A third of a shaft turn per quarter turn of the deck. Empirical: it is what
        /// looks right against the visibly spinning shaft. The base class calls the unit
        /// "revolutions", but the network's speed is not in revolutions per second, so the
        /// number is a ratio that had to be matched by eye rather than derived.
        /// </summary>
        public override float RevolutionsPerStroke => 1f / 3f;

        /// <summary>
        /// Which way a stroke turns, as an angle vanilla understands.
        ///
        /// Which rotation sense maps to which angle is not derivable — it depends on the
        /// shaft convention meeting the block-code convention — so this pairing was settled
        /// by watching the shaft and the deck turn together.
        /// </summary>
        int TurnAngle => Reversed ? 90 : 270;

        /// <summary>
        /// Temporary: reports which way a turn actually went, because the pairing between
        /// the shaft's rotation sense and the angle vanilla wants cannot be reasoned out.
        /// </summary>
        static readonly bool DebugTurn = false;

        void LogTurn(List<BlockPos> group, int angle)
        {
            BlockPos from = group[0];
            Vec3i offset = new Vec3i(from.X - Pos.X, from.InternalY - Pos.InternalY, from.Z - Pos.Z);
            BlockPos to = BlockSnapshot.WorldPos(Pos, BlockSnapshot.Rotate(offset, Facing, angle));

            Api.Logger.Notification(
                "[mechworks] turn pos={0} facing={1} reversed={2} angle={3} first {4} -> {5} (offset {6} -> {7})",
                Pos, Facing, Reversed, angle, from, to, offset, BlockSnapshot.Rotate(offset, Facing, angle));
        }

        protected override bool TryMove()
        {
            IBlockAccessor ba = Api.World.BlockAccessor;

            List<BlockPos> group = CollectLoad(ba);
            if (group == null) return false;

            int angle = TurnAngle;
            if (!CanLand(ba, group, angle)) return false;

            if (DebugTurn) LogTurn(group, angle);

            // The carrier takes it from here: it holds the blocks for the length of the
            // stroke, spins them about this block, and puts them down turned.
            return StartTurn(group, Facing, angle);
        }

        /// <summary>
        /// What sits on the turntable: the block against the deck, plus everything glued
        /// to it. Only glue holds a structure together here, the same rule the other
        /// machines use — resting on something is not being attached to it. That matters
        /// more than usual once the deck can face sideways or down, where "resting" would
        /// mean nothing at all.
        /// </summary>
        List<BlockPos> CollectLoad(IBlockAccessor ba)
        {
            BlockPos seat = Pos.AddCopy(Facing);
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

                BlockPos to = BlockSnapshot.WorldPos(Pos, BlockSnapshot.Rotate(offset, Facing, angle));

                if (vacated.Contains(to)) continue;
                if (ba.GetChunkAtBlockPos(to) == null) return false;
                if (!IsFree(ba.GetBlock(to))) return false;
            }

            return true;
        }

    }
}
