using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Mechworks
{
    /// <summary>
    /// The turntable takes its shaft from directly above or below — the axis it turns
    /// about — so unlike the other machines it has no facing and no variants.
    ///
    /// Which of the two faces the shaft plugs into decides which way it turns, the same
    /// way the choice of flank decides for the piston: the two give opposite rotation
    /// senses, and vanilla offers no other reverser.
    /// </summary>
    public class BlockTurntable : BlockMPBase
    {
        public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        {
            return face == BlockFacing.UP || face == BlockFacing.DOWN;
        }

        public override void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face)
        {
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            bool ok = base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode);
            if (!ok) return false;

            // Either end may already have a shaft waiting; the first that takes wins.
            if (!tryConnect(world, byPlayer, blockSel.Position, BlockFacing.DOWN))
            {
                tryConnect(world, byPlayer, blockSel.Position, BlockFacing.UP);
            }

            return true;
        }

        /// <summary>
        /// Right-clicking flips the direction by hand, on top of the shaft's own rotation.
        /// Debug aid: vanilla gives players no way to reverse a mechanical network.
        /// </summary>
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BEMoverBase mover)
            {
                return base.OnBlockInteractStart(world, byPlayer, blockSel);
            }

            if (world.Side == EnumAppSide.Server) mover.ToggleInverted();
            return true;
        }
    }
}
