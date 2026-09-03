using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Piston: drives a beam out of itself to shove blocks away, and draws it back in to
    /// pull them closer. One cell per stroke.
    ///
    /// Reach is set by how many beams have been loaded. One beam always stays inside as a
    /// counterweight, so a full load of four reaches three cells. The beams are a resource
    /// held by the machine, not blocks in the world — the piston stays one block wide
    /// whatever its extension.
    /// </summary>
    public class BEPiston : BEMoverBase
    {
        /// <summary>Most beams the machine will hold.</summary>
        public const int MaxBeams = 4;

        /// <summary>Beams that stay inside as a counterweight and never extend.</summary>
        public const int CounterweightBeams = 1;

        /// <summary>Longest run of blocks one stroke may shift.</summary>
        public const int MaxPushedBlocks = 12;

        /// <summary>
        /// A quarter of a turn per stroke, so the piston cycles four times as fast as the
        /// baseline machine at the same shaft speed.
        /// </summary>
        public override float RevolutionsPerStroke => 0.25f;

        int beams;
        int extension;

        /// <summary>Id of the queued beam write, -1 when none is pending.</summary>
        long pendingBeamSync = -1;

        /// <summary>
        /// Exactly which beam went in, so exactly that comes back out. Guessing a code
        /// would mean guessing both the asset domain and the wood type.
        /// </summary>
        string beamCode;

        /// <summary>Beams loaded into the machine.</summary>
        public int Beams => beams;

        /// <summary>How far this piston can drive its beam out, in cells.</summary>
        public int Reach => System.Math.Max(0, beams - CounterweightBeams);

        /// <summary>How far the beam is currently driven out, in cells.</summary>
        public int Extension => extension;

        public bool CanAcceptBeam => beams < MaxBeams;

        /// <summary>Code of the beams held, null when empty.</summary>
        public string BeamCode => beamCode;

        protected override string StrokeNoun => Reversed ? "pull" : "push";

        /// <summary>Direction the beam drives out, straight from the block variant.</summary>
        public BlockFacing PushFacing => (Block as BlockPiston)?.PushFacing ?? BlockFacing.NORTH;

        /// <summary>
        /// Tip of the extended beam. The beam is not made of world blocks, so the machine
        /// has to remember where its own reach currently ends; otherwise after the first
        /// stroke it looks at the empty cell it just vacated and finds nothing to push.
        /// </summary>
        BlockPos BeamTip => Pos.AddCopy(PushFacing, Extension);

        /// <summary>
        /// Loads one beam. False when full, or when it does not match the beams already
        /// inside — a mixed load would have no honest way to give itself back.
        /// </summary>
        public bool AddBeam(string code)
        {
            if (!CanAcceptBeam) return false;
            if (beams > 0 && beamCode != code) return false;
            if (!HasRoomBehind()) return false;

            beams++;
            beamCode = code;
            SyncBeamBlocks();
            MarkDirty(true);
            return true;
        }

        /// <summary>
        /// A loaded beam has to physically go somewhere: it trails out the back, so the
        /// next cell back has to be clear before another one will fit.
        /// </summary>
        public bool HasRoomBehind()
        {
            if (Api?.World == null) return true;

            // One more beam means one more cell of trailing beam behind the machine.
            BlockPos at = Pos.AddCopy(PushFacing.Opposite, BackBeams + 1);
            IBlockAccessor ba = Api.World.BlockAccessor;

            if (ba.GetChunkAtBlockPos(at) == null) return false;

            Block current = ba.GetBlock(at);
            return IsFree(current) || IsPistonBeam(current);
        }

        /// <summary>
        /// Takes every beam back out and reports how many. Refuses while the beam is
        /// driven out — that stroke has to be undone first.
        /// </summary>
        public int RemoveAllBeams()
        {
            if (extension > 0) return 0;

            int taken = beams;
            beams = 0;
            beamCode = null;
            SyncBeamBlocks();
            MarkDirty(true);
            return taken;
        }

        PistonHeadRenderer headRenderer;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            // Chunks can come back with the beam cells missing or stale; the machine's own
            // counters are the truth, so restate them in the world.
            SyncBeamBlocks();

            if (api is ICoreClientAPI capi)
            {
                headRenderer = new PistonHeadRenderer(capi, this);
                capi.Event.RegisterRenderer(headRenderer, EnumRenderStage.Opaque, "mechworks:pistonhead");
            }
        }

        /// <summary>
        /// Draws everything except the moving parts; those are drawn by
        /// PistonHeadRenderer so they can slide. Leaving them in here as well would render
        /// them twice, once stuck at rest.
        /// </summary>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            return TesselateSelf(mesher, tessThreadTesselator, PistonHeadRenderer.MovingElements);
        }

        void CancelPendingBeamSync()
        {
            if (pendingBeamSync < 0) return;

            UnregisterDelayedCallback(pendingBeamSync);
            pendingBeamSync = -1;
        }

        void DisposeRenderer()
        {
            if (headRenderer == null || Api is not ICoreClientAPI capi) return;

            capi.Event.UnregisterRenderer(headRenderer, EnumRenderStage.Opaque);
            headRenderer.Dispose();
            headRenderer = null;
        }

        public override void OnBlockUnloaded()
        {
            CancelPendingBeamSync();
            DisposeRenderer();
            base.OnBlockUnloaded();
        }

        public override void OnBlockRemoved()
        {
            CancelPendingBeamSync();
            DisposeRenderer();

            beams = 0;
            extension = 0;
            SyncBeamBlocks();
            base.OnBlockRemoved();
        }

        /// <summary>Cells the beam occupies in front of the piston.</summary>
        int FrontBeams => extension;

        /// <summary>Cells the beam occupies behind the piston.</summary>
        public int BackBeams => System.Math.Max(0, beams - CounterweightBeams - extension);

        /// <summary>
        /// Moves the world beam in step with the stroke it is drawing.
        ///
        /// The two ends want opposite timing. A cell the rod is moving into must stay empty
        /// until it arrives, or the block teleports ahead of its own animation. A cell the
        /// rod is leaving must empty at once, or the block sits there unmoved and vanishes
        /// at the end. So: clear now, place on arrival.
        ///
        /// The counters step immediately either way — the renderer interpolates from them.
        /// </summary>
        void SyncBeamBlocksForStroke()
        {
            if (Api?.Side != EnumAppSide.Server) return;

            // Cells the rod has left are freed at once; cells it is moving into are filled
            // only when it gets there.
            SyncBeamBlocks(place: false, clear: true);

            if (pendingBeamSync >= 0) UnregisterDelayedCallback(pendingBeamSync);
            pendingBeamSync = RegisterDelayedCallback(_ =>
            {
                pendingBeamSync = -1;
                SyncBeamBlocks();
            }, (int)(MoveDurationSec * 1000));
        }

        /// <summary>
        /// Writes the beam into the world to match the machine's own counters: as much as
        /// is extended in front, the remainder trailing out the back, nothing anywhere
        /// else. Driving it from state rather than patching cells one at a time means a
        /// beam block broken by a player simply comes back on the next stroke.
        /// </summary>
        void SyncBeamBlocks(bool place = true, bool clear = true)
        {
            if (Api?.Side != EnumAppSide.Server) return;

            BlockFacing facing = PushFacing;
            Block beam = Api.World.GetBlock(new AssetLocation("mechworks", "pistonbeam-" + facing.Code));
            if (beam == null || beam.Id == 0) return;

            IBlockAccessor ba = Api.World.BlockAccessor;
            int front = FrontBeams;
            int back = BackBeams;

            for (int i = 1; i <= MaxBeams; i++)
            {
                SetBeamCell(ba, beam, Pos.AddCopy(facing, i), i <= front, place, clear);
                SetBeamCell(ba, beam, Pos.AddCopy(facing.Opposite, i), i <= back, place, clear);
            }
        }

        void SetBeamCell(IBlockAccessor ba, Block beam, BlockPos at, bool wanted, bool place = true, bool clear = true)
        {
            if (ba.GetChunkAtBlockPos(at) == null) return;
            if (wanted ? !place : !clear) return;

            Block current = ba.GetBlock(at);
            bool isBeam = IsPistonBeam(current);

            if (wanted)
            {
                if (isBeam) return;
                if (!IsFree(current)) return;   // someone else's block: leave it be
                ba.SetBlock(beam.Id, at);
                ba.MarkBlockDirty(at);
                return;
            }

            if (!isBeam) return;
            ba.SetBlock(0, at);
            ba.MarkBlockDirty(at);
        }

        public static bool IsPistonBeam(Block block)
        {
            string path = block?.Code?.Path;
            return path != null && path.StartsWith("pistonbeam", System.StringComparison.Ordinal);
        }

        protected override bool TryMove()
        {
            return Reversed ? TryRetract() : TryExtend();
        }

        bool TryExtend()
        {
            if (extension >= Reach) return false;   // beam is already all the way out

            BlockFacing facing = PushFacing;
            IBlockAccessor ba = Api.World.BlockAccessor;

            BlockPos nextTip = BeamTip.AddCopy(facing);
            if (ba.GetChunkAtBlockPos(nextTip) == null) return false;

            // The beam is part of the machine, not cargo: it drives out into empty air
            // just as happily as against a load. Only something actually occupying the
            // cell has to be shifted first, and only that can refuse the stroke.
            if (!IsFree(ba.GetBlock(nextTip)))
            {
                List<BlockPos> chain = CollectPushChain(ba, facing);
                if (chain == null) return false;

                // Anything glued to the chain comes along, so a piston can shove a
                // structure and not just the line of blocks directly ahead of it.
                List<BlockPos> group = ExpandThroughGlue(chain);
                if (group == null) return false;
                if (!StartMove(group, facing)) return false;
            }

            extension++;
            SyncBeamBlocksForStroke();
            MarkDirty(true);
            return true;
        }

        bool TryRetract()
        {
            if (extension <= 0) return false;       // nothing to draw back in

            // Drawing in adds a cell of beam behind, so that cell has to be clear. Without
            // this the beam simply passed through whatever had been built there.
            if (!HasRoomBehind()) return false;

            BlockFacing facing = PushFacing;
            IBlockAccessor ba = Api.World.BlockAccessor;

            // The beam gives up its outermost cell first, otherwise the load it is dragging
            // back has nowhere to land — the beam itself would be standing in the way.
            SetBeamCell(ba, null, BeamTip, wanted: false);

            List<BlockPos> chain = CollectPullChain(ba, facing);

            // Drawing the beam back in works with nothing attached to it too: the machine
            // still has to return to rest before it can extend again.
            if (chain != null)
            {
                List<BlockPos> group = ExpandThroughGlue(chain);
                if (group == null || !StartMove(group, facing.Opposite))
                {
                    SyncBeamBlocks();   // stroke refused: put the beam cell back
                    return false;
                }
            }

            extension--;
            SyncBeamBlocksForStroke();
            MarkDirty(true);
            return true;
        }

        /// <summary>
        /// Walks forward collecting the contiguous run of blocks to move, stopping at the
        /// first free cell that the run will be shifted into. Null when the push is not
        /// legal: nothing in front, an immovable block in the way, the run longer than
        /// MaxPushedBlocks, or the path leaving loaded chunks.
        /// </summary>
        List<BlockPos> CollectPushChain(IBlockAccessor ba, BlockFacing facing)
        {
            List<BlockPos> chain = new List<BlockPos>();
            BlockPos cur = BeamTip.AddCopy(facing);

            while (true)
            {
                // Never push into terrain that is not loaded — GetBlock would report air
                // there and the write would be lost when the chunk loads for real.
                if (ba.GetChunkAtBlockPos(cur) == null) return null;

                Block block = ba.GetBlock(cur);
                if (IsFree(block)) break;                  // found the landing cell
                if (!IsMovable(block)) return null;        // bedrock and friends
                if (chain.Count >= MaxPushedBlocks) return null;

                chain.Add(cur.Copy());
                cur = cur.AddCopy(facing);
            }

            return chain.Count == 0 ? null : chain;
        }

        /// <summary>
        /// The single block a retraction drags back: the one just beyond the free cell in
        /// front. Deliberately one block — pushing shoves a whole run, pulling takes hold
        /// of one thing. Touching is not attachment; glue is, and ExpandThroughGlue adds
        /// the rest of the group afterwards.
        /// </summary>
        List<BlockPos> CollectPullChain(IBlockAccessor ba, BlockFacing facing)
        {
            // The load rides on the beam tip, so it comes back to where the tip is now.
            BlockPos landing = BeamTip.Copy();
            if (ba.GetChunkAtBlockPos(landing) == null) return null;
            if (!IsFree(ba.GetBlock(landing))) return null;    // nothing to pull it into

            BlockPos target = landing.AddCopy(facing);
            if (ba.GetChunkAtBlockPos(target) == null) return null;

            Block block = ba.GetBlock(target);
            if (IsFree(block)) return null;                    // nothing there to grab
            if (!IsMovable(block)) return null;

            return new List<BlockPos> { target };
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetInt("beams", beams);
            tree.SetInt("extension", extension);
            tree.SetString("beamCode", beamCode ?? "");
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            int wasExtension = extension;

            beams = tree.GetInt("beams");
            extension = tree.GetInt("extension");

            // Learning the machine has moved is the client's cue to run the animation, and
            // it arrives alongside the entity carrying whatever is being pushed.
            if (worldAccessForResolve.Side == EnumAppSide.Client && extension != wasExtension)
            {
                BeginStroke();
            }
            beamCode = tree.GetString("beamCode");
            if (string.IsNullOrEmpty(beamCode)) beamCode = null;
        }

        public override void GetBlockInfo(IPlayer forPlayer, System.Text.StringBuilder sb)
        {
            if (beams == 0)
            {
                sb.AppendLine("No beams loaded");
            }
            else
            {
                sb.AppendLine(string.Format("Beams: {0}/{1}, reach {2}", beams, MaxBeams, Reach));
                sb.AppendLine(string.Format("Extended {0}/{1}", extension, Reach));
            }

            base.GetBlockInfo(forPlayer, sb);
        }
    }
}
