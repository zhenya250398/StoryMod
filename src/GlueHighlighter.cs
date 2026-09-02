using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Mechworks
{
    /// <summary>
    /// Shows glued blocks to anyone holding the glue, so the set you are building is
    /// visible instead of invisible bookkeeping.
    ///
    /// Runs entirely on the server. HighlightBlocks lives on IWorldAccessor and pushes the
    /// overlay to that one player's client, so the client never has to be told what is
    /// glued and no network channel is needed.
    /// </summary>
    public class GlueHighlighter
    {
        /// <summary>
        /// Highlight slots are a small shared namespace with no registry; a distinctive
        /// number keeps us clear of vanilla's own (selection, land claim, brush...).
        /// </summary>
        const int HighlightSlot = 812;

        /// <summary>How far around the player glued blocks are shown.</summary>
        const int Radius = 24;

        const int RefreshIntervalMs = 500;

        static readonly List<BlockPos> NoBlocks = new List<BlockPos>();

        ICoreServerAPI sapi;
        GlueRegistry glue;

        /// <summary>Who currently has an overlay, so it can be cleared exactly once.</summary>
        readonly HashSet<string> showing = new HashSet<string>();

        public void Init(ICoreServerAPI api, GlueRegistry registry)
        {
            sapi = api;
            glue = registry;
            sapi.Event.RegisterGameTickListener(OnTick, RefreshIntervalMs);
        }

        void OnTick(float dt)
        {
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                if (IsHoldingGlue(player)) Show(player);
                else Hide(player);
            }
        }

        static bool IsHoldingGlue(IPlayer player)
        {
            ItemStack stack = player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            return stack?.Collectible is ItemGlue;
        }

        void Show(IPlayer player)
        {
            List<BlockPos> marks = glue.FindNear(player.Entity.Pos.AsBlockPos, Radius);

            List<int> colors = new List<int>(marks.Count);
            for (int i = 0; i < marks.Count; i++)
            {
                colors.Add(ColorUtil.ToRgba(80, 90, 220, 120));
            }

            // Arbitrary, not Cube. EnumHighlightShape.Cube means ONE cuboid described by
            // a start and an end corner, so a list of exactly two positions is read as a
            // corner pair instead of two separate blocks — one block and three-plus looked
            // fine, two did not.
            sapi.World.HighlightBlocks(
                player, HighlightSlot, marks, colors,
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);

            showing.Add(player.PlayerUID);
        }

        void Hide(IPlayer player)
        {
            if (!showing.Remove(player.PlayerUID)) return;

            sapi.World.HighlightBlocks(
                player, HighlightSlot, NoBlocks,
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        }
    }
}
