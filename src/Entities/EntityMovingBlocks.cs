using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Blocks in flight. Between the moment a piston lifts a run of blocks out of the
    /// world and the moment they land one cell over, they exist only as this entity:
    /// a snapshot plus a position interpolating from source to destination.
    ///
    /// Anything standing on the run rides along. Nothing else collides with it yet —
    /// walk into the side of a moving chain and you pass straight through.
    ///
    /// The lift/settle structure, the rider support tolerances and the grace period
    /// follow Vintage Kinematics' EntityVKContraption (MIT, Copyright (c) 2026 garward)
    /// — see THIRD-PARTY.md.
    /// </summary>
    public class EntityMovingBlocks : Entity
    {
        const string AttrSnapshot = "mechworksSnapshot";
        const string AttrSource = "mechworksSource";
        const string AttrDest = "mechworksDest";
        const string AttrDuration = "mechworksDuration";
        const string AttrTurn = "mechworksTurn";

        /// <summary>
        /// How far a rider may have sunk and still be picked up in the first place.
        ///
        /// Generous because a sink at the start of every stroke is unavoidable: the server
        /// takes the blocks out of the grid, and the carrier entity reaches the client a
        /// tick or two later in a separate packet. In between, the local player falls with
        /// no floor under them. Measured at 0.10–0.13; this leaves ~3x headroom.
        /// </summary>
        const double SupportSinkTolerance = 0.35;

        /// <summary>
        /// How far a rider already known to this platform may sink before being let go.
        /// Much larger than the pick-up tolerance on purpose: while the run is airborne
        /// there is no floor under the rider, so any hiccup drops them a long way in one
        /// tick and a strict tolerance would never get them back.
        /// </summary>
        const double SupportRecaptureTolerance = 1.5;

        /// <summary>How far above the surface a rider may hover and still be picked up.</summary>
        const double SupportHoverTolerance = 0.015625;

        /// <summary>
        /// How far above the surface a rider already known to this platform may drift.
        /// A rising platform repeatedly nudges its rider slightly airborne, and the
        /// pick-up tolerance is far too tight to survive that.
        /// </summary>
        const double SupportRecaptureHover = 0.5;

        /// <summary>Horizontal slack, so standing on the very edge still counts.</summary>
        const double SupportHorizontalSkin = 0.15;

        /// <summary>Blocks are treated as very slightly larger when testing for a hit.</summary>
        const double CollisionSkin = 0.0078125;

        /// <summary>Extra gap left after shoving, so the next tick does not re-hit.</summary>
        const double PushClearance = 0.001;

        /// <summary>
        /// Walking constantly lifts an entity off the ground by a hair. Without a grace
        /// period a walking player would be dropped by the platform every other tick.
        /// </summary>
        const long SupportGraceMs = 350;

        /// <summary>
        /// How long someone counts as "one of ours" for the purpose of picking tolerances.
        /// Deliberately much longer than the carry grace: a hoist strokes every second or
        /// two with the same rider standing there the whole time, and treating each stroke
        /// as a stranger means re-acquiring them cold every single time.
        ///
        /// This does NOT keep carrying them — that is still <see cref="SupportGraceMs"/>,
        /// so stepping or jumping off still works immediately.
        /// </summary>
        const long RiderMemoryMs = 3000;

        /// <summary>
        /// Logs why riding does or does not engage — feet height, distance to the nearest
        /// surface, which tolerance was in play. Off by default; flip it on when riding
        /// misbehaves, because guessing at this from the outside does not work.
        /// </summary>
        // static readonly, not const: a const false makes the call site unreachable code
        // and the compiler warns about it.
        static readonly bool DebugCarry = false;

        long lastCarryLogMs;

        public BlockSnapshot Snapshot { get; private set; }
        public BlockPos SourceOrigin { get; private set; }
        public BlockPos DestOrigin { get; private set; }

        /// <summary>0 at the source cell, 1 at the destination cell.</summary>
        public float Progress { get; private set; }

        /// <summary>
        /// Degrees this load turns about its origin over the stroke, 0 for a straight move.
        /// A turning load does not travel: source and destination are both the pivot.
        /// </summary>
        public int TurnDegrees { get; private set; }

        /// <summary>
        /// The shortest sweep that ends on the same orientation: 270 becomes -90.
        ///
        /// Placement and animation want different numbers from the same turn. Rotate and
        /// GetRotatedBlockCode need the angle as given, because that is the convention
        /// they share. Sweeping it literally sends the load three quarters of the way
        /// round to reach a place a quarter turn away.
        /// </summary>
        int SweepDegrees => GameMath.Mod(TurnDegrees + 180, 360) - 180;

        /// <summary>How far round the sweep is right now.</summary>
        public float TurnedDegrees => SweepDegrees * Progress;

        /// <summary>False until the snapshot and both origins are known on this side.</summary>
        public bool Configured => Snapshot != null && SourceOrigin != null && DestOrigin != null;

        float duration = 0.4f;
        bool settled;
        MovingBlocksRenderer renderer;

        RiderMemory riderMemory;
        readonly Dictionary<long, Vec3d> riderCarrierPos = new Dictionary<long, Vec3d>();
        readonly Dictionary<long, float> riderTurned = new Dictionary<long, float>();
        readonly Dictionary<long, System.Action> hooks = new Dictionary<long, System.Action>();
        readonly Dictionary<long, Entity> hooked = new Dictionary<long, Entity>();
        readonly HashSet<long> seenThisTick = new HashSet<long>();
        readonly CollisionTester collisionTester = new CollisionTester();

        public override bool ApplyGravity => false;
        public override bool IsInteractable => false;

        /// <summary>
        /// Called on the server right after the entity is created, before spawning.
        /// </summary>
        public void Configure(BlockSnapshot snapshot, BlockPos source, BlockPos dest, float durationSec, int turnDegrees = 0)
        {
            Snapshot = snapshot;
            SourceOrigin = source.Copy();
            DestOrigin = dest.Copy();
            duration = durationSec;
            TurnDegrees = turnDegrees;

            TreeAttribute snapTree = new TreeAttribute();
            snapshot.ToAttributes(snapTree);
            WatchedAttributes[AttrSnapshot] = snapTree;

            WatchedAttributes.SetBlockPos(AttrSource, SourceOrigin);
            WatchedAttributes.SetBlockPos(AttrDest, DestOrigin);
            WatchedAttributes.SetFloat(AttrDuration, duration);
            WatchedAttributes.SetInt(AttrTurn, turnDegrees);
        }

        public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            base.Initialize(properties, api, InChunkIndex3d);

            // On the client everything arrives through WatchedAttributes.
            Snapshot ??= BlockSnapshot.FromAttributes(WatchedAttributes.GetTreeAttribute(AttrSnapshot));
            SourceOrigin ??= WatchedAttributes.GetBlockPos(AttrSource, null);
            DestOrigin ??= WatchedAttributes.GetBlockPos(AttrDest, null);
            duration = WatchedAttributes.GetFloat(AttrDuration, duration);
            TurnDegrees = WatchedAttributes.GetInt(AttrTurn);

            riderMemory = api.ModLoader.GetModSystem<MechworksModSystem>()?.Riders;

            // Put the blocks where they belong before anything gets a chance to look at
            // them. Waiting for the first tick leaves them at the spawn position for a
            // frame, which is long enough for a rider to lose support and fall through.
            if (Configured)
            {
                Pos.SetPos(LerpOrigin());

                // Hook riders right now, not on the first tick. The blocks were already
                // taken out of the grid before this entity existed, so any physics that
                // runs before the first tick does so with no floor under the rider.
                UpdateRiderHooks();
            }

            if (api is ICoreClientAPI capi && Snapshot != null)
            {
                renderer = new MovingBlocksRenderer(capi, this);
                capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "mechworks:movingblocks");
            }
        }

        public override void OnGameTick(float dt)
        {
            base.OnGameTick(dt);

            // On the client the snapshot and the two origins arrive through
            // WatchedAttributes, which is not necessarily before the first tick. Acting on
            // a half-configured entity puts the blocks at a junk position for one tick,
            // which is long enough for a rider to fall out of support and never recover.
            if (!Configured) return;

            if (duration <= 0f) duration = 0.4f;
            Progress = GameMath.Clamp(Progress + dt / duration, 0f, 1f);

            // Both sides run the same lerp rather than leaning on entity position sync:
            // the motion is fully determined by Progress, and the client needs an exact
            // delta to carry the local player without jitter.
            Pos.SetPos(LerpOrigin());

            UpdateRiderHooks();

            if (World.Side == EnumAppSide.Server && Progress >= 1f) Settle();
        }

        Vec3d LerpOrigin()
        {
            if (SourceOrigin == null || DestOrigin == null) return Pos.XYZ;

            return new Vec3d(
                GameMath.Lerp(SourceOrigin.X, DestOrigin.X, Progress),
                GameMath.Lerp(SourceOrigin.InternalY, DestOrigin.InternalY, Progress),
                GameMath.Lerp(SourceOrigin.Z, DestOrigin.Z, Progress));
        }

        // --- riding ---

        /// <summary>Middle of the cell the load turns about.</summary>
        Vec3d PivotCentre => new Vec3d(SourceOrigin.X + 0.5, 0, SourceOrigin.Z + 0.5);

        /// <summary>
        /// Turns a point about the pivot. Positive follows the standard rotation about Y,
        /// where 90 degrees sends (x, z) to (z, -x).
        /// </summary>
        Vec3d TurnAbout(Vec3d p, float degrees)
        {
            if (degrees == 0f) return p.Clone();

            Vec3d c = PivotCentre;
            double rad = degrees * GameMath.DEG2RAD;
            double cos = System.Math.Cos(rad), sin = System.Math.Sin(rad);
            double dx = p.X - c.X, dz = p.Z - c.Z;

            return new Vec3d(c.X + dx * cos + dz * sin, p.Y, c.Z - dx * sin + dz * cos);
        }

        /// <summary>
        /// A world point expressed in the load's own unturned frame.
        ///
        /// This is what lets a turning load reuse every support test unchanged. Turning the
        /// blocks' boxes would stop them being axis-aligned; turning the rider back into
        /// the layout the boxes are built in costs one rotation and keeps them so.
        /// </summary>
        Vec3d ToLoadFrame(Vec3d worldPos)
        {
            return TurnDegrees == 0 ? worldPos : TurnAbout(worldPos, TurnedDegrees);
        }

        /// <summary>Collision boxes of the carried blocks, in world space, right now.</summary>
        Cuboidd[] GetWorldBlockBoxes()
        {
            return GetBlockBoxesAt(Pos.XYZ);
        }

        /// <summary>Collision boxes the carried blocks would have with the given origin.</summary>
        Cuboidd[] GetBlockBoxesAt(Vec3d origin)
        {
            if (Snapshot == null) return System.Array.Empty<Cuboidd>();

            List<Cuboidd> boxes = new List<Cuboidd>();

            for (int i = 0; i < Snapshot.Count; i++)
            {
                Block block = World.GetBlock(new AssetLocation(Snapshot.BlockCodes[i]));
                Cuboidf[] blockBoxes = block?.CollisionBoxes;
                if (blockBoxes == null) continue;   // non-solid, nothing to stand on

                Vec3i offset = Snapshot.Offsets[i];
                foreach (Cuboidf cb in blockBoxes)
                {
                    Cuboidd box = new Cuboidd();
                    box.SetAndTranslate(cb,
                        origin.X + offset.X,
                        origin.Y + offset.Y,
                        origin.Z + offset.Z);
                    boxes.Add(box);
                }
            }

            return boxes.ToArray();
        }

        /// <summary>
        /// Attaches the carry to every nearby entity this side is responsible for, and
        /// drops the attachment for anything that has wandered off.
        /// </summary>
        void UpdateRiderHooks()
        {
            Cuboidd[] boxes = GetWorldBlockBoxes();
            seenThisTick.Clear();

            if (boxes.Length > 0)
            {
                GetBounds(boxes, out Vec3d center, out double horizontalRadius, out double verticalRadius);

                // The boxes are the unturned layout, but riders are out in the world. A
                // turning load sweeps a circle about its pivot, so search that circle
                // instead — otherwise a rider on the far side falls outside the box the
                // layout happens to occupy at zero degrees.
                if (TurnDegrees != 0)
                {
                    horizontalRadius = ReachFromPivot(boxes);
                    center = new Vec3d(PivotCentre.X, center.Y, PivotCentre.Z);
                }

                Entity[] nearby = World.GetEntitiesAround(
                    center,
                    (float)(horizontalRadius + 2),
                    (float)(verticalRadius + 2),
                    CanCarry);

                foreach (Entity candidate in nearby)
                {
                    seenThisTick.Add(candidate.EntityId);
                    AttachHook(candidate);
                }
            }

            DetachStaleHooks();
        }

        /// <summary>
        /// Who this side is allowed to move. Applying the carry on both sides moves an
        /// entity twice as far, so ownership is split: a client only ever moves its own
        /// player, the server moves everything else. In multiplayer each remote player is
        /// carried by their own client.
        /// </summary>
        bool CanCarry(Entity candidate)
        {
            if (candidate == null || candidate == this || !candidate.Alive) return false;
            if (candidate.CollisionBox == null) return false;
            if (candidate is EntityMovingBlocks) return false;

            if (Api is ICoreClientAPI capi) return candidate == capi.World.Player?.Entity;
            return candidate is not EntityPlayer;
        }

        void AttachHook(Entity rider)
        {
            if (hooks.ContainsKey(rider.EntityId)) return;

            // The carry has to run *after* the entity's own physics. Applied before it,
            // gravity immediately undoes the correction and the rider sinks — during the
            // flight there is nothing under their feet, the blocks are out of the grid.
            System.Action hook = () => OnRiderAfterPhysics(rider);
            hooks[rider.EntityId] = hook;
            hooked[rider.EntityId] = rider;
            rider.AfterPhysicsTick += hook;
        }

        void OnRiderAfterPhysics(Entity rider)
        {
            if (!Alive || rider == null || !rider.Alive) return;

            Cuboidd[] boxes = GetWorldBlockBoxes();
            long now = World.ElapsedMilliseconds;

            // Tested where the rider stands relative to the load, not where it stands in
            // the world: the boxes are built unturned, so the rider comes to them.
            Vec3d riderInLoadFrame = ToLoadFrame(rider.Pos.XYZ);

            Cuboidd riderBox = new Cuboidd();
            riderBox.SetAndTranslate(rider.CollisionBox, riderInLoadFrame.X, riderInLoadFrame.Y, riderInLoadFrame.Z);

            // Walking lifts an entity off the ground by a hair every other tick; the grace
            // period keeps a walking rider aboard, and while it lasts they are held with a
            // far more forgiving downward tolerance so a single bad tick cannot lose them.
            // Two different questions, two different windows. "Keep carrying them right
            // now" is a short window; "we know this rider, hold them loosely" is a long one.
            bool recentlySupported =
                riderMemory != null && riderMemory.WasRecentlySupported(rider.EntityId, now, SupportGraceMs);
            bool knownRider =
                riderMemory != null && riderMemory.WasRecentlySupported(rider.EntityId, now, RiderMemoryMs);

            double sinkTolerance = knownRider ? SupportRecaptureTolerance : SupportSinkTolerance;
            double hoverTolerance = knownRider ? SupportRecaptureHover : SupportHoverTolerance;

            // Someone actively jumping off should not be pinned back down — but a rising
            // platform gives its rider upward motion every tick, and reading that as a
            // jump would drop them exactly when they most need holding.
            bool rising = TravelDirection.Y > 0;
            bool jumping = !rising && rider.Pos.Motion != null && rider.Pos.Motion.Y > 0.01;

            double surfaceY = 0;
            bool supported = !jumping && TryGetSupportTop(riderBox, boxes, sinkTolerance, hoverTolerance, out surfaceY);

            if (DebugCarry) LogCarry(rider, riderBox, boxes, supported, surfaceY, jumping, knownRider);

            if (supported)
            {
                riderMemory?.Touch(rider.EntityId, now);
            }
            else
            {
                // Not standing on top, so it may be standing in the way. Shove it clear
                // instead of letting the chain swallow it. A shoved entity is already
                // being moved along by the leading face — carrying it too would double up.
                if (TryPushOut(rider, riderBox, boxes))
                {
                    riderCarrierPos.Remove(rider.EntityId);
                    riderTurned.Remove(rider.EntityId);
                    return;
                }

                if (!recentlySupported)
                {
                    riderCarrierPos.Remove(rider.EntityId);
                    riderTurned.Remove(rider.EntityId);
                    return;
                }
            }

            Vec3d carrierNow = Pos.XYZ;

            // Delta measured against where the carrier was when this rider was last
            // handled, so it stays correct no matter how physics and ticks interleave.
            if (riderCarrierPos.TryGetValue(rider.EntityId, out Vec3d carrierBefore))
            {
                EntityPos pos = rider.Pos;
                pos.X += carrierNow.X - carrierBefore.X;
                pos.Y += carrierNow.Y - carrierBefore.Y;
                pos.Z += carrierNow.Z - carrierBefore.Z;
            }

            riderCarrierPos[rider.EntityId] = carrierNow.Clone();

            // The same idea for the turn: swing the rider by however much the load has
            // turned since this rider was last handled. Negated to match the mesh, which
            // the renderer draws at minus the placement angle.
            if (TurnDegrees != 0)
            {
                float turnedNow = TurnedDegrees;
                if (riderTurned.TryGetValue(rider.EntityId, out float turnedBefore))
                {
                    float swing = -(turnedNow - turnedBefore);

                    Vec3d swung = TurnAbout(rider.Pos.XYZ, swing);
                    rider.Pos.X = swung.X;
                    rider.Pos.Z = swung.Z;

                    // The view comes round with the platform, or a rider ends the turn
                    // facing the way they started while the world has moved under them.
                    //
                    // Same angle and same sign as the position: yaw shares the angular
                    // convention of HORIZONTALS_ANGLEORDER, where a heading is
                    // (cos t, -sin t), and TurnAbout by +d takes heading t to t + d.
                    //
                    // Added rather than assigned, so on the client this composes with the
                    // player's own mouse movement instead of fighting it — which is also
                    // why only the side that owns an entity touches it. See CanCarry.
                    rider.Pos.Yaw += swing * GameMath.DEG2RAD;
                }

                riderTurned[rider.EntityId] = turnedNow;
            }

            if (!supported) return;

            // Stand them back on the surface. Without this they fall through the hole the
            // lifted blocks left behind and end up inside the chain when it lands.
            // Correct by the gap between feet and surface — the entity position is not
            // necessarily the bottom of its collision box.
            double correction = surfaceY - riderBox.Y1;
            if (correction >= 0.000001) rider.Pos.Y += correction;

            if (rider.Pos.Motion != null && rider.Pos.Motion.Y < 0) rider.Pos.Motion.Y = 0;

            // Physics has to be told this counts as standing on ground, or it keeps
            // accelerating the rider downwards and racks up fall damage.
            rider.CollidedVertically = true;
            rider.OnGround = true;
            rider.PositionBeforeFalling.Set(rider.Pos.X, rider.Pos.InternalY, rider.Pos.Z);
        }

        /// <summary>Unit direction the run is travelling in.</summary>
        Vec3i TravelDirection => new Vec3i(
            System.Math.Sign(DestOrigin.X - SourceOrigin.X),
            System.Math.Sign(DestOrigin.InternalY - SourceOrigin.InternalY),
            System.Math.Sign(DestOrigin.Z - SourceOrigin.Z));

        /// <summary>
        /// Shoves anything the run has driven into out of the way, along the direction of
        /// travel.
        ///
        /// Vintage Kinematics infers the push axis from how the entity itself was moving,
        /// because a contraption can travel any direction and be walked into from any
        /// side. Here the run sweeps one known axis for exactly one cell, so the answer is
        /// already known: everything gets pushed off the leading face.
        /// </summary>
        bool TryPushOut(Entity rider, Cuboidd riderBox, Cuboidd[] boxes)
        {
            Vec3i dir = TravelDirection;
            if (dir.X == 0 && dir.Y == 0 && dir.Z == 0) return false;

            double furthest = 0;
            foreach (Cuboidd raw in boxes)
            {
                Cuboidd box = raw.Clone().GrowBy(CollisionSkin, 0, CollisionSkin);
                if (!box.Intersects(riderBox)) continue;

                // Clearing the deepest block clears all the shallower ones too.
                double push = RequiredPush(riderBox, box, dir);
                if (System.Math.Abs(push) > System.Math.Abs(furthest)) furthest = push;
            }

            if (furthest == 0) return false;

            double wanted = furthest + PushClearance * System.Math.Sign(furthest);
            double possible = ClampToFreeSpace(rider, dir, wanted);
            if (possible == 0) return false;

            ApplyPush(rider, dir, possible);
            return true;
        }

        /// <summary>
        /// Cuts a push down to what the world actually has room for. Without this, someone
        /// standing between the run and a wall gets shoved straight into the wall and ends
        /// up embedded in solid blocks. Squeezed flush against it is the honest outcome.
        /// </summary>
        double ClampToFreeSpace(Entity rider, Vec3i dir, double wanted)
        {
            Vec3d basePos = rider.Pos.XYZ;
            if (!HitsTerrain(rider, Shift(basePos, dir, wanted))) return wanted;

            // Largest free distance, to within 1/64 of the requested push.
            double free = 0;
            double blocked = wanted;
            for (int i = 0; i < 6; i++)
            {
                double mid = (free + blocked) / 2;
                if (HitsTerrain(rider, Shift(basePos, dir, mid))) blocked = mid;
                else free = mid;
            }

            return System.Math.Abs(free) <= PushClearance ? 0 : free;
        }

        bool HitsTerrain(Entity rider, Vec3d at)
        {
            return collisionTester.IsColliding(World.BlockAccessor, rider.CollisionBox, at, false);
        }

        /// <summary>Same axis priority as <see cref="ApplyPush"/> — they must agree.</summary>
        static Vec3d Shift(Vec3d origin, Vec3i dir, double amount)
        {
            if (dir.X != 0) return new Vec3d(origin.X + amount, origin.Y, origin.Z);
            if (dir.Z != 0) return new Vec3d(origin.X, origin.Y, origin.Z + amount);
            return new Vec3d(origin.X, origin.Y + amount, origin.Z);
        }

        /// <summary>How far along <paramref name="dir"/> the box has to move to clear.</summary>
        static double RequiredPush(Cuboidd riderBox, Cuboidd box, Vec3i dir)
        {
            if (dir.X > 0) return box.X2 - riderBox.X1;
            if (dir.X < 0) return box.X1 - riderBox.X2;
            if (dir.Z > 0) return box.Z2 - riderBox.Z1;
            if (dir.Z < 0) return box.Z1 - riderBox.Z2;
            if (dir.Y > 0) return box.Y2 - riderBox.Y1;
            if (dir.Y < 0) return box.Y1 - riderBox.Y2;
            return 0;
        }

        static void ApplyPush(Entity rider, Vec3i dir, double amount)
        {
            EntityPos pos = rider.Pos;
            Vec3d motion = pos.Motion;
            int sign = System.Math.Sign(amount);

            if (dir.X != 0)
            {
                pos.X += amount;
                rider.CollidedHorizontally = true;
                // Kill motion fighting the push, or the entity grinds back into the blocks.
                if (motion != null && System.Math.Sign(motion.X) != sign) motion.X = 0;
                return;
            }

            if (dir.Z != 0)
            {
                pos.Z += amount;
                rider.CollidedHorizontally = true;
                if (motion != null && System.Math.Sign(motion.Z) != sign) motion.Z = 0;
                return;
            }

            pos.Y += amount;
            rider.CollidedVertically = true;
            if (motion != null && System.Math.Sign(motion.Y) != sign) motion.Y = 0;

            if (amount > 0)
            {
                rider.OnGround = true;
                rider.PositionBeforeFalling.Set(pos.X, pos.InternalY, pos.Z);
            }
        }

        /// <summary>
        /// True when the box is resting on one of the carried blocks; reports the top
        /// surface it is resting on.
        /// </summary>
        static bool TryGetSupportTop(Cuboidd riderBox, Cuboidd[] boxes, double sinkTolerance, double hoverTolerance, out double surfaceY)
        {
            surfaceY = 0;

            double bestDelta = double.MaxValue;
            foreach (Cuboidd box in boxes)
            {
                if (riderBox.X2 <= box.X1 - SupportHorizontalSkin) continue;
                if (riderBox.X1 >= box.X2 + SupportHorizontalSkin) continue;
                if (riderBox.Z2 <= box.Z1 - SupportHorizontalSkin) continue;
                if (riderBox.Z1 >= box.Z2 + SupportHorizontalSkin) continue;

                double feetDelta = riderBox.Y1 - box.Y2;
                if (feetDelta < -sinkTolerance || feetDelta > hoverTolerance) continue;

                // Closest surface to the feet wins.
                double absDelta = System.Math.Abs(feetDelta);
                if (absDelta >= bestDelta) continue;

                bestDelta = absDelta;
                surfaceY = box.Y2;
            }

            return bestDelta != double.MaxValue;
        }

        /// <summary>
        /// Temporary instrumentation: whether the hook fires at all, and if it does, why
        /// support was or was not found. Flip <see cref="DebugCarry"/> off once riding works.
        /// </summary>
        void LogCarry(Entity rider, Cuboidd riderBox, Cuboidd[] boxes, bool supported, double surfaceY, bool jumping, bool recent)
        {
            long now = World.ElapsedMilliseconds;
            if (now - lastCarryLogMs < 100) return;
            lastCarryLogMs = now;

            double nearestDelta = double.NaN;
            foreach (Cuboidd box in boxes)
            {
                double d = riderBox.Y1 - box.Y2;
                if (double.IsNaN(nearestDelta) || System.Math.Abs(d) < System.Math.Abs(nearestDelta)) nearestDelta = d;
            }

            World.Logger.Notification(
                "[mechworks] carry side={0} dir={1} progress={2:0.##} boxes={3} feetY={4:0.###} nearestTopDelta={5:0.####} supported={6} recent={7} jumping={8} motionY={9:0.####} surfaceY={10:0.###} carrierY={11:0.###}",
                World.Side, TravelDirection, Progress, boxes.Length, riderBox.Y1, nearestDelta,
                supported, recent, jumping, rider.Pos.Motion?.Y ?? 0, surfaceY, Pos.Y);
        }

        void DetachStaleHooks()
        {
            if (hooks.Count == 0) return;

            List<long> stale = null;
            foreach (KeyValuePair<long, Entity> pair in hooked)
            {
                if (seenThisTick.Contains(pair.Key) && pair.Value != null && pair.Value.Alive) continue;
                (stale ??= new List<long>()).Add(pair.Key);
            }

            if (stale == null) return;
            foreach (long id in stale) DetachHook(id);
        }

        void DetachHook(long entityId)
        {
            if (hooks.TryGetValue(entityId, out System.Action hook)
                && hooked.TryGetValue(entityId, out Entity rider)
                && rider != null)
            {
                rider.AfterPhysicsTick -= hook;
            }

            hooks.Remove(entityId);
            hooked.Remove(entityId);
            riderCarrierPos.Remove(entityId);
            riderTurned.Remove(entityId);

            // riderMemory is deliberately left alone — it has to outlive this stroke so
            // the next one recognises the same rider.
        }

        void DetachAllHooks()
        {
            foreach (long id in new List<long>(hooks.Keys)) DetachHook(id);
        }

        /// <summary>
        /// Sets every rider exactly on top of where the blocks are about to land.
        ///
        /// Without this the restored block appears overlapping the rider's feet by a
        /// fraction, and the game's own block-collision resolution ejects them — a full
        /// cell straight up, with upward motion to match. From there they are outside any
        /// support tolerance and fall. Vintage Kinematics handles the same moment in
        /// SnapSupportedEntitiesToRestoredBlocks.
        /// </summary>
        void SnapRidersToLanding()
        {
            if (DestOrigin == null || hooked.Count == 0) return;

            Cuboidd[] landed = GetBlockBoxesAt(new Vec3d(DestOrigin.X, DestOrigin.InternalY, DestOrigin.Z));
            if (landed.Length == 0) return;

            foreach (Entity rider in hooked.Values)
            {
                if (rider == null || !rider.Alive) continue;

                Cuboidd riderBox = new Cuboidd();
                riderBox.SetAndTranslate(rider.CollisionBox, rider.Pos.X, rider.Pos.Y, rider.Pos.Z);

                if (!TryGetSupportTop(riderBox, landed, SupportRecaptureTolerance, SupportRecaptureHover, out double surfaceY))
                {
                    continue;
                }

                double correction = surfaceY - riderBox.Y1;
                if (System.Math.Abs(correction) < 0.000001) continue;

                rider.Pos.Y += correction;
                if (rider.Pos.Motion != null && rider.Pos.Motion.Y < 0) rider.Pos.Motion.Y = 0;
                rider.CollidedVertically = true;
                rider.OnGround = true;
                rider.PositionBeforeFalling.Set(rider.Pos.X, rider.Pos.InternalY, rider.Pos.Z);
            }
        }

        /// <summary>Distance from the pivot to the farthest corner of the load.</summary>
        double ReachFromPivot(Cuboidd[] boxes)
        {
            Vec3d c = PivotCentre;
            double worst = 0;

            foreach (Cuboidd box in boxes)
            {
                foreach (double x in new[] { box.X1, box.X2 })
                {
                    foreach (double z in new[] { box.Z1, box.Z2 })
                    {
                        double dx = x - c.X, dz = z - c.Z;
                        worst = System.Math.Max(worst, System.Math.Sqrt(dx * dx + dz * dz));
                    }
                }
            }

            return worst;
        }

        static void GetBounds(Cuboidd[] boxes, out Vec3d center, out double horizontalRadius, out double verticalRadius)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (Cuboidd box in boxes)
            {
                if (box.X1 < minX) minX = box.X1;
                if (box.Y1 < minY) minY = box.Y1;
                if (box.Z1 < minZ) minZ = box.Z1;
                if (box.X2 > maxX) maxX = box.X2;
                if (box.Y2 > maxY) maxY = box.Y2;
                if (box.Z2 > maxZ) maxZ = box.Z2;
            }

            center = new Vec3d((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
            horizontalRadius = System.Math.Max(maxX - minX, maxZ - minZ) / 2;
            verticalRadius = (maxY - minY) / 2;
        }

        // --- landing ---

        /// <summary>
        /// Re-applies the glue marks the blocks were carrying, at wherever they landed.
        /// Server-side only, like the registry itself.
        /// </summary>
        void RestoreGlue(BlockPos origin, int turnDegrees)
        {
            if (Snapshot?.Glued == null || origin == null) return;
            if (World?.Side != EnumAppSide.Server) return;

            GlueRegistry glue = Api.ModLoader.GetModSystem<MechworksModSystem>()?.Glue;
            if (glue == null) return;

            for (int i = 0; i < Snapshot.Count && i < Snapshot.Glued.Length; i++)
            {
                if (!Snapshot.Glued[i]) continue;
                glue.Add(BlockSnapshot.WorldPos(origin, BlockSnapshot.Rotate(Snapshot.Offsets[i], turnDegrees)));
            }
        }

        /// <summary>Puts the blocks back into the world grid and removes the entity.</summary>
        void Settle()
        {
            if (settled) return;
            settled = true;

            Snapshot?.RestoreToWorld(World, DestOrigin, TurnDegrees);
            RestoreGlue(DestOrigin, TurnDegrees);
            Die(EnumDespawnReason.Removed);
        }

        /// <summary>
        /// If this entity goes away for any reason other than a clean landing, the blocks
        /// it is carrying would be gone from the world for good. Put them back wherever
        /// they currently are rather than losing them.
        /// </summary>
        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            // Order matters: seat the riders on the landing surface first, then let go.
            SnapRidersToLanding();

            // Leaving hooks attached would keep dragging riders around after we are gone.
            DetachAllHooks();

            if (World?.Side == EnumAppSide.Server && !settled && Snapshot != null)
            {
                settled = true;
                // Past the halfway point the destination is the better guess, before it
                // the source is — either way the blocks come back somewhere sane.
                BlockPos landing = Progress >= 0.5f ? DestOrigin : SourceOrigin;
                int turn = Progress >= 0.5f ? TurnDegrees : 0;
                Snapshot.RestoreToWorld(World, landing, turn);
                RestoreGlue(landing, turn);
            }

            if (renderer != null && Api is ICoreClientAPI capi)
            {
                capi.Event.UnregisterRenderer(renderer, EnumRenderStage.Opaque);
                renderer.Dispose();
                renderer = null;
            }

            base.OnEntityDespawn(despawn);
        }
    }
}
