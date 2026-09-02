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

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            Glue.Init(api);
            glueHighlighter.Init(api, Glue);
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
