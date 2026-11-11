using System.Text;
using OpenPlane.App.Services;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.App.Api;

/// <summary>
/// The builder's free-form shape parts (primitives, mesh edits, .obj import, fixed bodies made editable) and its
/// reference pictures. Mesh edits run here, in OpenPlane.Core, so the page and the saved plane agree.
/// </summary>
public static class ShapeEndpoints
{
    public const long MaxObjBytes = 64L * 1024 * 1024;

    public static void Map(WebApplication app, OpenPlaneServices s)
    {
        app.MapPost("/api/shapes/primitive", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<PrimitiveRequest>(req);
            var kind = (body.Kind ?? "").Trim().ToLowerInvariant();
            if (!ShapeMesh.PrimitiveKinds.Contains(kind))
                throw new UserFacingException($"OpenPlane doesn't know the shape \"{body.Kind}\". Pick a box, cylinder, sphere, cone or capsule.");
            double[]? size = body.Size != null && body.Size.Length >= 3 && body.Size.Take(3).All(v => v > 0.001 && v < 1000) ? body.Size : null;
            return ApiJson.Ok(new { shape = ShapeMesh.NewPrimitiveShape(kind, size) });
        });

        app.MapPost("/api/shapes/op", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<OpRequest>(req);
            if (body.Shape == null) throw new UserFacingException("The request has no shape.");
            return ApiJson.Ok(RunOp(body));
        });

        app.MapPost("/api/shapes/import-obj", async (HttpRequest req) =>
        {
            var name = Path.GetFileNameWithoutExtension(Path.GetFileName(req.Query["name"].ToString()));
            using var ms = await ApiJson.ReadBody(req, MaxObjBytes, () => new UserFacingException("That .obj file is over 64 MB. Simplify the model first."));
            if (ms.Length == 0) throw new UserFacingException("That .obj file is empty.");
            var text = new UTF8Encoding(false).GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var r = ShapeMesh.ImportObj(text);
            var shape = new ShapeSpec { Id = ShapeMesh.NewId(), Name = string.IsNullOrWhiteSpace(name) ? "Imported model" : name, Source = "obj" };
            r.Mesh.WriteTo(shape);
            return ApiJson.Ok(new { shape, triangles = r.Triangles, warning = r.Warning });
        });

        app.MapPost("/api/shapes/from-mesh", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<FromMeshRequest>(req);
            var meshes = s.Meshes.Get(body.MeshSet) ?? throw new UserFacingException("mesh_set_unknown", 409);
            var f = body.Fuselage ?? throw new UserFacingException("The request has no body to make editable.");
            if (f.Kind != FuselageSpec.MeshKind || f.MeshIndex < 0 || f.MeshIndex >= meshes.Count)
                throw new UserFacingException($"\"{f.Name}\" has no fixed shape to make editable.");
            return ApiJson.Ok(new { shape = FromFixedMesh(f, meshes[f.MeshIndex]) });
        });

        // ------------------------------------------------------------ reference pictures
        app.MapPost("/api/references", async (HttpRequest req) =>
        {
            using var ms = await ApiJson.ReadBody(req, ReferenceStore.MaxBytes, () => new UserFacingException($"That picture is over {ReferenceStore.MaxBytes / 1024 / 1024} MB; reference pictures can be up to {ReferenceStore.MaxBytes / 1024 / 1024} MB."));
            var bytes = ms.ToArray();
            var image = s.References.Add(bytes);
            var size = ReferenceStore.Size(bytes);
            return ApiJson.Ok(new { image, width = size?.width, height = size?.height });
        });

        app.MapGet("/api/references/{image}", (string image) =>
        {
            var bytes = s.References.Get(image);
            return bytes != null ? Results.File(bytes, ReferenceStore.ContentType(image)) : ApiJson.Error("That picture isn't here any more.", 404);
        });
    }

    /// <summary>A fixed body as an editable shape: its move, turn and scale baked into the corners, identity transform.</summary>
    public static ShapeSpec FromFixedMesh(FuselageSpec f, MeshData src)
    {
        double scale = f.Scale > 0 ? f.Scale : 1;
        var offset = Vec3.FromArray(f.Position ?? new double[3]);
        var pivot = FuselageLoft.BoundsCenter(src);
        var placed = FuselageLoft.IsTurned(f.RotationDeg)
            ? FuselageLoft.Transform(src, pivot, scale, FuselageLoft.Rotation(f.RotationDeg), offset, f.Name)
            : FuselageLoft.Transform(src, pivot, scale, offset, f.Name);
        var poly = ShapeMesh.FromMesh(placed);
        if (poly.Faces.Count == 0) throw new UserFacingException($"\"{f.Name}\" has no faces to edit.");
        int tris = ShapeMesh.TriangleCount(poly.Faces);
        if (tris > ShapeMesh.MaxTriangles) throw new UserFacingException($"\"{f.Name}\" has {tris:N0} triangles, too many to edit (up to {ShapeMesh.MaxTriangles:N0}).");
        var shape = new ShapeSpec
        {
            Id = ShapeMesh.NewId(), Name = f.Name, Mirror = f.Mirror, Source = "mesh", SourcePartId = f.SourcePartId,
            Material = src.TriangleMaterials.Length > 0 && src.TriangleMaterials.All(m => m == 1) ? ShapeSpec.Glass : ShapeSpec.Skin,
        };
        poly.WriteTo(shape);
        return shape;
    }

    public static object RunOp(OpRequest body)
    {
        var mesh = PolyMesh.FromShape(body.Shape!);
        var sel = body.Selection ?? new SelectionDto();
        var mode = sel.Mode is "vert" or "edge" or "face" ? sel.Mode : "vert";
        var p = body.Params ?? new ParamsDto();
        List<int> Faces() => ShapeMesh.SelectedFaces(mesh, mode, sel.Verts, sel.Edges, sel.Faces);
        List<int> Verts() => ShapeMesh.SelectedVerts(mesh, mode, sel.Verts, sel.Edges, sel.Faces);

        MeshOpResult r = (body.Op ?? "").ToLowerInvariant() switch
        {
            "extrude" => ShapeMesh.ExtrudeFaces(mesh, NeedFaces(Faces(), "extrude")),
            "inset" => ShapeMesh.InsetFaces(mesh, NeedFaces(Faces(), "inset")),
            "loopcut" => p.Edge != null && p.Edge.Length >= 2
                ? ShapeMesh.LoopCut(mesh, p.Edge[0], p.Edge[1], p.Cuts ?? 1, p.Factor ?? 0.5)
                : throw new UserFacingException("Pick an edge to cut across."),
            "merge" => ShapeMesh.Merge(mesh, Verts(), p.Mode ?? "center"),
            "merge_distance" => ShapeMesh.MergeByDistance(mesh, p.Distance is > 0 ? p.Distance.Value : 0.001, Verts()),
            "delete" => ShapeMesh.Delete(mesh, p.What ?? (mode == "face" ? "faces" : mode == "edge" ? "edges" : "verts"), Verts(), sel.Edges, mode == "face" ? Faces() : sel.Faces),
            "subdivide" => ShapeMesh.SubdivideApply(mesh),
            "recalc_outward" => ShapeMesh.RecalculateOutward(mesh),
            "flip" => ShapeMesh.Flip(mesh, NeedFaces(Faces(), "flip")),
            _ => throw new UserFacingException($"OpenPlane doesn't know the edit \"{body.Op}\"."),
        };

        var shape = BuilderJson.Clone(body.Shape!);
        r.Mesh.WriteTo(shape);
        // Fill in the selection in every mode, so the page can keep whichever it shows.
        var faces = r.Faces;
        var verts = r.Verts.Count > 0 ? r.Verts : faces.SelectMany(f => r.Mesh.Faces[f]).Distinct().ToList();
        var edges = r.Edges.Count > 0 ? r.Edges : ShapeMesh.FaceEdges(r.Mesh, faces);
        return new
        {
            shape,
            selection = new { mode, verts, edges, faces },
            motion = r.Motion?.Select(m => new { v = m.V, origin = m.Origin, dir = m.Dir }).ToList(),
            max_amount = r.MaxAmount,
            note = r.Note,
            triangles = ShapeMesh.TriangleCount(r.Mesh.Faces),
        };
    }

    private static List<int> NeedFaces(List<int> faces, string what)
    {
        if (faces.Count == 0) throw new UserFacingException($"Select some faces to {what} (whole faces: all of their corners).");
        return faces;
    }

    public sealed class PrimitiveRequest { public string? Kind { get; set; } public double[]? Size { get; set; } }
    public sealed class FromMeshRequest { public string? MeshSet { get; set; } public FuselageSpec? Fuselage { get; set; } }
    public sealed class SelectionDto
    {
        public string? Mode { get; set; }
        public List<int>? Verts { get; set; }
        public List<int[]>? Edges { get; set; }
        public List<int>? Faces { get; set; }
    }
    public sealed class ParamsDto
    {
        public int[]? Edge { get; set; }
        public int? Cuts { get; set; }
        public double? Factor { get; set; }
        public string? Mode { get; set; }
        public double? Distance { get; set; }
        public string? What { get; set; }
    }
    public sealed class OpRequest
    {
        public ShapeSpec? Shape { get; set; }
        public string? Op { get; set; }
        public SelectionDto? Selection { get; set; }
        public ParamsDto? Params { get; set; }
    }
}
