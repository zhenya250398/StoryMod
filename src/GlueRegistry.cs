using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Mechworks
{
    /// <summary>
    /// Remembers which blocks have been glued together.
    ///
    /// Vintage Story blocks carry no per-position metadata unless they have a block
    /// entity, and gluing arbitrary vanilla blocks must not require replacing them. So the
    /// marks live here instead, in a set saved with the world.
    ///
    /// Server-only on purpose: groups are worked out inside TryMove, which is
    /// server-authoritative, and the resulting snapshot is what reaches the client. The
    /// client never needs to know what is glued, so none of this is synced.
    /// </summary>
    public class GlueRegistry
    {
        const string SaveKey = "mechworksGluedBlocks";

        readonly HashSet<BlockPos> glued = new HashSet<BlockPos>();
        ICoreServerAPI sapi;

        public int Count => glued.Count;

        public void Init(ICoreServerAPI api)
        {
            sapi = api;
            sapi.Event.SaveGameLoaded += Load;
            sapi.Event.GameWorldSave += Save;

            // A mark on a block that no longer exists is worse than untidy: the movers
            // would treat the hole as part of the group and stall.
            sapi.Event.DidBreakBlock += (player, oldBlockId, blockSel) => Remove(blockSel?.Position);
        }

        public bool IsGlued(BlockPos pos)
        {
            return pos != null && glued.Contains(pos);
        }

        /// <summary>Returns the state the position ended up in.</summary>
        public bool Toggle(BlockPos pos)
        {
            BlockPos key = pos.Copy();
            if (glued.Remove(key)) return false;

            glued.Add(key);
            return true;
        }

        /// <summary>Sets the mark regardless of what was there. Use when restoring.</summary>
        public void Add(BlockPos pos)
        {
            if (pos != null) glued.Add(pos.Copy());
        }

        /// <summary>Glued positions within a cube of the given radius, for the overlay.</summary>
        public List<BlockPos> FindNear(BlockPos centre, int radius)
        {
            List<BlockPos> found = new List<BlockPos>();
            if (centre == null) return found;

            IBlockAccessor ba = sapi.World.BlockAccessor;
            List<BlockPos> stale = null;

            foreach (BlockPos pos in glued)
            {
                if (pos.dimension != centre.dimension) continue;
                if (System.Math.Abs(pos.X - centre.X) > radius) continue;
                if (System.Math.Abs(pos.InternalY - centre.InternalY) > radius) continue;
                if (System.Math.Abs(pos.Z - centre.Z) > radius) continue;

                // Sweeping while highlighting keeps the overlay honest and slowly clears
                // marks left by anything that removed a block without an event.
                Block block = ba.GetBlock(pos);
                if (block == null || block.Id == 0 || block.IsLiquid())
                {
                    (stale ??= new List<BlockPos>()).Add(pos);
                    continue;
                }

                found.Add(pos);
            }

            if (stale != null)
            {
                foreach (BlockPos pos in stale) glued.Remove(pos);
            }

            return found;
        }

        /// <summary>Called when a glued block stops existing, so marks do not pile up.</summary>
        public void Remove(BlockPos pos)
        {
            if (pos != null) glued.Remove(pos);
        }

        /// <summary>
        /// True when the mark refers to a block that is no longer there, in which case the
        /// mark is dropped. Breaking a block is only the common way for that to happen —
        /// explosions, decay and worldgen leave no event to hook, so callers verify lazily.
        ///
        /// Blocks in flight never look stale here: their marks are lifted along with them
        /// and put back where they land.
        /// </summary>
        public bool PruneIfStale(IBlockAccessor ba, BlockPos pos)
        {
            Block block = ba.GetBlock(pos);
            if (block != null && block.Id != 0 && !block.IsLiquid()) return false;

            glued.Remove(pos);
            return true;
        }

        void Load()
        {
            glued.Clear();

            int[] flat = sapi.WorldManager.SaveGame.GetData<int[]>(SaveKey, null);
            if (flat == null) return;

            for (int i = 0; i + 3 < flat.Length; i += 4)
            {
                glued.Add(new BlockPos(flat[i], flat[i + 1], flat[i + 2], flat[i + 3]));
            }
        }

        void Save()
        {
            int[] flat = new int[glued.Count * 4];
            int w = 0;
            foreach (BlockPos pos in glued)
            {
                flat[w++] = pos.X;
                flat[w++] = pos.InternalY;
                flat[w++] = pos.Z;
                flat[w++] = pos.dimension;
            }

            sapi.WorldManager.SaveGame.StoreData(SaveKey, flat);
        }
    }
}
