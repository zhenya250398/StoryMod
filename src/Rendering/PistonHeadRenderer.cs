using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Mechworks
{
    /// <summary>
    /// Draws the moving ends of the piston's beam.
    ///
    /// The beam itself is real blocks, and a rod of identical blocks sliding along looks
    /// the same in the middle wherever it is — only its ends change. So only the ends are
    /// animated: the head plate, plus one cell of beam emerging at the front and another
    /// withdrawing at the back. All three ride at the machine's own continuous extension,
    /// which is the same number the load it is shoving moves by. All are kept out of the block's static mesh (see
    /// BEPiston.OnTesselation) and drawn here.
    ///
    /// Follows Vintage Kinematics' KineticPistonRenderer (MIT, Copyright (c) 2026 garward)
    /// — see THIRD-PARTY.md.
    /// </summary>
    public class PistonHeadRenderer : IRenderer
    {
        /// <summary>Shape elements this renderer owns. Must match piston.json.</summary>
        public static readonly string[] MovingElements = { "head", "beamsegment" };

        const string HeadElement = "head";
        const string SegmentElement = "beamsegment";

        readonly ICoreClientAPI capi;
        readonly BEPiston piston;
        readonly BlockPos pos;
        readonly Matrixf modelMat = new Matrixf();

        MultiTextureMeshRef headMesh;
        MultiTextureMeshRef segmentMesh;
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
            if (!meshBuilt) BuildMeshes();
            if (headMesh == null && segmentMesh == null) return;

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);

            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            // The head is the plate on the end of the beam, so it rides at the beam tip.
            float outCells = (float)piston.BeamOut;
            Draw(rpi, prog, camPos, headMesh, outCells);

            // Both ends of the rod are in motion, and the middle is indistinguishable
            // either way. The leading segment emerges from behind the head; the trailing
            // one withdraws into the machine. Same mesh, two ends. They are drawn only
            // while the beam is between cells: parked, each would land exactly on a real
            // beam block and fight it for depth.
            if (piston.BeamMoving)
            {
                Draw(rpi, prog, camPos, segmentMesh, outCells);
                Draw(rpi, prog, camPos, segmentMesh, -(float)piston.BeamBack);
            }

            prog.Stop();
        }

        void Draw(IRenderAPI rpi, IStandardShaderProgram prog, Vec3d camPos, MultiTextureMeshRef mesh, float offset)
        {
            if (mesh == null) return;

            // The meshes are baked with the block's own shape rotation, exactly like the
            // static body, so there is no rotation to redo here. That leaves a plain
            // translation along the world axis the piston points down — far less to get
            // wrong than rotating the offset into place.
            Vec3f dir = piston.PushFacing.Normalf;

            modelMat
                .Identity()
                .Translate(
                    (float)(pos.X - camPos.X) + dir.X * offset,
                    (float)(pos.Y - camPos.Y) + dir.Y * offset,
                    (float)(pos.Z - camPos.Z) + dir.Z * offset);

            prog.ModelMatrix = modelMat.Values;
            rpi.RenderMultiTextureMesh(mesh, "tex");
        }

        void BuildMeshes()
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

            headMesh = Upload(block, shape, cshape, HeadElement);
            segmentMesh = Upload(block, shape, cshape, SegmentElement);
        }

        MultiTextureMeshRef Upload(Block block, Shape shape, CompositeShape cshape, string element)
        {
            capi.Tesselator.TesselateShape(
                block, shape, out MeshData mesh,
                new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ),
                null, new[] { element });

            return mesh == null ? null : capi.Render.UploadMultiTextureMesh(mesh);
        }

        public void Dispose()
        {
            headMesh?.Dispose();
            headMesh = null;
            segmentMesh?.Dispose();
            segmentMesh = null;
        }
    }
}
