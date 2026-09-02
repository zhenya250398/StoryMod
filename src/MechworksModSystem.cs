using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Mechworks
{
    public class MechworksModSystem : ModSystem
    {
        /// <summary>
        /// Shared across carrier entities so a rider is not forgotten between strokes.
        /// One instance per side, since the mod system itself is per side.
        /// </summary>
        public readonly RiderMemory Riders = new RiderMemory();

        /// <summary>Which blocks are glued together. Populated on the server only.</summary>
        public readonly GlueRegistry Glue = new GlueRegistry();

        readonly GlueHighlighter glueHighlighter = new GlueHighlighter();

        ICoreServerAPI sapi;

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            sapi = api;

            Glue.Init(api);
            glueHighlighter.Init(api, Glue);

            // Beams belong to their piston. Letting players mine them out invites a whole
            // category of half-broken machines; they come back out by unloading the piston
            // or breaking it, and no other way.
            api.Event.CanPlaceOrBreakBlock += CanPlaceOrBreak;
        }

        bool CanPlaceOrBreak(IServerPlayer byPlayer, BlockSelection blockSel, out string claimant)
        {
            claimant = null;
            if (blockSel?.Position == null) return true;

            Block block = sapi.World.BlockAccessor.GetBlock(blockSel.Position);
            if (!BEPiston.IsPistonBeam(block)) return true;

            claimant = "the piston it belongs to";
            return false;
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // These names are what blocktype json refers to via "class" / "entityClass".
            api.RegisterBlockClass("MechworksPiston", typeof(BlockPiston));
            api.RegisterBlockEntityClass("MechworksPiston", typeof(BEPiston));

            api.RegisterBlockClass("MechworksRopeHoist", typeof(BlockRopeHoist));
            api.RegisterBlockEntityClass("MechworksRopeHoist", typeof(BERopeHoist));

            api.RegisterItemClass("MechworksGlue", typeof(ItemGlue));

            api.RegisterEntity("MechworksMovingBlocks", typeof(EntityMovingBlocks));
        }
    }
}
