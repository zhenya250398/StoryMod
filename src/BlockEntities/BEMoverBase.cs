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
    /// reading the network, accumulating a stroke, and handing a set of cells to an
    /// <see cref="EntityMovingBlocks"/> to fly across.
    ///
    /// Subclasses decide *what* moves and *where* — see <see cref="TryMove"/>.
    /// </summary>
    public abstract class BEMoverBase : BlockEntity
    {
        /// <summary>
        /// How much accumulated rotation one stroke costs. Lower means the machine fires
        /// more often at the same shaft speed. Per machine, since a piston nudging a block
        /// along and a hoist hauling a load are not the same amount of work.
        /// </summary>
        public virtual float RevolutionsPerStroke => 1f;

        /// <summary>How long blocks spend in the air between the two cells.</summary>
        public const float MoveDurationSec = 0.4f;

        /// <summary>Below this the network counts as stopped.</summary>
        protected const float MinSpeed = 0.001f;

        const int TickIntervalMs = 250;

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

        float progress;

        /// <summary>
        /// When the current stroke began, in world milliseconds; -1 when at rest.
        ///
        /// Deliberately a timestamp rather than a countdown ticked down in OnTick: the tick
        /// runs four times a second, so a countdown only knows about two moments inside a
        /// 0.4s stroke. The carried blocks are interpolated every frame by their carrier
        /// entity, and anything drawn from a coarse counter visibly lags behind them.
        /// </summary>
        long strokeStartMs = -1;

        BEBehaviorMPConsumer mpConsumer;

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

        /// <summary>True while a stroke is in the air.</summary>
        public bool Stroking => strokeStartMs >= 0 && StrokeElapsed() < MoveDurationSec;

        /// <summary>How far through the current stroke, 0 at the start and 1 at the end.</summary>
        public float StrokeProgress =>
            strokeStartMs < 0 ? 0f : GameMath.Clamp(StrokeElapsed() / MoveDurationSec, 0f, 1f);

        float StrokeElapsed()
        {
            return (Api.World.ElapsedMilliseconds - strokeStartMs) / 1000f;
        }

        /// <summary>Starts the visual stroke from now.</summary>
        protected void BeginStroke()
        {
            if (Api?.World == null) return;
            strokeStartMs = Api.World.ElapsedMilliseconds;
        }

        /// <summary>Seconds until the next stroke at the current speed, 0 when unpowered.</summary>
        public float SecondsToNextStroke
        {
            get
            {
                float speed = Speed;
                return speed <= MinSpeed ? 0f : (RevolutionsPerStroke - progress) / speed;
            }
        }

        /// <summary>Word for one stroke in the block info readout, e.g. "push" or "lift".</summary>
        protected virtual string StrokeNoun => "move";

        /// <summary>
        /// Runs on the server when a stroke fires. Should work out which cells move and
        /// call <see cref="StartMove"/>. Returning false just means nothing happened.
        /// </summary>
        protected abstract bool TryMove();

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            mpConsumer = GetBehavior<BEBehaviorMPConsumer>();

            // Both sides tick. The server owns the decision to move; the client runs the
            // same accumulator purely so the block info readout moves in real time —
            // syncing progress every tick would be a packet per machine per 250ms.
            RegisterGameTickListener(OnTick, TickIntervalMs);
        }

        void OnTick(float dt)
        {
            if (DebugRotation) LogRotationIfChanged();

            float speed = Speed;
            if (speed <= MinSpeed) return;

            progress += speed * dt;
            if (progress < RevolutionsPerStroke) return;

            // A stroke is still in the air. Hold the charge rather than firing again —
            // the blocks of the previous stroke are not in the grid to be found right now.
            if (Stroking) return;

            progress -= RevolutionsPerStroke;

            // Only the server starts strokes. The client used to start its own from this
            // same accumulator, which drifts by up to a tick — a quarter second against a
            // stroke lasting under half of one. It now starts the animation when it is told
            // the machine moved, which is also when the carrier entity carrying the load
            // shows up, so the two stay together.
            if (Api.Side != EnumAppSide.Server) return;
            if (TryMove()) BeginStroke();
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
        /// Lifts the given cells out of the grid and hands them to a carrier entity that
        /// flies them one cell along <paramref name="direction"/> and puts them back.
        /// </summary>
        protected bool StartMove(IList<BlockPos> cells, BlockFacing direction)
        {
            if (cells == null || cells.Count == 0) return false;

            IBlockAccessor ba = Api.World.BlockAccessor;

            // Every cell needs somewhere to land. A cell vacated by another group member
            // counts as free — that is what lets a solid group shuffle along at all.
            HashSet<BlockPos> group = new HashSet<BlockPos>(cells);
            foreach (BlockPos cell in cells)
            {
                BlockPos landing = cell.AddCopy(direction);
                if (group.Contains(landing)) continue;
                if (ba.GetChunkAtBlockPos(landing) == null) return false;
                if (!IsFree(ba.GetBlock(landing))) return false;
            }

            EntityProperties type = Api.World.GetEntityType(new AssetLocation("mechworks", "movingblocks"));
            if (type == null) return false;
            if (Api.World.ClassRegistry.CreateEntity(type) is not EntityMovingBlocks carrier) return false;

            // From here until the entity settles, these blocks exist only inside the
            // snapshot — which is why EntityMovingBlocks puts them back even when it dies
            // unexpectedly.
            BlockPos source = cells[0].Copy();
            BlockPos dest = source.AddCopy(direction);

            BlockSnapshot snapshot = BlockSnapshot.Capture(ba, cells, source);

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

            snapshot.ClearFromWorld(ba, source);

            carrier.Configure(snapshot, source, dest, MoveDurationSec);
            carrier.Pos.SetPos(source.X, source.InternalY, source.Z);

            Api.World.SpawnEntity(carrier);
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
            tree.SetFloat("progress", progress);
            tree.SetBool("inverted", Inverted);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            progress = tree.GetFloat("progress");
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

            // Speed is revolutions per second; a stroke costs RevolutionsPerStroke of them.
            sb.AppendLine(string.Format("Rotation: {0}{1}",
                RotationReversed ? "reversed" : "forward",
                Inverted ? " (flipped by hand)" : ""));
            sb.AppendLine(string.Format("Speed: {0:0.###}/s", speed));
            sb.AppendLine(string.Format("Charge: {0:P0}", progress / RevolutionsPerStroke));
            sb.AppendLine(string.Format("Next {0} in {1:0.#}s (one every {2:0.#}s)",
                StrokeNoun, SecondsToNextStroke, RevolutionsPerStroke / speed));
        }
    }
}
