using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Mechworks
{
    /// <summary>
    /// Marks blocks as glued. Glued blocks that touch each other move as one when a
    /// piston or hoist takes hold of any of them.
    ///
    /// Right-click a block to glue it, right-click again to unglue.
    /// </summary>
    public class ItemGlue : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (blockSel == null)
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            handling = EnumHandHandling.PreventDefault;

            // Held right-click keeps calling this. Without the firstEvent guard a single
            // press toggles twice and quietly undoes itself.
            if (!firstEvent) return;

            IWorldAccessor world = byEntity.World;
            if (world.Side != EnumAppSide.Server) return;

            GlueRegistry glue = api.ModLoader.GetModSystem<MechworksModSystem>()?.Glue;
            if (glue == null) return;

            BlockPos pos = blockSel.Position;
            Block block = world.BlockAccessor.GetBlock(pos);

            if (!BEMoverBase.CanBeGlued(block))
            {
                Tell(byEntity, Lang.Get("mechworks:glue-refused"));
                return;
            }

            bool nowGlued = glue.Toggle(pos);
            Tell(byEntity, Lang.Get(nowGlued ? "mechworks:glue-added" : "mechworks:glue-removed", glue.Count));
        }

        static void Tell(EntityAgent byEntity, string message)
        {
            if ((byEntity as EntityPlayer)?.Player is not IServerPlayer player) return;
            player.SendMessage(GlobalConstants.CurrentChatGroup, message, EnumChatType.Notification);
        }
    }
}
