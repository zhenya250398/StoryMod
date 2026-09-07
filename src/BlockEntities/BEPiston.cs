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
        /// How far the beam is driven out, in cells, while the machine is pushing nothing.
        /// With nothing in front there is no load and so no carrier, and the beam still has
        /// to come out smoothly rather than jump a cell at a time.
        ///
        /// Absolute rather than a fraction of the current cell. A fraction has to be reset
        /// every time the whole count changes, and the whole count reaches the client in
        /// its own packet — so the reset lands before the news does and the beam jumps.
        /// An absolute number is simply clamped into the cell the server says we are in,
        /// and carries on from wherever it was.
        /// </summary>
        double beamOut;

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
                if (!Running) return beamOut;

                // Measured from where the run began, not from the whole-cell count. Those
                // are two numbers that reach the client separately — the count in the
                // machine's own packet, the progress computed here from the carrier — and
                // combining them means every cell boundary is a moment when one has
                // arrived and the other has not. The beam jumped a whole cell back and the
                // trailing end stuck out by the same amount. Where the run started never
                // changes, so there is nothing to race.
                return runRetracting ? runStartExtension - RunProgress : runStartExtension + RunProgress;
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
            }

            // Both sides, and faster than the machine's own tick, because this is the only
            // thing keeping the beam moving when there is no load — and the only thing that
            // parks it on the grid when the power goes. The machine tick cannot do it: it
            // stops being called at all once there is no power and no run.
            RegisterGameTickListener(TickBeam, 20);
        }

        /// <summary>
        /// How fast a free beam eases onto the nearest whole cell once nothing is driving
        /// it, in cells per second. Same idea as the carrier's settle: never at the speed
        /// of the shaft that has just stopped.
        /// </summary>
        const double BeamSettleSpeed = 3.0;

        /// <summary>
        /// Moves the beam when there is no load to move it with, on both sides.
        ///
        /// With a load there is a carrier and the beam simply reads its position — beam
        /// and load are the same motion. With nothing in front there is no carrier and
        /// nothing synced, so both sides run the same sum against the same shaft speed.
        ///
        /// The important half is what happens when it is *not* being driven: the beam
        /// eases onto the nearest whole cell, exactly as a carried load does. Leaving it
        /// parked mid-cell was two visible faults at once. The cells the rod covers are
        /// counted with a floor, so a rod stopped at three-and-a-bit covers one cell fewer
        /// behind than it should — a beam block missing at the back for as long as it sat
        /// there. And the client, which used to snap its drawing back to the whole count
        /// when the power went, then disagreed with the server by most of a cell; when the
        /// power came back the server crossed the boundary at once and the drawn beam
        /// jumped a whole cell to catch up.
        /// </summary>
        void TickBeam(float dt)
        {
            // A run owns the position outright; keep our own copy level with it so there
            // is nothing to jump when the run ends.
            if (Running)
            {
                beamOut = BeamOut;
                return;
            }

            int sign = Reversed ? -1 : 1;

            if (Speed > MinSpeed && BeamCanDriveFreely(sign))
            {
                beamOut += StepSpeed * dt * sign;
            }
            else
            {
                double target = System.Math.Round(beamOut, System.MidpointRounding.AwayFromZero);
                double step = BeamSettleSpeed * dt;

                beamOut = beamOut < target
                    ? System.Math.Min(target, beamOut + step)
                    : System.Math.Max(target, beamOut - step);
            }

            beamOut = GameMath.Clamp(beamOut, 0, Reach);

            if (Api.Side == EnumAppSide.Server)
            {
                UpdateExtensionFromBeamOut();
                SyncBeamBlocksIfMoved();
                return;
            }

            // Never out of the cell the server says the beam is in. Crossing one is the
            // server's call and arrives as a new extension; until it does, the drawn beam
            // creeps up to the boundary and waits there.
            beamOut = GameMath.Clamp(beamOut, extension, extension + 0.999);
        }

        void UpdateExtensionFromBeamOut()
        {
            while (beamOut >= extension + 1 && extension < Reach)
            {
                extension++;
                MarkDirty(true);
            }

            while (beamOut < extension && extension > 0)
            {
                extension--;
                MarkDirty(true);
            }
        }

        /// <summary>
        /// Can the beam move on its own, with no load to shove? Forwards that means reach
        /// left and empty air in front — anything actually there is cargo, and cargo means
        /// a carrier. Backwards it means room behind for the cell being drawn in.
        /// </summary>
        bool BeamCanDriveFreely(int sign)
        {
            if (Api?.World == null) return false;

            // Gated on the continuous position, not the whole-cell count. The count is a
            // floor, so it drops the moment the rod leaves a cell rather than when it
            // arrives at the next one — gating on it stopped the drive a hair into the
            // last cell, whereupon the beam eased back to the cell it had just left. The
            // piston could never draw its final cell in; it bounced there forever.
            if (sign < 0) return beamOut > 0 && HasRoomToDrawBack();
            if (beamOut >= Reach) return false;

            BlockPos next = BeamTip.AddCopy(PushFacing);
            IBlockAccessor ba = Api.World.BlockAccessor;

            if (ba.GetChunkAtBlockPos(next) == null) return false;
            return IsFree(ba.GetBlock(next));
        }

        /// <summary>
        /// Is the cell the rod's rear tip is drawing back into clear?
        ///
        /// Not the same question as <see cref="HasRoomBehind"/>, which asks whether a
        /// *further* beam would fit and is about the machine's load-out. This one is about
        /// where the rod is right now, so it counts from the cells the rod actually covers,
        /// and it stops asking once the whole rod is inside — a fully drawn-in piston needs
        /// nothing beyond its own trailing length.
        /// </summary>
        bool HasRoomToDrawBack()
        {
            int next = BackBeamCells + 1;
            if (next > RodCells) return true;

            BlockPos at = Pos.AddCopy(PushFacing.Opposite, next);
            IBlockAccessor ba = Api.World.BlockAccessor;

            if (ba.GetChunkAtBlockPos(at) == null) return false;

            Block current = ba.GetBlock(at);
            return IsFree(current) || IsPistonBeam(current);
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
            beamOut = 0;
            SyncBeamBlocks();
            base.OnBlockRemoved();
        }

        /// <summary>
        /// Length of the extendable part of the rod, in cells. The counterweight never
        /// leaves the machine, so it is not part of it.
        /// </summary>
        int RodCells => System.Math.Max(0, beams - CounterweightBeams);

        /// <summary>
        /// Cells of beam behind, at rest. Used when asking whether another beam will fit,
        /// which is a question about the machine's load-out and not about where the rod
        /// happens to be sliding right now.
        /// </summary>
        public int BackBeams => System.Math.Max(0, RodCells - extension);

        /// <summary>
        /// Cells the rod covers *completely* in front and behind, right now.
        ///
        /// The rod is one rigid bar running from BeamOut - RodCells to BeamOut, and these
        /// are the whole cells inside it. A cell it is halfway out of belongs to neither:
        /// the renderer draws that end.
        ///
        /// The old "clear the cell being left at once, fill the cell being entered only on
        /// arrival" rule is not a rule any more, it just falls out of this. The back count
        /// drops the instant the rod leaves an integer; the front count rises only when it
        /// reaches the next one. Losing that asymmetry is what left a whole extra block
        /// sticking out the back for the length of every cell.
        /// </summary>
        int FrontBeamCells => GameMath.Clamp((int)System.Math.Floor(BeamOut), 0, RodCells);

        int BackBeamCells => System.Math.Max(0, (int)System.Math.Floor(RodCells - BeamOut));

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
            int front = FrontBeamCells;
            int back = BackBeamCells;

            for (int i = 1; i <= MaxBeams; i++)
            {
                SetBeamCell(ba, beam, Pos.AddCopy(facing, i), i <= front, place, clear);
                SetBeamCell(ba, beam, Pos.AddCopy(facing.Opposite, i), i <= back, place, clear);
            }

            // Recorded here rather than by the caller, so loading a beam or breaking the
            // machine leaves the record straight too. Otherwise the next tick can find a
            // stale pair that happens to match and skip a write that was needed.
            syncedFront = front;
            syncedBack = back;
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
            return Reversed ? TryRetract() : TryExtend();
        }

        /// <summary>
        /// The beam is part of the machine, not cargo: it drives out into empty air just
        /// as happily as against a load. So there are two cases, and only one of them
        /// needs a carrier.
        /// </summary>
        bool TryExtend()
        {
            if (extension >= Reach) return false;   // beam is already all the way out

            BlockFacing facing = PushFacing;
            IBlockAccessor ba = Api.World.BlockAccessor;

            BlockPos nextTip = BeamTip.AddCopy(facing);
            if (ba.GetChunkAtBlockPos(nextTip) == null) return false;

            // Nothing to shove: no run, and TickBeam drives the bare beam out.
            if (IsFree(ba.GetBlock(nextTip))) return false;

            List<BlockPos> chain = CollectPushChain(ba, facing);
            if (chain == null) return false;

            // Anything glued to the chain comes along, so a piston can shove a
            // structure and not just the line of blocks directly ahead of it.
            List<BlockPos> group = ExpandThroughGlue(chain);
            if (group == null) return false;

            return BeginRun(group, facing, retracting: false);
        }

        bool TryRetract()
        {
            if (extension <= 0) return false;       // nothing to draw back in

            // Drawing in adds a cell of beam behind, so that cell has to be clear. Without
            // this the beam simply passed through whatever had been built there.
            if (!HasRoomToDrawBack()) return false;

            BlockFacing facing = PushFacing;
            IBlockAccessor ba = Api.World.BlockAccessor;

            List<BlockPos> chain = CollectPullChain(ba, facing);

            // Drawing the beam back in works with nothing attached to it too: TickBeam
            // does that, and the machine still has to return to rest before it can
            // extend again.
            if (chain == null) return false;

            List<BlockPos> group = ExpandThroughGlue(chain);
            if (group == null) return false;

            return BeginRun(group, facing.Opposite, retracting: true);
        }

        bool BeginRun(List<BlockPos> group, BlockFacing direction, bool retracting)
        {
            runStartExtension = extension;
            runRetracting = retracting;
            beamOut = extension;

            if (StartMove(group, direction)) return true;

            runRetracting = false;
            return false;
        }

        int syncedFront = -1;
        int syncedBack = -1;

        /// <summary>
        /// Restates the rod in the world, but only when the cells it fully covers have
        /// actually changed. Called every tick of a run, so the cheap check matters.
        /// </summary>
        void SyncBeamBlocksIfMoved()
        {
            if (FrontBeamCells == syncedFront && BackBeamCells == syncedBack) return;

            SyncBeamBlocks();
            MarkDirty(true);
        }

        protected override void OnRunTick(float dt)
        {
            SyncBeamBlocksIfMoved();
        }

        /// <summary>
        /// The load has finished crossing a cell, so the beam has too — they are the same
        /// motion. Restating the beam from the counters is what puts the arriving cell in
        /// place at the moment it is arrived at.
        /// </summary>
        protected override void OnStepCompleted(int steps)
        {
            extension = runRetracting ? runStartExtension - steps : runStartExtension + steps;
            SyncBeamBlocksIfMoved();
            MarkDirty(true);
        }

        protected override void OnRunEnded()
        {
            beamOut = extension;
            runRetracting = false;
            SyncBeamBlocksIfMoved();
            MarkDirty(true);
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
            // The load rides on the beam tip, so it comes back to where the tip is now —
            // a cell that holds a beam block, this machine's own. It has to be read as
            // available or the piston can never pull anything: the old code cleared it out
            // of the world first and put it back if the stroke was refused. Saying so here
            // is the same answer without editing the world to ask a question.
            BlockPos landing = BeamTip.Copy();
            if (ba.GetChunkAtBlockPos(landing) == null) return null;

            Block at = ba.GetBlock(landing);
            if (!IsFree(at) && !IsPistonBeam(at)) return null;   // nothing to pull it into

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
            tree.SetInt("runStartExtension", runStartExtension);
            tree.SetBool("runRetracting", runRetracting);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);


            beams = tree.GetInt("beams");

            extension = tree.GetInt("extension");
            runStartExtension = tree.GetInt("runStartExtension");
            runRetracting = tree.GetBool("runRetracting");

            // The whole-cell count moved under us. The drawn beam does not jump to meet
            // it — it is already somewhere sensible, and only needs to be inside the cell
            // the server now says we are in.
            beamOut = GameMath.Clamp(beamOut, extension, extension + 0.999);

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
