using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Draws the piston head: the plate on the end of the beam.
    ///
    /// It is kept out of the block's static mesh (see BEPiston.OnTesselation) and put back
    /// here with a translation, so it can ride out to the beam tip while the casing stays
    /// put.
    ///
    /// Follows Vintage Kinematics' KineticPistonRenderer (MIT, Copyright (c) 2026 garward)
    /// — see THIRD-PARTY.md.
    /// </summary>
    public class PistonHeadRenderer : IRenderer
    {
        /// <summary>Shape elements this renderer owns. Must match piston.json.</summary>
        public static readonly string[] MovingElements = { "head" };

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
        /// Where the head sits right now, in blocks along the push direction.
        ///
        /// The head is the plate on the far end of the beam, not a face of the casing: the
        /// force runs shaft, casing, beam, plate, load, so the plate is what actually meets
        /// whatever is being pushed. It therefore rides at the beam tip and only slides
        /// while a stroke is carrying it to the next cell.
        /// </summary>
        float HeadOffset()
        {
            float tip = piston.Extension;
            if (!piston.Stroking) return tip;

            // Extension was already stepped when the stroke started, so the head is still
            // catching up: one cell behind it when extending, one ahead when drawing in.
            float progress = GameMath.Clamp(piston.StrokeProgress, 0f, 1f);
            float lag = (1f - progress) * Travel;

            return piston.Reversed ? tip + lag : tip - lag;
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
