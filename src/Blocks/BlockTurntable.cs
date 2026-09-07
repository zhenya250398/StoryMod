using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Mechworks
{
    /// <summary>
    /// The turntable takes its shaft in through the back and turns about the axis its deck
    /// faces. The deck can face any of the six directions, chosen by the surface it is
    /// placed against, so the same block is a floor turntable, a ceiling one, or a wheel
    /// standing on edge against a wall.
    ///
    /// Which way it turns for a given shaft follows from the mounting. Turning about DOWN
    /// is the same motion as turning about UP the other way, so a deck facing down runs
    /// backwards against the identical drive — the same trick the piston plays with its
    /// two flanks, and still the only reverser vanilla offers.
    /// </summary>
    public class BlockTurntable : BlockMPBase
    {
        /// <summary>Which way the deck faces, and so the axis it turns about.</summary>
        public BlockFacing Facing { get; private set; } = BlockFacing.UP;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            Facing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.UP;
        }

        /// <summary>
        /// The back takes the shaft, and only the back.
        ///
        /// The deck side is deliberately not a socket. The cell against the deck is where
        /// the load is read from, so a shaft plugged in there would be picked up and turned
        /// as cargo — which is exactly what the old up-or-down socket allowed.
        /// </summary>
        public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        {
            return face == Facing.Opposite;
        }

        public override void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face)
        {
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

            // The deck points away from whatever it was placed against: click the top of a
            // block and the deck faces up with its shaft socket underneath, click a wall
            // and it stands on edge. blockSel.Face is the face that was hit, which is the
            // outward normal of the surface the machine backs onto.
            BlockFacing facing = blockSel.Face;

            Block oriented = world.GetBlock(CodeWithVariant("side", facing.Code));
            if (oriented == null) return false;

            oriented.DoPlaceBlock(world, byPlayer, blockSel, itemstack);

            // A shaft may already be waiting at the back.
            tryConnect(world, byPlayer, blockSel.Position, facing.Opposite);
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
