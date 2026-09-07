using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Draws the blocks an <see cref="EntityMovingBlocks"/> is carrying.
    ///
    /// The mesh is built once when the entity appears and then just moved around — the
    /// snapshot never changes during flight. Vintage Kinematics builds cube faces by hand
    /// for this because a contraption can hold 512 blocks; with at most a dozen we can
    /// let the tesselator do the work.
    /// </summary>
    public class MovingBlocksRenderer : IRenderer
    {
        readonly ICoreClientAPI capi;
        readonly EntityMovingBlocks entity;
        readonly Matrixf modelMat = new Matrixf();

        MultiTextureMeshRef meshRef;
        bool meshBuilt;

        public double RenderOrder => 0.5;
        public int RenderRange => 99;

        public MovingBlocksRenderer(ICoreClientAPI capi, EntityMovingBlocks entity)
        {
            this.capi = capi;
            this.entity = entity;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (entity == null || !entity.Alive) return;
            if (capi.World.Player?.Entity == null) return;

            if (!meshBuilt) BuildMesh();
            if (meshRef == null) return;

            // Entity position is the snapshot origin, so block offsets apply straight on top.
            Vec3d origin = entity.Pos.XYZ;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            IRenderAPI rpi = capi.Render;
            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);

            IStandardShaderProgram prog = rpi.PreparedStandardShader(
                (int)origin.X, (int)origin.Y, (int)origin.Z);
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            modelMat
                .Identity()
                .Translate((float)(origin.X - camPos.X), (float)(origin.Y - camPos.Y), (float)(origin.Z - camPos.Z));

            // A turning load stays put and spins about the middle of its own cell. The
            // sign is negated because BlockSnapshot.Rotate is a right-handed rotation by
            // MINUS the angle — the handedness vanilla's block codes use, not the usual
            // one. Turning about a negative axis (down, west, north) is the same motion
            // the other way round again, which the axis component's sign takes care of.
            //
            // Translating by half a block on all three axes rather than only the two
            // across the axis: the third does not matter, since a rotation leaves its own
            // axis alone, and one expression covers all six directions.
            if (entity.Turning)
            {
                Vec3i n = entity.TurnAxis.Normali;

                // Reduced into a single circle first. A turntable that has been running
                // for a while has turned through thousands of degrees, and a float radian
                // that large has lost enough precision to make the load visibly judder.
                float turned = (float)GameMath.Mod(entity.TurnedDegrees, 360);
                float rad = -turned * GameMath.DEG2RAD;

                modelMat.Translate(0.5f, 0.5f, 0.5f);

                if (n.X != 0) modelMat.RotateX(rad * n.X);
                else if (n.Y != 0) modelMat.RotateY(rad * n.Y);
                else modelMat.RotateZ(rad * n.Z);

                modelMat.Translate(-0.5f, -0.5f, -0.5f);
            }

            prog.ModelMatrix = modelMat.Values;

            // "tex" — the sampler name the standard shader actually declares.
            rpi.RenderMultiTextureMesh(meshRef, "tex");

            prog.Stop();
        }

        void BuildMesh()
        {
            meshBuilt = true;

            BlockSnapshot snapshot = entity.Snapshot;
            if (snapshot == null || snapshot.Count == 0) return;

            MeshData combined = null;

            for (int i = 0; i < snapshot.Count; i++)
            {
                Block block = capi.World.GetBlock(new AssetLocation(snapshot.BlockCodes[i]));
                if (block == null || block.Id == 0) continue;

                capi.Tesselator.TesselateBlock(block, out MeshData blockMesh);
                if (blockMesh == null) continue;

                Vec3i offset = snapshot.Offsets[i];
                blockMesh.Translate(offset.X, offset.Y, offset.Z);

                if (combined == null) combined = blockMesh;
                else combined.AddMeshData(blockMesh);
            }

            if (combined == null) return;
            meshRef = capi.Render.UploadMultiTextureMesh(combined);
        }

        public void Dispose()
        {
            meshRef?.Dispose();
            meshRef = null;
        }
    }
}
