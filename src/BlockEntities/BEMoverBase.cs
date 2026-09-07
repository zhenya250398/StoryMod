using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Mechworks
{
    /// <summary>
    /// Shared machinery for anything that turns vanilla rotation into moved blocks:
    /// reading the network, lifting a set of cells into an
    /// <see cref="EntityMovingBlocks"/>, and driving that carrier for as long as the
    /// shaft turns.
    ///
    /// A run is not a stroke. The carrier is created once and held for the whole run, so
    /// a powered machine moves its load without ever putting it back down — no charge, no
    /// pause, no cell-by-cell round trip through the block grid. The machine's job each
    /// tick is to tell the carrier how fast to go and how far ahead has been checked.
    ///
    /// Subclasses decide *what* moves and *where* — see <see cref="TryStartRun"/>.
    /// </summary>
    public abstract class BEMoverBase : BlockEntity
    {
        /// <summary>
        /// How much accumulated rotation one stroke costs. Lower means the machine fires
        /// more often at the same shaft speed. Per machine, since a piston nudging a block
        /// along and a hoist hauling a load are not the same amount of work.
        /// </summary>
        public virtual float RevolutionsPerStroke => 1f;

        /// <summary>Below this the network counts as stopped.</summary>
        protected const float MinSpeed = 0.001f;

        /// <summary>
        /// Fast, because this tick is what keeps the checked frontier ahead of a moving
        /// load. At a quarter second the load could cross more than a cell between ticks
        /// and run into ground nobody had looked at.
        /// </summary>
        const int TickIntervalMs = 50;

        /// <summary>
        /// How many whole steps of clear ground to keep ahead of the load. One would do if
        /// ticks were instant; two leaves room for a tick's worth of travel so the load
        /// never has to wait at the frontier for the lookahead to catch up.
        /// </summary>
        const int Lookahead = 2;

        /// <summary>
        /// Temporary measurement: is the sign of the network's rotation stable for a given
        /// machine across chunk loads and world reloads?
        ///
        /// It matters because BEBehaviorMPBase restores propagationDir from the save on the
        /// CLIENT only — the server rediscovers networks on chunk load, seeded by whichever
        /// block entity initialises first. Whether that flips the sign seen here is the
        /// open question.
        /// </summary>
        static readonly bool DebugRotation = false;

        int loggedSign;

        BEBehaviorMPConsumer mpConsumer;

        /// <summary>The run in progress, server side. Null when the machine is at rest.</summary>
        EntityMovingBlocks carrier;

        /// <summary>Layout of the load, relative to the cell the run started from.</summary>
        List<Vec3i> runOffsets;
        BlockPos runOrigin;
        BlockFacing runTravel;
        BlockFacing runAxis = BlockFacing.UP;
        int runStepAngle;

        /// <summary>Highest step checked and found clear.</summary>
        int clearedSteps;

        /// <summary>
        /// Which way round the machine was when this run started. A run is committed to
        /// its direction — the cells it checks and the limit it obeys are both worked out
        /// from it — so reversing is not something a run can absorb. It ends the run
        /// instead, and the next one goes the other way.
        /// </summary>
        bool runReversed;

        /// <summary>
        /// Id of the carrier, synced so the client can find it. The client draws the
        /// machine's own moving parts against the load, and only the carrier knows how far
        /// through a cell the load actually is.
        /// </summary>
        long carrierId;

        /// <summary>Current network speed at this block, 0 when unpowered.</summary>
        public float Speed => mpConsumer?.TrueSpeed ?? 0f;

        /// <summary>
        /// Which way the shaft is visibly turning at THIS block.
        ///
        /// Not simply the sign of the network speed: that is one value shared by the whole
        /// network, so a gear arrangement that reverses rotation halfway along does not
        /// change it. What changes is the direction power arrives from, which the game
        /// keeps per node as propagationDir and reads back through IsRotationReversed().
        ///
        /// This is the same pair of terms BEBehaviorMPBase.AngleRad uses to draw the
        /// rotation, so the machine always does what the player can see the shaft doing.
        /// </summary>
        public bool RotationReversed
        {
            get
            {
                MechanicalNetwork net = mpConsumer?.Network;
                if (net == null) return false;
                return (net.Speed < 0f) ^ mpConsumer.IsRotationReversed();
            }
        }

        /// <summary>Manual override on top of the rotation sign. Debug aid for now.</summary>
        public bool Inverted { get; private set; }

        /// <summary>
        /// True when the machine should run its stroke backwards: either the shaft turns
        /// the other way, or the player has flipped it by hand.
        /// </summary>
        public bool Reversed => RotationReversed ^ Inverted;

        public void ToggleInverted()
        {
            Inverted = !Inverted;
            MarkDirty(true);
        }

        /// <summary>
        /// The carrier of the run in progress, on either side. The server holds it
        /// directly; the client looks it up by the synced id, which is how the machine's
        /// own animated parts stay glued to the load they are pushing.
        /// </summary>
        public EntityMovingBlocks Carrier
        {
            get
            {
                if (carrier != null && carrier.Alive) return carrier;
                if (Api?.World == null || carrierId == 0) return null;

                return Api.World.GetEntityById(carrierId) as EntityMovingBlocks;
            }
        }

        /// <summary>True while a load is off the grid and moving.</summary>
        public bool Running => Carrier != null;

        /// <summary>
        /// How far the current run has got, in whole cells plus a fraction. Zero at rest.
        /// </summary>
        public double RunProgress => Carrier?.Progress ?? 0;

        /// <summary>Steps per second at the current shaft speed.</summary>
        public float StepSpeed
        {
            get
            {
                float per = RevolutionsPerStroke;
                return per <= 0f ? 0f : Speed / per;
            }
        }

        /// <summary>Word for one stroke in the block info readout, e.g. "push" or "lift".</summary>
        protected virtual string StrokeNoun => "move";

        /// <summary>
        /// Runs on the server when a powered machine has no run going. Should work out
        /// which cells move and call <see cref="StartMove"/> or <see cref="StartTurn"/>.
        /// Returning false just means there was nothing to do this tick.
        /// </summary>
        /// <param name="dt">
        /// Seconds since the last tick. A machine with moving parts of its own — the
        /// piston and its beam — has to keep them going even on the ticks where there is
        /// no load to pick up, and this is the only tick it gets while idle.
        /// </param>
        protected abstract bool TryStartRun(float dt);

        /// <summary>
        /// How many steps this machine will allow in one run, before anything in the way
        /// is considered. A piston is bounded by its beam, a hoist by its rope; a turntable
        /// has no such limit and turns until the power stops.
        /// </summary>
        protected virtual int MaxRunSteps => int.MaxValue;

        /// <summary>
        /// A cell the load may pass into. Free by default; the piston widens this to
        /// include its own beam, which it moves out of the way itself.
        /// </summary>
        protected virtual bool IsPassable(Block block) => IsFree(block);

        /// <summary>
        /// Called on the server whenever the load has fully entered a new cell, with the
        /// number of whole steps completed. Machines with parts of their own to keep in
        /// step — the piston and its beam — do it here.
        /// </summary>
        protected virtual void OnStepCompleted(int steps)
        {
        }

        /// <summary>
        /// Called on the server once a run is over and the blocks are back in the grid.
        /// </summary>
        protected virtual void OnRunEnded()
        {
        }

        /// <summary>
        /// Called on the server every tick a run is going. For machines whose own parts
        /// move continuously with the load rather than a cell at a time.
        /// </summary>
        protected virtual void OnRunTick(float dt)
        {
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            mpConsumer = GetBehavior<BEBehaviorMPConsumer>();

            // Both sides tick. The server owns the decision to move; the client runs the
            // same accumulator purely so the block info readout moves in real time —
            // syncing progress every tick would be a packet per machine per 250ms.
            RegisterGameTickListener(OnTick, TickIntervalMs);
        }

        /// <summary>
        /// Drives the run. Everything here is server side: the client has nothing to
        /// decide, it watches the carrier the server gave it.
        /// </summary>
        void OnTick(float dt)
        {
            if (DebugRotation) LogRotationIfChanged();
            if (Api.Side != EnumAppSide.Server) return;

            bool powered = Speed > MinSpeed;

            // A run that outlived a reload. The layout it was moving is not saved, so it
            // cannot be taken back over — but it must not be built on top of either. Its
            // own watchdog puts it down; wait for that rather than starting a second run
            // over a load that is still off the grid.
            if (carrier == null && carrierId != 0)
            {
                if (Api.World.GetEntityById(carrierId) is EntityMovingBlocks orphan && orphan.Alive) return;

                carrierId = 0;
                MarkDirty(true);
            }

            // The carrier puts the blocks back itself when it settles, so noticing it has
            // gone is all the tidying up there is.
            if (carrier != null && !carrier.Alive) EndRun();

            if (carrier == null)
            {
                if (powered) TryStartRun(dt);
                return;
            }

            // Before anything else, and on every tick including the one the power dies on.
            // A settling load can still finish crossing a cell, and a machine that missed
            // being told would leave its own parts a cell behind the blocks.
            ReportCompletedSteps();
            OnRunTick(dt);

            // Out of power, or turned round under us: come to rest on whichever grid
            // position is nearest right now. For a reversal that is also what makes the
            // machine answer at once instead of finishing the cell it was in the middle
            // of — the load settles, and the next tick starts a run the other way.
            if (!powered || Reversed != runReversed)
            {
                carrier.StopAtNearest();
                return;
            }

            bool blocked = ExtendFrontier();

            // Rounded, because the shaft speed wobbles in the last decimal and Drive
            // compares against what it last sent to decide whether to send anything at all.
            // Unrounded it would count as changed on every one of twenty ticks a second.
            double speed = System.Math.Round(StepSpeed, 3);

            carrier.Drive(speed, clearedSteps, blocked);
        }

        int reportedSteps;

        /// <summary>
        /// Tells the machine about every whole cell the load has finished crossing, one at
        /// a time and in order, so nothing is skipped if a tick was long.
        /// </summary>
        void ReportCompletedSteps()
        {
            if (carrier == null) return;

            int done = (int)System.Math.Floor(carrier.Progress);
            while (reportedSteps < done)
            {
                reportedSteps++;
                OnStepCompleted(reportedSteps);
            }
        }

        /// <summary>
        /// Pushes the checked frontier far enough ahead of the load, and reports whether it
        /// could not be pushed as far as wanted — which is what tells the carrier this run
        /// is going to end where the frontier now is.
        /// </summary>
        bool ExtendFrontier()
        {
            while (clearedSteps < carrier.Progress + Lookahead)
            {
                if (clearedSteps >= MaxRunSteps) return true;
                if (!LoadFitsAt(clearedSteps + 1)) return true;

                clearedSteps++;
            }

            return false;
        }

        /// <summary>
        /// Would the load sit legally if it had taken this many steps?
        ///
        /// Every cell of it is tested, not just the leading face. The load is out of the
        /// grid while it runs, so the cells it came from read as air and there is nothing
        /// to subtract — the same reason a solid group can shuffle along at all.
        /// </summary>
        bool LoadFitsAt(int step, HashSet<BlockPos> ignore = null)
        {
            if (runOffsets == null || runOrigin == null) return false;

            IBlockAccessor ba = Api.World.BlockAccessor;
            Vec3i dir = runTravel?.Normali ?? new Vec3i(0, 0, 0);
            int angle = GameMath.Mod(runStepAngle * step, 360);

            foreach (Vec3i offset in runOffsets)
            {
                Vec3i turned = BlockSnapshot.Rotate(offset, runAxis, angle);

                BlockPos pos = runOrigin.AddCopy(
                    turned.X + dir.X * step,
                    turned.Y + dir.Y * step,
                    turned.Z + dir.Z * step);

                if (ignore != null && ignore.Contains(pos)) continue;
                if (ba.GetChunkAtBlockPos(pos) == null) return false;
                if (!IsPassable(ba.GetBlock(pos))) return false;
            }

            return true;
        }

        /// <summary>
        /// Ends the run because the machine itself is going away — broken, or its chunk
        /// unloaded. The carrier does not need us after this: it settles on the nearest
        /// whole step by itself and puts the blocks back.
        ///
        /// Without this the load simply stayed in the air. Nothing but the machine ever
        /// declares a run finished, so breaking one left its blocks drawn but absent from
        /// the world: impossible to break, and new blocks could be built straight through
        /// them.
        /// </summary>
        void AbandonRun()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            if (carrier == null || !carrier.Alive) return;

            carrier.StopAtNearest();
        }

        public override void OnBlockRemoved()
        {
            AbandonRun();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            AbandonRun();
            base.OnBlockUnloaded();
        }

        void EndRun()
        {
            // The carrier may have settled and died between two of our ticks, taking its
            // last cell with it. Its fields outlive the entity, so the tally is still
            // there to be read and the machine's own parts end up where the blocks did.
            ReportCompletedSteps();
            OnRunEnded();

            carrier = null;
            carrierId = 0;
            runOffsets = null;
            runOrigin = null;
            runTravel = null;
            runStepAngle = 0;
            clearedSteps = 0;
            reportedSteps = 0;
            MarkDirty(true);
        }

        /// <summary>
        /// Logs the signed local rotation the first time it is seen and every time it
        /// flips. One line per world load per machine, so a reload that changes the sign
        /// is immediately visible in the log.
        /// </summary>
        void LogRotationIfChanged()
        {
            if (Api.Side != EnumAppSide.Server || mpConsumer == null) return;

            MechanicalNetwork net = mpConsumer.Network;
            if (net == null) return;

            float geared = mpConsumer.GearedRatio;
            float local = net.Speed * geared;
            if (System.Math.Abs(local) <= MinSpeed) return;   // stopped: no sign to report

            int sign = local < 0 ? -1 : 1;
            if (sign == loggedSign) return;

            string moment = loggedSign == 0 ? "first" : "FLIPPED";
            loggedSign = sign;

            Api.Logger.Notification(
                "[mechworks] rot {0} pos={1} netId={2} netSpeed={3:0.#####} geared={4:0.###} local={5:0.#####} turnDir={6}",
                moment, Pos, mpConsumer.NetworkId, net.Speed, geared, local, net.TurnDir);
        }

        /// <summary>
        /// Draws the block, which nothing else will.
        ///
        /// BEBehaviorMPConsumer.OnTesselation returns true without adding a mesh — it is
        /// claiming the block is rendered by the mechanical network's own renderer, which
        /// is true for vanilla machines. We opt out of that renderer with
        /// mechPartShape: null, so without this the block is simply never drawn: solid,
        /// selectable, invisible.
        /// </summary>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            return TesselateSelf(mesher, tessThreadTesselator, null);
        }

        /// <summary>
        /// Builds this block's own mesh from its shape, optionally leaving out elements a
        /// renderer draws separately. Returns false when there is nothing to draw, which
        /// lets the normal block rendering have its turn.
        /// </summary>
        protected bool TesselateSelf(ITerrainMeshPool mesher, ITesselatorAPI tess, string[] excludedElements)
        {
            if (Api is not ICoreClientAPI capi) return false;

            CompositeShape cshape = Block?.Shape;
            if (cshape?.Base == null) return false;

            AssetLocation loc = cshape.Base.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");

            Shape shape = Shape.TryGet(capi, loc);
            if (shape == null) return false;

            if (excludedElements != null && excludedElements.Length > 0)
            {
                shape = shape.Clone();
                shape.RemoveElements(excludedElements);
            }

            tess.TesselateShape(Block, shape, out MeshData mesh,
                new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ));

            if (mesh == null) return false;

            mesher.AddMeshData(mesh);
            return true;
        }

        /// <summary>Largest group one stroke may move, glue included.</summary>
        public const int MaxGroupSize = 64;

        protected GlueRegistry Glue => Api.ModLoader.GetModSystem<MechworksModSystem>()?.Glue;

        /// <summary>Blocks that may carry a glue mark at all.</summary>
        public static bool CanBeGlued(Block block)
        {
            return IsMovable(block);
        }

        /// <summary>
        /// Grows a set of seed cells outwards through glue. A cell only spreads to a
        /// neighbour when *both* are glued, so ungluing either side breaks the joint and
        /// an unglued seed behaves exactly as it did before glue existed.
        ///
        /// Returns null if the group would exceed <see cref="MaxGroupSize"/> — better to
        /// stall the machine than to rip out half a build.
        /// </summary>
        protected List<BlockPos> ExpandThroughGlue(IList<BlockPos> seeds)
        {
            GlueRegistry glue = Glue;
            if (glue == null) return new List<BlockPos>(seeds);

            IBlockAccessor ba = Api.World.BlockAccessor;
            List<BlockPos> group = new List<BlockPos>(seeds);
            HashSet<BlockPos> known = new HashSet<BlockPos>(seeds);
            Queue<BlockPos> open = new Queue<BlockPos>(seeds);

            while (open.Count > 0)
            {
                BlockPos cur = open.Dequeue();
                if (!glue.IsGlued(cur)) continue;   // unglued members do not spread

                foreach (BlockFacing face in BlockFacing.ALLFACES)
                {
                    BlockPos next = cur.AddCopy(face);
                    if (known.Contains(next)) continue;
                    if (ba.GetChunkAtBlockPos(next) == null) return null;
                    if (!glue.IsGlued(next)) continue;

                    // A mark whose block is gone must not jam the machine — drop it and
                    // carry on. Only a real block that cannot move is a reason to refuse.
                    if (glue.PruneIfStale(ba, next)) continue;

                    Block block = ba.GetBlock(next);
                    if (!IsMovable(block)) return null;   // glued to something immovable
                    if (group.Count >= MaxGroupSize) return null;

                    known.Add(next);
                    group.Add(next);
                    open.Enqueue(next);
                }
            }

            return group;
        }

        /// <summary>
        /// Lifts the given cells and hands them to a carrier that turns them about the
        /// given axis through this block, a step at a time, for as long as the run lasts.
        /// </summary>
        protected bool StartTurn(IList<BlockPos> cells, BlockFacing axis, int stepAngle)
        {
            return StartRun(cells, Pos, null, axis, stepAngle);
        }

        /// <summary>
        /// Lifts the given cells out of the grid and hands them to a carrier that runs them
        /// along <paramref name="direction"/> one cell per step.
        /// </summary>
        protected bool StartMove(IList<BlockPos> cells, BlockFacing direction)
        {
            if (cells == null || cells.Count == 0) return false;
            return StartRun(cells, cells[0], direction, null, 0);
        }

        /// <summary>
        /// Starts a run: records the layout, checks that the first step is actually
        /// possible, then lifts the blocks and spawns the carrier that owns them until the
        /// run ends.
        ///
        /// From here until the carrier settles these blocks exist only inside the snapshot,
        /// which is why EntityMovingBlocks puts them back even when it dies unexpectedly.
        /// </summary>
        bool StartRun(IList<BlockPos> cells, BlockPos origin, BlockFacing travel, BlockFacing axis, int stepAngle)
        {
            if (cells == null || cells.Count == 0) return false;
            if (MaxRunSteps < 1) return false;

            IBlockAccessor ba = Api.World.BlockAccessor;

            runOrigin = origin.Copy();
            runTravel = travel;
            runAxis = axis ?? BlockFacing.UP;
            runStepAngle = stepAngle;
            runOffsets = new List<Vec3i>(cells.Count);

            foreach (BlockPos cell in cells)
            {
                runOffsets.Add(new Vec3i(
                    cell.X - runOrigin.X,
                    cell.InternalY - runOrigin.InternalY,
                    cell.Z - runOrigin.Z));
            }

            // The load is still in the grid at this point, so its own cells have to be
            // discounted — that is what lets a solid group shuffle along at all. Once it is
            // lifted they read as air and no such exception is needed.
            if (!LoadFitsAt(1, new HashSet<BlockPos>(cells)))
            {
                runOffsets = null;
                runOrigin = null;
                return false;
            }

            EntityProperties type = Api.World.GetEntityType(new AssetLocation("mechworks", "movingblocks"));
            if (type == null) return false;
            if (Api.World.ClassRegistry.CreateEntity(type) is not EntityMovingBlocks fresh) return false;

            BlockSnapshot snapshot = BlockSnapshot.Capture(ba, cells, runOrigin);

            // Glue marks travel with the blocks. Lift them here; the carrier puts them
            // back wherever it puts the blocks down, including an emergency landing.
            GlueRegistry glue = Glue;
            if (glue != null)
            {
                snapshot.Glued = new bool[cells.Count];
                for (int i = 0; i < cells.Count; i++)
                {
                    snapshot.Glued[i] = glue.IsGlued(cells[i]);
                    if (snapshot.Glued[i]) glue.Remove(cells[i]);
                }
            }

            snapshot.ClearFromWorld(ba, runOrigin);

            fresh.Configure(snapshot, runOrigin, travel, runAxis, stepAngle);
            fresh.Drive(StepSpeed, 1, false);
            fresh.Pos.SetPos(runOrigin.X, runOrigin.InternalY, runOrigin.Z);

            Api.World.SpawnEntity(fresh);

            carrier = fresh;
            carrierId = fresh.EntityId;
            clearedSteps = 1;
            reportedSteps = 0;
            runReversed = Reversed;

            MarkDirty(true);
            return true;
        }

        /// <summary>A block this machine is allowed to pick up.</summary>
        protected static bool IsMovable(Block block)
        {
            if (block == null || block.Id == 0) return false;   // nothing there
            if (block.IsLiquid()) return false;
            if (block.Attributes?["mechworksImmovable"].AsBool(false) == true) return false;
            if (block.Code?.Path != null && block.Code.Path.StartsWith("bedrock", StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>A cell blocks can be moved into.</summary>
        protected static bool IsFree(Block block)
        {
            return block == null || block.Id == 0 || block.IsLiquid();
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetLong("carrierId", carrierId);
            tree.SetBool("inverted", Inverted);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            carrierId = tree.GetLong("carrierId");
            Inverted = tree.GetBool("inverted");
        }

        public override void GetBlockInfo(IPlayer forPlayer, System.Text.StringBuilder sb)
        {
            base.GetBlockInfo(forPlayer, sb);

            float speed = Speed;
            if (speed <= MinSpeed)
            {
                sb.AppendLine("Not powered");
                return;
            }

            sb.AppendLine(string.Format("Rotation: {0}{1}",
                RotationReversed ? "reversed" : "forward",
                Inverted ? " (flipped by hand)" : ""));
            sb.AppendLine(string.Format("Speed: {0:0.###}/s", speed));

            // Steps per second, and its reciprocal, which is the more useful of the two
            // when watching a machine crawl.
            float steps = StepSpeed;
            sb.AppendLine(steps <= 0f
                ? "Stalled"
                : string.Format("One {0} every {1:0.##}s", StrokeNoun, 1f / steps));

            sb.AppendLine(Running
                ? string.Format("Running: {0:0.##} {1}s so far", RunProgress, StrokeNoun)
                : "Idle");
        }
    }
}
