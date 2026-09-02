using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// A set of blocks lifted out of the world grid, remembered well enough to be put
    /// back somewhere else: block code, and the block entity payload if there was one.
    ///
    /// Positions are stored relative to an origin so the whole thing can be written back
    /// at an offset without touching every entry.
    /// </summary>
    public class BlockSnapshot
    {
        public Vec3i[] Offsets;
        public string[] BlockCodes;
        public TreeAttribute[] Trees;

        /// <summary>
        /// Which cells carried a glue mark. Server-side only and deliberately not synced —
        /// the client never needs it, but the marks have to travel with the blocks or glue
        /// would survive exactly one stroke.
        /// </summary>
        public bool[] Glued;

        public int Count => Offsets?.Length ?? 0;

        /// <summary>
        /// Reads the given positions out of the world. Does not modify anything —
        /// call <see cref="ClearFromWorld"/> separately once the snapshot is safe.
        /// </summary>
        public static BlockSnapshot Capture(IBlockAccessor ba, IList<BlockPos> positions, BlockPos origin)
        {
            int count = positions.Count;
            BlockSnapshot snap = new BlockSnapshot
            {
                Offsets = new Vec3i[count],
                BlockCodes = new string[count],
                Trees = new TreeAttribute[count]
            };

            for (int i = 0; i < count; i++)
            {
                BlockPos pos = positions[i];
                snap.Offsets[i] = new Vec3i(
                    pos.X - origin.X,
                    pos.InternalY - origin.InternalY,
                    pos.Z - origin.Z);
                snap.BlockCodes[i] = ba.GetBlock(pos).Code.ToString();

                BlockEntity be = ba.GetBlockEntity(pos);
                if (be == null) continue;
                TreeAttribute tree = new TreeAttribute();
                be.ToTreeAttributes(tree);
                snap.Trees[i] = tree;
            }

            return snap;
        }

        public void ClearFromWorld(IBlockAccessor ba, BlockPos origin)
        {
            for (int i = 0; i < Count; i++)
            {
                BlockPos pos = WorldPos(origin, Offsets[i]);
                ba.SetBlock(0, pos);
                ba.MarkBlockDirty(pos);
            }
        }

        /// <summary>
        /// Writes every block back, positioned relative to <paramref name="origin"/> and
        /// optionally turned about it. Assumes the target cells were already checked free.
        /// </summary>
        public void RestoreToWorld(IWorldAccessor world, BlockPos origin, int angleDeg = 0)
        {
            IBlockAccessor ba = world.BlockAccessor;

            for (int i = 0; i < Count; i++)
            {
                Block block = world.GetBlock(new AssetLocation(BlockCodes[i]));
                if (block == null || block.Id == 0) continue;

                block = Turned(world, block, angleDeg);
                if (block == null || block.Id == 0) continue;

                BlockPos pos = WorldPos(origin, Rotate(Offsets[i], angleDeg));
                ba.SetBlock(block.Id, pos);
                RestoreBlockEntity(world, pos, Trees[i]);
                ba.MarkBlockDirty(pos);
            }
        }

        /// <summary>
        /// Turns an offset about the vertical axis. Paired with <see cref="Turned"/>: both
        /// must agree or a turned structure ends up with its blocks facing the wrong way.
        ///
        /// The sense comes from vanilla. BlockBehaviorHorizontalOrientable rotates a code by
        /// stepping back through HORIZONTALS_ANGLEORDER (E, N, W, S), so 90 degrees maps
        /// north to east — clockwise seen from above. In world axes, where north is -Z and
        /// east is +X, that is (x, z) -> (-z, x).
        /// </summary>
        public static Vec3i Rotate(Vec3i offset, int angleDeg)
        {
            switch (GameMath.Mod(angleDeg, 360))
            {
                case 90: return new Vec3i(-offset.Z, offset.Y, offset.X);
                case 180: return new Vec3i(-offset.X, offset.Y, -offset.Z);
                case 270: return new Vec3i(offset.Z, offset.Y, -offset.X);
                default: return offset.Clone();
            }
        }

        /// <summary>
        /// The same block turned to match. Blocks with no notion of facing return
        /// themselves; a few return a code that no longer resolves, and those are dropped
        /// rather than silently placed unturned.
        /// </summary>
        static Block Turned(IWorldAccessor world, Block block, int angleDeg)
        {
            if (GameMath.Mod(angleDeg, 360) == 0) return block;

            AssetLocation code = block.GetRotatedBlockCode(angleDeg);
            if (code == null || code.Equals(block.Code)) return block;

            return world.GetBlock(code) ?? block;
        }

        /// <summary>
        /// Re-applies a captured block entity payload at its new position.
        /// The saved tree still holds the *old* coordinates and SetBlock does not fix
        /// them up, so they have to be rewritten by hand or the block entity keeps
        /// acting as if it lived at the old spot.
        /// </summary>
        static void RestoreBlockEntity(IWorldAccessor world, BlockPos pos, TreeAttribute savedTree)
        {
            if (savedTree == null || savedTree.Count == 0) return;

            BlockEntity be = world.BlockAccessor.GetBlockEntity(pos);
            if (be == null) return;

            TreeAttribute tree = savedTree.Clone() as TreeAttribute;
            if (tree == null) return;

            tree.SetInt("posx", pos.X);
            tree.SetInt("posy", pos.InternalY); // InternalY, not Y — Y is dimension-local
            tree.SetInt("posz", pos.Z);

            be.FromTreeAttributes(tree, world);
            be.MarkDirty(true);
        }

        public static BlockPos WorldPos(BlockPos origin, Vec3i offset)
        {
            return new BlockPos(
                origin.X + offset.X,
                origin.InternalY + offset.Y,
                origin.Z + offset.Z,
                origin.dimension);
        }

        // --- persistence, so the client can render what the server captured ---

        public void ToAttributes(TreeAttribute tree)
        {
            tree.SetVec3is("offsets", Offsets);
            tree.SetStringArray("blockCodes", BlockCodes);

            TreeAttribute[] trees = new TreeAttribute[Count];
            for (int i = 0; i < Count; i++) trees[i] = Trees[i] ?? new TreeAttribute();
            tree["trees"] = new TreeArrayAttribute(trees);
        }

        public static BlockSnapshot FromAttributes(ITreeAttribute source)
        {
            // SetStringArray/GetStringArray live on the concrete TreeAttribute,
            // not on the interface.
            if (source is not TreeAttribute tree) return null;

            Vec3i[] offsets = tree.GetVec3is("offsets", null);
            string[] codes = tree.GetStringArray("blockCodes", null);
            if (offsets == null || codes == null || offsets.Length == 0) return null;

            int count = System.Math.Min(offsets.Length, codes.Length);
            TreeAttribute[] stored = (tree["trees"] as TreeArrayAttribute)?.value;
            TreeAttribute[] trees = new TreeAttribute[count];
            for (int i = 0; i < count; i++)
            {
                trees[i] = stored != null && i < stored.Length ? stored[i] : null;
            }

            return new BlockSnapshot { Offsets = offsets, BlockCodes = codes, Trees = trees };
        }
    }
}
