using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent.Mechanics;

namespace Mechworks
{
    /// <summary>
    /// The piston connects to the mechanical network like every other machine here — see
    /// the comment on deriving from BlockMPBase in <see cref="BlockRopeHoist"/> for why
    /// that is mandatory and not merely tidy.
    ///
    /// The "side" variant is the direction the beam drives out. The axle plugs into one of
    /// the two flanks, never the front or the back, which is where a real crank would sit.
    ///
    /// Which flank you use decides whether the piston extends or retracts on a given
    /// shaft: power arriving from the left and from the right give opposite rotation
    /// senses. That is the only reversing mechanism vanilla offers.
    /// </summary>
    public class BlockPiston : BlockMPBase
    {
        /// <summary>Direction the beam drives out, from the "side" variant.</summary>
        public BlockFacing PushFacing { get; private set; } = BlockFacing.NORTH;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            PushFacing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        }

        /// <summary>The two flanks take the axle; front, back, top and bottom do not.</summary>
        public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        {
            return IsFlank(face, PushFacing);
        }

        static bool IsFlank(BlockFacing face, BlockFacing pushFacing)
        {
            if (face == BlockFacing.UP || face == BlockFacing.DOWN) return false;
            return face != pushFacing && face != pushFacing.Opposite;
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

            // Either flank may already have a shaft waiting; the first one that takes wins.
            foreach (BlockFacing face in BlockFacing.HORIZONTALS)
            {
                if (!IsFlank(face, placedPush)) continue;
                if (tryConnect(world, byPlayer, blockSel.Position, face)) break;
            }

            return true;
        }

        /// <summary>
        /// Right-click with a beam in hand to load it, empty-handed to flip the direction
        /// by hand. The manual flip is a debug aid on top of the shaft's own rotation.
        /// </summary>
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BEPiston piston)
            {
                return base.OnBlockInteractStart(world, byPlayer, blockSel);
            }

            ItemSlot slot = byPlayer.InventoryManager?.ActiveHotbarSlot;

            if (IsBeam(slot?.Itemstack))
            {
                if (world.Side == EnumAppSide.Server) LoadBeam(world, byPlayer, piston, slot);
                return true;
            }

            if (byPlayer.Entity?.Controls?.Sneak == true)
            {
                if (world.Side == EnumAppSide.Server) UnloadBeams(world, byPlayer, piston);
                return true;
            }

            if (world.Side == EnumAppSide.Server) piston.ToggleInverted();
            return true;
        }

        static void LoadBeam(IWorldAccessor world, IPlayer byPlayer, BEPiston piston, ItemSlot slot)
        {
            string code = slot.Itemstack.Collectible.Code.ToString();

            if (!piston.AddBeam(code))
            {
                string why = !piston.CanAcceptBeam ? "mechworks:piston-beams-full"
                    : !piston.HasRoomBehind() ? "mechworks:piston-no-room-behind"
                    : "mechworks:piston-beams-mixed";
                Tell(byPlayer, Lang.Get(why, BEPiston.MaxBeams));
                return;
            }

            if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                slot.TakeOut(1);
                slot.MarkDirty();
            }

            Tell(byPlayer, Lang.Get("mechworks:piston-beam-loaded", piston.Beams, piston.Reach));
        }

        static void UnloadBeams(IWorldAccessor world, IPlayer byPlayer, BEPiston piston)
        {
            string code = piston.BeamCode;
            int taken = piston.RemoveAllBeams();
            if (taken == 0)
            {
                Tell(byPlayer, Lang.Get("mechworks:piston-retract-first"));
                return;
            }

            GiveBeams(world, code, byPlayer.Entity.Pos.AsBlockPos, taken);
            Tell(byPlayer, Lang.Get("mechworks:piston-beams-unloaded", taken));
        }

        /// <summary>Beams inside the machine are not lost when it is broken.</summary>
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            if (world.Side == EnumAppSide.Server
                && world.BlockAccessor.GetBlockEntity(pos) is BEPiston piston
                && piston.Beams > 0)
            {
                GiveBeams(world, piston.BeamCode, pos, piston.Beams);
            }

            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        }

        /// <summary>Hands back exactly the beams that went in, code and all.</summary>
        static void GiveBeams(IWorldAccessor world, string code, BlockPos at, int quantity)
        {
            if (string.IsNullOrEmpty(code) || quantity <= 0) return;

            AssetLocation loc = new AssetLocation(code);
            ItemStack stack = world.GetBlock(loc) is Block block && block.Id != 0
                ? new ItemStack(block, quantity)
                : world.GetItem(loc) is Item item ? new ItemStack(item, quantity) : null;

            if (stack == null) return;
            world.SpawnItemEntity(stack, at.ToVec3d().Add(0.5, 0.5, 0.5));
        }

        /// <summary>
        /// The vanilla wooden support beam, any wood. Heavier materials want a
        /// counterweight rule of their own before they are worth allowing.
        /// </summary>
        static bool IsBeam(ItemStack stack)
        {
            string path = stack?.Collectible?.Code?.Path;
            return path != null && path.StartsWith("supportbeam", System.StringComparison.Ordinal);
        }

        static void Tell(IPlayer player, string message)
        {
            (player as IServerPlayer)?.SendMessage(
                GlobalConstants.CurrentChatGroup, message, EnumChatType.Notification);
        }
    }
}
