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
    /// The "side" variant names the face the axle plugs into, matching the rope hoist. The
    /// beam drives out of the opposite face, so a piston is placed facing its own axle.
    /// Which face you plug into therefore decides whether a given shaft extends or
    /// retracts it: the two opposite sockets give opposite rotation senses, which is the
    /// only reversing mechanism vanilla offers.
    /// </summary>
    public class BlockPiston : BlockMPBase
    {
        /// <summary>Face that accepts the axle, from the "side" variant.</summary>
        public BlockFacing PowerFacing { get; private set; } = BlockFacing.NORTH;

        /// <summary>Face the beam drives out of — away from the axle.</summary>
        public BlockFacing PushFacing => PowerFacing.Opposite;

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
                Tell(byPlayer, Lang.Get(piston.CanAcceptBeam
                    ? "mechworks:piston-beams-mixed"
                    : "mechworks:piston-beams-full", BEPiston.MaxBeams));
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
