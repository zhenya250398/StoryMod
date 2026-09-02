using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Draws the moving parts of the piston: the head plate and the stub of beam behind it.
    ///
    /// Those elements are kept out of the block's static mesh (see BEPiston.OnTesselation)
    /// and put back here with a translation, so they can slide a full cell over a stroke
    /// while the casing stays put.
    ///
    /// Follows Vintage Kinematics' KineticPistonRenderer (MIT, Copyright (c) 2026 garward)
    /// — see THIRD-PARTY.md.
    /// </summary>
    public class PistonHeadRenderer : IRenderer
    {
        /// <summary>Shape elements this renderer owns. Must match piston.json.</summary>
        public static readonly string[] MovingElements = { "head", "headrod" };

        /// <summary>How far the head travels over one stroke, in blocks.</summary>
        const float Travel = 1f;

        readonly ICoreClientAPI capi;
        readonly BEPiston piston;
        readonly BlockPos pos;
        readonly Matrixf modelMat = new Matrixf();

        MultiTextureMeshRef headMesh;
        bool meshBuilt;

        public double RenderOrder => 0.5;
        public int RenderRange => 48;

        public PistonHeadRenderer(ICoreClientAPI capi, BEPiston piston)
        {
            this.capi = capi;
            this.piston = piston;
            pos = piston.Pos.Copy();
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (capi.World.Player?.Entity == null) return;

            if (!meshBuilt) BuildMesh();
            if (headMesh == null) return;

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);

            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            // The mesh is baked with the block's own shape rotation, exactly like the static
            // body, so there is no rotation to redo here. That leaves a plain translation
            // along the world axis the piston actually points down — far less to get wrong
            // than rotating the offset into place.
            Vec3f dir = piston.PushFacing.Normalf;
            float offset = HeadOffset();

            modelMat
                .Identity()
                .Translate(
                    (float)(pos.X - camPos.X) + dir.X * offset,
                    (float)(pos.Y - camPos.Y) + dir.Y * offset,
                    (float)(pos.Z - camPos.Z) + dir.Z * offset);

            prog.ModelMatrix = modelMat.Values;
            rpi.RenderMultiTextureMesh(headMesh, "tex");
            prog.Stop();
        }

        /// <summary>
        /// Where the head sits right now, in blocks along the push direction. At rest it is
        /// home; during a stroke it travels a full cell, out when extending and back when
        /// drawing in, so it reads as the leading edge of the beam about to appear or just
        /// gone.
        /// </summary>
        float HeadOffset()
        {
            if (!piston.Stroking) return 0f;

            float progress = GameMath.Clamp(piston.StrokeProgress, 0f, 1f);
            return (piston.Reversed ? 1f - progress : progress) * Travel;
        }

        void BuildMesh()
        {
            meshBuilt = true;

            Block block = piston.Block;
            CompositeShape cshape = block?.Shape;
            if (cshape?.Base == null) return;

            AssetLocation loc = cshape.Base.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");

            Shape shape = Shape.TryGet(capi, loc);
            if (shape == null) return;

            capi.Tesselator.TesselateShape(
                block, shape, out MeshData mesh,
                new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ),
                null, MovingElements);

            if (mesh == null) return;
            headMesh = capi.Render.UploadMultiTextureMesh(mesh);
        }

        public void Dispose()
        {
            headMesh?.Dispose();
            headMesh = null;
        }
    }
}
