using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Mechworks
{
    /// <summary>
    /// The piston block itself is nearly dumb: orientation comes from the "side"
    /// variant (HorizontalOrientable behavior), the pushing lives in <see cref="BEPiston"/>.
    ///
    /// It must derive from BlockMPBase — carrying only the MPConsumer *behavior* is not
    /// enough. When a neighbouring axle propagates the network it runs
    /// BEBehaviorMPBase.spreadTo, which does:
    ///
    ///     BEBehaviorMPBase beMechBase = ...GetBehavior&lt;BEBehaviorMPBase&gt;();
    ///     IMechanicalPowerBlock mechBlock = beMechBase?.Block as IMechanicalPowerBlock;
    ///     if (beMechBase != null &amp;&amp; mechBlock.HasMechPowerConnectorAt(...))
    ///
    /// It null-checks the behavior but then dereferences the *block* cast. A block with
    /// the behavior but without the interface makes that cast null and crashes the game.
    /// </summary>
    public class BlockPiston : BlockMPBase
    {
        /// <summary>Direction the piston pushes towards, from the "side" variant.</summary>
        public BlockFacing PushFacing { get; private set; } = BlockFacing.NORTH;

        /// <summary>
        /// Face that accepts the axle — the back. Power in the front would put the
        /// axle exactly where the pushed block needs to go.
        /// </summary>
        public BlockFacing PowerFacing => PushFacing.Opposite;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            PushFacing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
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
            BlockFacing placedPush = BlockFacing.FromCode(placed?.Variant["side"]) ?? PushFacing;
            tryConnect(world, byPlayer, blockSel.Position, placedPush.Opposite);

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
