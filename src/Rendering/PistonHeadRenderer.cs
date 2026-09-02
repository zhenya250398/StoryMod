using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Draws the piston head, the one part of the block that moves.
    ///
    /// The static mesh is tesselated without it (see BEPiston.OnTesselation) and this
    /// renderer puts it back with a translation, so the head can slide a full cell over
    /// the course of a stroke while the casing stays put.
    ///
    /// Follows Vintage Kinematics' KineticPistonRenderer (MIT, Copyright (c) 2026 garward)
    /// — see THIRD-PARTY.md.
    /// </summary>
    public class PistonHeadRenderer : IRenderer
    {
        /// <summary>Element the shape gives the moving part. Must match piston.json.</summary>
        public const string HeadElement = "head";

        /// <summary>How far the head travels over one stroke, in shape units (1/16ths).</summary>
        const float TravelVoxels = 16f;

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

            // The element mesh comes out of the tesselator unrotated, so the block's own
            // shape rotation is applied here. The head offset is translated last, which
            // means it is expressed in the unrotated shape frame and the rotation carries
            // it into the world — the model always slides along its own +X.
            Vec3f rotRad = ShapeRotationRad();
            float offset = HeadOffsetVoxels() / 16f;

            modelMat
                .Identity()
                .Translate((float)(pos.X - camPos.X), (float)(pos.Y - camPos.Y), (float)(pos.Z - camPos.Z))
                .Translate(0.5f, 0.5f, 0.5f)
                .Rotate(rotRad)
                .Translate(-0.5f, -0.5f, -0.5f)
                .Translate(offset, 0f, 0f);

            prog.ModelMatrix = modelMat.Values;
            rpi.RenderMultiTextureMesh(headMesh, "tex");
            prog.Stop();
        }

        /// <summary>
        /// Where the head sits right now. At rest it is home; during a stroke it travels a
        /// full cell, forwards when extending and backwards when drawing in, so it reads as
        /// the leading edge of the beam that is about to appear or has just gone.
        /// </summary>
        float HeadOffsetVoxels()
        {
            if (!piston.Stroking) return 0f;

            float progress = GameMath.Clamp(piston.StrokeProgress, 0f, 1f);
            return piston.Reversed
                ? (1f - progress) * TravelVoxels
                : progress * TravelVoxels;
        }

        Vec3f ShapeRotationRad()
        {
            CompositeShape shape = piston.Block?.Shape;
            if (shape == null) return new Vec3f();

            const float deg2rad = GameMath.PI / 180f;
            return new Vec3f(shape.rotateX * deg2rad, shape.rotateY * deg2rad, shape.rotateZ * deg2rad);
        }

        void BuildMesh()
        {
            meshBuilt = true;

            Block block = piston.Block;
            if (block?.Shape?.Base == null) return;

            AssetLocation loc = block.Shape.Base.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");

            Shape shape = Shape.TryGet(capi, loc);
            if (shape == null) return;

            // "head/*", not "head": a bare name matches the element but drops its children,
            // so the rod inside the head would silently vanish. The wildcard is what tells
            // the matcher to recurse.
            capi.Tesselator.TesselateShape(
                block, shape, out MeshData mesh, new Vec3f(), null, new[] { HeadElement + "/*" });

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
