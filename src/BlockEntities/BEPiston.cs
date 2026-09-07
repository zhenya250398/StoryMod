using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Piston: drives a beam out of itself to shove blocks away, and draws it back in to
    /// pull them closer, continuously, for as long as the shaft turns.
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

        /// <summary>
        /// How far into the next cell the beam has driven, 0 to 1. Only used while the
        /// machine is idle: with nothing in front there is no load and so no carrier, and
        /// the beam still has to come out smoothly rather than jump a cell at a time.
        ///
        /// While a run is going the carrier owns the fraction — beam and load are the same
        /// motion, and reading it off the load is the only way they cannot drift apart.
        /// </summary>
        double beamFraction;

        /// <summary>Extension the current run started from, and which way it is going.</summary>
        int runStartExtension;
        bool runRetracting;

        /// <summary>
        /// Exactly which beam went in, so exactly that comes back out. Guessing a code
        /// would mean guessing both the asset domain and the wood type.
        /// </summary>
        string beamCode;

        /// <summary>Beams loaded into the machine.</summary>
        public int Beams => beams;

        /// <summary>How far this piston can drive its beam out, in cells.</summary>
        public int Reach => System.Math.Max(0, beams - CounterweightBeams);

        /// <summary>How far the beam is currently driven out, in whole cells.</summary>
        public int Extension => extension;

        /// <summary>
        /// How far the beam is driven out including the part-cell it is in the middle of.
        /// This is what the renderer draws against, so the beam, its head and the blocks
        /// it is shoving are all one motion.
        /// </summary>
        public double BeamOut
        {
            get
            {
                if (!Running) return extension + beamFraction;

                double frac = RunProgress - System.Math.Floor(RunProgress);
                return extension + (runRetracting ? -frac : frac);
            }
        }

        /// <summary>Cells of beam trailing out the back, part-cells included.</summary>
        public double BeamBack => System.Math.Max(0, beams - CounterweightBeams - BeamOut);

        /// <summary>True while the beam is somewhere between two cells.</summary>
        public bool BeamMoving => System.Math.Abs(BeamOut - System.Math.Round(BeamOut)) > 0.0001;

        /// <summary>
        /// A run is bounded by the beam: forwards by what is left of the reach, backwards
        /// by how far it is already out. Fixed at the start of the run, because the shaft
        /// reversing mid-run must not change what the run was allowed to do.
        /// </summary>
        protected override int MaxRunSteps => runRetracting ? runStartExtension : Reach - runStartExtension;

        /// <summary>
        /// The machine's own beam is not an obstacle to its own load. Retracting pulls the
        /// load into cells the beam is currently occupying, and the beam gets out of the
        /// way itself as the run reports each completed cell.
        /// </summary>
        protected override bool IsPassable(Block block) => IsFree(block) || IsPistonBeam(block);

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

                RegisterGameTickListener(OnClientBeamTick, 20);
            }
        }

        /// <summary>
        /// Keeps the drawn beam moving on the client while the machine is pushing nothing.
        ///
        /// With a load there is a carrier, and both sides read the part-cell off it. With
        /// nothing in front there is no carrier and nothing synced, so the client runs the
        /// same sum against the same shaft speed. It is never allowed past the cell
        /// boundary: crossing one is the server's call, and its word arrives as a new
        /// extension, which is also what resets this.
        /// </summary>
        void OnClientBeamTick(float dt)
        {
            if (Running || Speed <= MinSpeed)
            {
                beamFraction = 0;
                return;
            }

            int sign = Reversed ? -1 : 1;
            if (!BeamCanDriveFreely(sign))
            {
                beamFraction = 0;
                return;
            }

            beamFraction = GameMath.Clamp(beamFraction + StepSpeed * dt * sign, -0.999, 0.999);
        }

        /// <summary>
        /// Can the beam move on its own, with no load to shove? Forwards that means reach
        /// left and empty air in front — anything actually there is cargo, and cargo means
        /// a carrier. Backwards it means room behind for the cell being drawn in.
        /// </summary>
        bool BeamCanDriveFreely(int sign)
        {
            if (Api?.World == null) return false;

            if (sign < 0) return extension > 0 && HasRoomBehind();
            if (extension >= Reach) return false;

            BlockPos next = BeamTip.AddCopy(PushFacing);
            IBlockAccessor ba = Api.World.BlockAccessor;

            if (ba.GetChunkAtBlockPos(next) == null) return false;
            return IsFree(ba.GetBlock(next));
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

        void DisposeRenderer()
        {
            if (headRenderer == null || Api is not ICoreClientAPI capi) return;

            capi.Event.UnregisterRenderer(headRenderer, EnumRenderStage.Opaque);
            headRenderer.Dispose();
            headRenderer = null;
        }

        public override void OnBlockUnloaded()
        {
            DisposeRenderer();
            base.OnBlockUnloaded();
        }

        public override void OnBlockRemoved()
        {
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
        /// Frees the cells the beam has left, without filling the ones it is heading for.
        ///
        /// The two ends want opposite timing. A cell the rod is moving into must stay empty
        /// until it gets there, or the block appears ahead of its own animation. A cell the
        /// rod is leaving must empty at once, or the block sits there unmoved. Continuous
        /// motion makes both halves fall out naturally: clear when the beam leaves a cell,
        /// fill when it finishes entering the next one, and OnStepCompleted is exactly the
        /// moment for the second.
        /// </summary>
        void ClearVacatedBeamCells()
        {
            SyncBeamBlocks(place: false, clear: true);
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

        protected override bool TryStartRun(float dt)
        {
            return Reversed ? TryRetract(dt) : TryExtend(dt);
        }

        /// <summary>
        /// The beam is part of the machine, not cargo: it drives out into empty air just
        /// as happily as against a load. So there are two cases, and only one of them
        /// needs a carrier.
        /// </summary>
        bool TryExtend(float dt)
        {
            if (extension >= Reach) return false;   // beam is already all the way out

            BlockFacing facing = PushFacing;
            IBlockAccessor ba = Api.World.BlockAccessor;

            BlockPos nextTip = BeamTip.AddCopy(facing);
            if (ba.GetChunkAtBlockPos(nextTip) == null) return false;

            if (IsFree(ba.GetBlock(nextTip)))
            {
                // Nothing to shove: drive the beam out under our own steam.
                DriveBeam(dt, +1);
                return false;
            }

            List<BlockPos> chain = CollectPushChain(ba, facing);
            if (chain == null) return false;

            // Anything glued to the chain comes along, so a piston can shove a
            // structure and not just the line of blocks directly ahead of it.
            List<BlockPos> group = ExpandThroughGlue(chain);
            if (group == null) return false;

            return BeginRun(group, facing, retracting: false);
        }

        bool TryRetract(float dt)
        {
            if (extension <= 0) return false;       // nothing to draw back in

            // Drawing in adds a cell of beam behind, so that cell has to be clear. Without
            // this the beam simply passed through whatever had been built there.
            if (!HasRoomBehind()) return false;

            BlockFacing facing = PushFacing;
            IBlockAccessor ba = Api.World.BlockAccessor;

            List<BlockPos> chain = CollectPullChain(ba, facing);

            if (chain == null)
            {
                // Drawing the beam back in works with nothing attached to it too: the
                // machine still has to return to rest before it can extend again.
                DriveBeam(dt, -1);
                return false;
            }

            List<BlockPos> group = ExpandThroughGlue(chain);
            if (group == null) return false;

            return BeginRun(group, facing.Opposite, retracting: true);
        }

        bool BeginRun(List<BlockPos> group, BlockFacing direction, bool retracting)
        {
            runStartExtension = extension;
            runRetracting = retracting;
            beamFraction = 0;

            if (StartMove(group, direction)) return true;

            runRetracting = false;
            return false;
        }

        /// <summary>
        /// Advances the beam by itself, for the runs that carry nothing. Whole cells are
        /// committed to the world as they are reached; the part-cell is what the renderer
        /// draws against.
        /// </summary>
        void DriveBeam(float dt, int sign)
        {
            double moved = StepSpeed * dt * sign;
            beamFraction += moved;

            while (beamFraction >= 1.0 && extension < Reach)
            {
                beamFraction -= 1.0;
                extension++;
                CommitExtension();
            }

            while (beamFraction < 0.0 && extension > 0)
            {
                beamFraction += 1.0;
                extension--;
                CommitExtension();
            }

            // Hard against an end stop: no part-cell to draw, the beam simply stays put.
            if (extension >= Reach && beamFraction > 0) beamFraction = 0;
            if (extension <= 0 && beamFraction < 0) beamFraction = 0;
        }

        void CommitExtension()
        {
            ClearVacatedBeamCells();
            SyncBeamBlocks();
            MarkDirty(true);
        }

        /// <summary>
        /// The load has finished crossing a cell, so the beam has too — they are the same
        /// motion. Restating the beam from the counters is what puts the arriving cell in
        /// place at the moment it is arrived at.
        /// </summary>
        protected override void OnStepCompleted(int steps)
        {
            extension = runRetracting ? runStartExtension - steps : runStartExtension + steps;
            CommitExtension();
        }

        protected override void OnRunEnded()
        {
            beamFraction = 0;
            runRetracting = false;
            CommitExtension();
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


            beams = tree.GetInt("beams");

            // A cell boundary the server has crossed on our behalf: whatever part-cell the
            // client had drawn its way to is now spent.
            int wasExtension = extension;
            extension = tree.GetInt("extension");
            if (extension != wasExtension) beamFraction = 0;

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
