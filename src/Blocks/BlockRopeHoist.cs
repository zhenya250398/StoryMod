using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Mechworks
{
    /// <summary>
    /// The rope hoist connects to the mechanical network exactly like the piston does —
    /// see <see cref="BlockPiston"/> for why deriving from BlockMPBase is mandatory and
    /// not merely tidy. The difference is that the "side" variant here is purely the axle
    /// socket; the load always travels straight up and down.
    /// </summary>
    public class BlockRopeHoist : BlockMPBase
    {
        /// <summary>Face that accepts the axle, from the "side" variant.</summary>
        public BlockFacing PowerFacing { get; private set; } = BlockFacing.NORTH;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            PowerFacing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        }

        public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        {
            return face == PowerFacing;
        }

        public override void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face)
        {
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            bool ok = base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode);
            if (!ok) return false;

            // HorizontalOrientable swapped in the rotated variant, so re-read the facing
            // off the world rather than trusting this instance's own.
            Block placed = world.BlockAccessor.GetBlock(blockSel.Position);
            BlockFacing placedPower = BlockFacing.FromCode(placed?.Variant["side"]) ?? PowerFacing;
            tryConnect(world, byPlayer, blockSel.Position, placedPower);

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
