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
        /// optionally turned about the <paramref name="axis"/> through it. Assumes the
        /// target cells were already checked free.
        /// </summary>
        public void RestoreToWorld(IWorldAccessor world, BlockPos origin, BlockFacing axis = null, int angleDeg = 0)
        {
            IBlockAccessor ba = world.BlockAccessor;

            for (int i = 0; i < Count; i++)
            {
                Block block = world.GetBlock(new AssetLocation(BlockCodes[i]));
                if (block == null || block.Id == 0) continue;

                block = Turned(world, block, axis, angleDeg);
                if (block == null || block.Id == 0) continue;

                BlockPos pos = WorldPos(origin, Rotate(Offsets[i], axis, angleDeg));
                ba.SetBlock(block.Id, pos);
                RestoreBlockEntity(world, pos, Trees[i]);
                ba.MarkBlockDirty(pos);
            }
        }

        /// <summary>
        /// Turns an offset about one of the six axis directions. Paired with
        /// <see cref="Turned"/>: both must agree or a turned structure ends up with its
        /// blocks facing the wrong way.
        ///
        /// The sense for a vertical axis comes from vanilla. BlockBehaviorHorizontalOrientable
        /// rotates a code by stepping back through HORIZONTALS_ANGLEORDER (E, N, W, S), so
        /// 90 degrees maps north to east — clockwise seen from above. In world axes, where
        /// north is -Z and east is +X, that is (x, z) -> (-z, x).
        ///
        /// That is the *opposite* handedness to the usual rotation about +Y, so the other
        /// axes are derived from it rather than guessed: this is a right-handed rotation
        /// about the axis by MINUS the angle. Written out as Rodrigues' formula, which for
        /// quarter turns about a unit axis is exact in integers — cosine and sine are only
        /// ever 0 and +/-1.
        ///
        /// A null axis means the vertical one, so a straight move needs no axis at all.
        /// </summary>
        public static Vec3i Rotate(Vec3i offset, BlockFacing axis, int angleDeg)
        {
            int a = GameMath.Mod(angleDeg, 360);
            if (a == 0) return offset.Clone();

            axis ??= BlockFacing.UP;
            Vec3i n = axis.Normali;

            // v' = v cos t + (n x v) sin t + n (n.v) (1 - cos t), with t = -angle.
            int cos = a == 180 ? -1 : 0;
            int sin = a == 90 ? -1 : a == 270 ? 1 : 0;

            Vec3i cross = new Vec3i(
                n.Y * offset.Z - n.Z * offset.Y,
                n.Z * offset.X - n.X * offset.Z,
                n.X * offset.Y - n.Y * offset.X);

            int dot = n.X * offset.X + n.Y * offset.Y + n.Z * offset.Z;
            int k = dot * (1 - cos);

            return new Vec3i(
                offset.X * cos + cross.X * sin + n.X * k,
                offset.Y * cos + cross.Y * sin + n.Y * k,
                offset.Z * cos + cross.Z * sin + n.Z * k);
        }

        /// <summary>
        /// The same block turned to match. Blocks with no notion of facing return
        /// themselves; a few return a code that no longer resolves, and those are dropped
        /// rather than silently placed unturned.
        ///
        /// Only a vertical axis can turn a block code. Vanilla models orientation as a
        /// horizontal facing plus, at most, an up/down flip — GetRotatedBlockCode takes a
        /// yaw and nothing else, and there is simply no code for "these stairs, tipped a
        /// quarter turn onto their side". So a turntable on a wall moves blocks to the
        /// right cells but leaves each one facing the way it already faced. Plain blocks
        /// are unaffected; stairs, chests and the like come out turned in place.
        /// </summary>
        static Block Turned(IWorldAccessor world, Block block, BlockFacing axis, int angleDeg)
        {
            int yaw = YawFor(axis, angleDeg);
            if (yaw == 0) return block;

            AssetLocation code = block.GetRotatedBlockCode(yaw);
            if (code == null || code.Equals(block.Code)) return block;

            return world.GetBlock(code) ?? block;
        }

        /// <summary>
        /// The turn expressed as a yaw, or 0 when it cannot be. Turning about DOWN is the
        /// same motion as turning about UP the other way, which is what makes mounting the
        /// machine upside down a working reverser.
        /// </summary>
        static int YawFor(BlockFacing axis, int angleDeg)
        {
            if (GameMath.Mod(angleDeg, 360) == 0) return 0;
            if (axis == null || axis == BlockFacing.UP) return angleDeg;
            if (axis == BlockFacing.DOWN) return -angleDeg;
            return 0;
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
