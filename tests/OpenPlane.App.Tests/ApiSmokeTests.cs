using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenPlane.App.Services;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using Xunit;
using Xunit.Abstractions;

namespace OpenPlane.App.Tests;

/// <summary>Starts the real server in-process against fake folders and calls every endpoint the page uses.</summary>
public class ApiSmokeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = TestPaths.NewScratch("api");
    private AppHost _host = null!;
    private HttpClient _http = null!;
    private string Planes => Path.Combine(_dir, "planes");
    private string Game => Path.Combine(_dir, "steamapps", "common", "Nuclear Option");

    public ApiSmokeTests(ITestOutputHelper output) { _out = output; }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Game, "NuclearOption_Data", "Managed"));
        var args = new List<string>
        {
            "--port", "0", "--data-dir", Path.Combine(_dir, "data"), "--planes-dir", Planes, "--game-dir", Game,
            "--downloads-dir", Path.Combine(_dir, "downloads"), "--no-external",
            "--flyout-dir", TestPaths.CraftRoot ?? Path.Combine(_dir, "no-craft"),
        };
        var mod = ModBundle();
        if (mod != null) { args.Add("--mod-dir"); args.Add(mod); }
        _host = await AppHost.StartAsync(AppOptions.Parse(args.ToArray()));
        _http = new HttpClient { BaseAddress = new Uri(_host.Url) };
        _http.DefaultRequestHeaders.Add("X-OpenPlane", "1");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
        TestPaths.TryDelete(_dir);
    }

    /// <summary>The app build copies the mod into bin/&lt;config&gt;/net10.0/mod; null when the mod can't be built here.</summary>
    private static string? ModBundle()
        => Directory.GetDirectories(Path.Combine(TestPaths.RepoRoot, "src", "OpenPlane.App", "bin"), "mod", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "OpenPlane", "OpenPlane.Mod.dll")) && File.Exists(Path.Combine(d, "Blueprinter", "LICENSE.txt")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "OpenPlane", "OpenPlane.Mod.dll"))).FirstOrDefault();

    private async Task<JToken> Get(string url, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var r = await _http.GetAsync(url);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expect, $"GET {url}: {(int)r.StatusCode} {text}");
        return string.IsNullOrEmpty(text) ? JValue.CreateNull() : JToken.Parse(text);
    }

    private async Task<JToken> Send(HttpMethod method, string url, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(body is string s ? s : Api.ApiJson.Serialize(body), Encoding.UTF8, "application/json");
        var r = await _http.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expect, $"{method} {url}: {(int)r.StatusCode} {text}");
        return string.IsNullOrEmpty(text) ? JValue.CreateNull() : JToken.Parse(text);
    }

    private async Task<JToken> Upload(string name, byte[] bytes, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var r = await _http.PostAsync($"/api/import?name={Uri.EscapeDataString(name)}", content);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expect, $"import {name}: {(int)r.StatusCode} {text}");
        return JToken.Parse(text);
    }

    [Fact]
    public async Task PageAndStatusAndSecurity()
    {
        var page = await _http.GetStringAsync("/");
        Assert.Contains("<title>OpenPlane</title>", page);
        Assert.Contains("importmap", page);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/js/builder/viewport.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/vendor/three/three.module.js")).StatusCode);

        var st = await Get("/api/status");
        Assert.Equal("command line", (string)st["paths"]!["game"]!["source"]!);
        Assert.True((bool)st["paths"]!["game"]!["valid"]!);
        Assert.Contains((string)st["mod"]!["state"]!, new[] { "needs-install", "no-bundle" });
        Assert.True((bool)st["dry_run"]!);

        // Changes without the app's header, from another origin, or for another host are refused.
        using var bare = new HttpClient { BaseAddress = new Uri(_host.Url) };
        var r1 = await bare.PostAsync("/api/game/launch", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/game/launch") { Content = new StringContent("{}") };
        req.Headers.Add("X-OpenPlane", "1"); req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(req)).StatusCode);
        var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        req2.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.SendAsync(req2)).StatusCode);

        await Send(HttpMethod.Post, "/api/game/launch");
        st = await Get("/api/status");
        Assert.Contains("open steam://rungameid/2168680", st["external_history"]!.Select(x => (string)x!));
        var dialog = await Send(HttpMethod.Post, "/api/dialog/folder", new { title = "x" }, HttpStatusCode.Conflict);
        Assert.Contains("Type the folder path", (string)dialog["error"]!);
        await Send(HttpMethod.Post, "/api/open-link", new { path = "https://evil.example" }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/open-link", new { path = "https://github.com/nikkorap/NOBlueprinter-Releases" });
    }

    [Fact]
    public async Task SettingsSaveAndValidate()
    {
        var s = await Get("/api/settings");
        Assert.True((bool)s["overridden"]!["game"]!);
        var bad = await Send(HttpMethod.Put, "/api/settings", new { game_dir = Path.Combine(_dir, "nope"), theme = "dark" }, HttpStatusCode.BadRequest);
        Assert.Contains("doesn't exist", (string)bad["error"]!);
        await Send(HttpMethod.Put, "/api/settings", new { game_dir = Game, theme = "dark", your_name = "Tester", flyout_dirs = new[] { _dir } });
        var saved = OplanePackage.FromJson<Services.AppSettings>(File.ReadAllText(Path.Combine(_dir, "data", "settings.json")));
        Assert.Equal("dark", saved.Theme);
        Assert.Equal("Tester", saved.YourName);
        Assert.Contains(_dir, saved.FlyoutDirs);
        var detect = await Send(HttpMethod.Post, "/api/settings/detect");
        Assert.NotNull(detect["message"]);
    }

    [Fact]
    public async Task ConvertEditSaveShareAndDelete()
    {
        if (TestPaths.CraftRoot == null) { _out.WriteLine("samples-private not found; conversion part skipped."); return; }
        var list = await Get("/api/flyout");
        var craft = list["craft"]!.ToArray();
        Assert.Equal(13, craft.Length);
        var heli = craft.Single(c => (bool)c["helicopter"]!);
        var satoria = craft.Single(c => (string)c["name"]! == "Satoria-LF-2");
        var thumb = await _http.GetAsync($"/api/flyout/{satoria["id"]}/thumb");
        Assert.Equal("image/png", thumb.Content.Headers.ContentType!.MediaType);

        var conv = await Send(HttpMethod.Post, "/api/flyout/convert", new { ids = new[] { (string)satoria["id"]!, (string)heli["id"]! }, builder = "Tester", replace = true });
        var results = conv["results"]!.ToArray();
        Assert.True((bool)results[0]["ok"]!);
        Assert.False((bool)results[1]["ok"]!);
        Assert.True((bool)results[1]["helicopter"]!);
        Assert.Contains(results[0]["report"]!, i => (string)i["level"]! == "approximated");
        var file = Path.Combine(Planes, "Satoria-LF-2.oplane");
        Assert.True(File.Exists(file));
        var read = OplanePackage.Read(file);
        Assert.Equal("Tester", read.Design.Origin!.Builder);
        Assert.Equal("COIN", read.Design.Donor);
        Assert.NotNull(read.Thumbnail);

        // Converting again with "replace" puts the new plane in place of the old one, which moves to "Deleted planes".
        var again = await Send(HttpMethod.Post, "/api/flyout/convert", new { ids = new[] { (string)satoria["id"]! }, builder = "Tester", replace = true });
        var token = (string)again["results"]![0]!["replaced"]!["token"]!;
        Assert.True(File.Exists(Path.Combine(_dir, "data", "Deleted planes", token)));
        Assert.Equal(read.Design.Id, OplanePackage.Read(file).Design.Id);   // the same plane, so it keeps its id
        Assert.Single(Directory.GetFiles(Planes, "Satoria*"));
        list = await Get("/api/flyout");
        Assert.NotNull((string?)list["craft"]!.Single(c => (string)c["name"]! == "Satoria-LF-2")["converted_plane_id"]);

        // Open it in the builder, edit an engine, save.
        var id = (string)results[0]["plane"]!["id"]!;
        var plane = await Get($"/api/planes/{id}");
        Assert.False((bool)plane["from_builder_data"]!);
        var meshSet = (string)plane["mesh_set"]!;
        Assert.NotEmpty(meshSet);
        var spec = plane["spec"]!.ToObject<JObject>()!;
        spec["engines"]![0]!["power_kw"] = 700;
        var analysis = await Send(HttpMethod.Post, "/api/analyze", new JObject { ["spec"] = spec, ["mesh_set"] = meshSet });
        Assert.True((double)analysis["flight"]!["gross_mass_kg"]! > 500);
        Assert.Equal("COIN", (string)analysis["donor"]!);
        var unknown = await Send(HttpMethod.Post, "/api/analyze", new JObject { ["spec"] = spec, ["mesh_set"] = "nope" }, HttpStatusCode.Conflict);
        Assert.Equal("mesh_set_unknown", (string)unknown["error"]!);
        var ms = await Send(HttpMethod.Post, "/api/meshsets", new JObject { ["meshes"] = plane["source_meshes"] });
        Assert.Equal(meshSet, (string)ms["mesh_set"]!);

        var saved = await Send(HttpMethod.Put, $"/api/planes/{id}", new JObject { ["spec"] = spec, ["mesh_set"] = meshSet });
        Assert.True((bool)saved["plane"]!["has_builder_data"]!);
        var reopened = await Get($"/api/planes/{id}");
        Assert.True((bool)reopened["from_builder_data"]!);
        Assert.Equal(700, (double)reopened["spec"]!["engines"]![0]!["power_kw"]!);
        Assert.Equal("Tester", (string)reopened["spec"]!["origin"]!["builder"]!);
        Assert.NotEmpty(reopened["report"]!);  // the conversion report is kept

        // Share: export to Downloads and to a chosen folder.
        var ex = await Send(HttpMethod.Post, $"/api/planes/{id}/export", new { to = "downloads" });
        Assert.True(File.Exists((string)ex["path"]!));
        var ex2 = await Send(HttpMethod.Post, $"/api/planes/{id}/export", new { to = "folder", folder = Path.Combine(_dir, "shared") });
        Assert.StartsWith(Path.Combine(_dir, "shared"), (string)ex2["path"]!);
        var bytes = await _http.GetByteArrayAsync($"/api/planes/{id}/file");
        Assert.Equal((byte)'P', bytes[0]);

        // Import the very same file back: nothing is copied.
        var same = await Upload("from a friend.oplane", bytes);
        Assert.Equal("same", (string)same["outcome"]!);
        Assert.Contains("already have", (string)same["plane"]!["warning"]!);
        Assert.Single(Directory.GetFiles(Planes, "*.oplane"));
        // Another version of the same plane (same id, other contents): a second copy with a new file name, and a warning.
        var imp = await Upload("from a friend.oplane", WithDescription(bytes, "a friend's version"));
        Assert.Contains("same plane", (string)imp["plane"]!["warning"]!);   // same id as the plane already here
        Assert.Equal("plane", (string)imp["kind"]!);
        Assert.Equal("added", (string)imp["outcome"]!);
        Assert.Equal(2, Directory.GetFiles(Planes, "*.oplane").Length);

        // Duplicate, delete, restore.
        var dup = await Send(HttpMethod.Post, $"/api/planes/{id}/duplicate");
        Assert.Equal("Satoria-LF-2 copy", (string)dup["plane"]!["name"]!);
        var del = await Send(HttpMethod.Delete, $"/api/planes/{dup["plane"]!["id"]}");
        Assert.False(File.Exists((string)dup["plane"]!["path"]!));
        Assert.True(File.Exists(Path.Combine(_dir, "data", "Deleted planes", (string)del["token"]!)));
        var restored = await Send(HttpMethod.Post, "/api/planes/restore", new { token = (string)del["token"]! });
        Assert.True(File.Exists((string)restored["plane"]!["path"]!));
        await Send(HttpMethod.Post, $"/api/planes/{id}/reveal");
        var thumbPng = await _http.GetAsync($"/api/planes/{id}/thumb");
        Assert.Equal(HttpStatusCode.OK, thumbPng.StatusCode);
        await Get("/api/planes/bm9wZS5vcGxhbmU", HttpStatusCode.NotFound);
        await Get("/api/planes/Li4vLi4vZXZpbC5vcGxhbmU", HttpStatusCode.NotFound); // ../../evil.oplane
    }

    [Fact]
    public async Task TemplatesBuildSaveAutoGearAndDraft()
    {
        var templates = await Get("/api/templates");
        Assert.Equal(new[] { "trainer", "fighter", "bomber", "blank" }, templates.Select(t => (string)t["id"]!));
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/templates/fighter/thumb")).StatusCode);
        var spec = (await Get("/api/templates/fighter"))["spec"]!;
        var a = await Send(HttpMethod.Post, "/api/analyze", new JObject { ["spec"] = spec, ["mesh_set"] = "", ["include_design"] = true });
        Assert.False((bool)a["flight"]!["has_problems"]!);
        Assert.Equal(5, a["design"]!["wings"]!.Count());   // wing and tailplane pairs, one fin
        Assert.NotNull(a["ground_y"]);

        var gear = await Send(HttpMethod.Post, "/api/autogear", new JObject { ["spec"] = spec, ["mesh_set"] = "" });
        Assert.Equal(2, gear["gear"]!.Count());

        Assert.Matches("^[0-9a-f]{16}$", (string)spec["id"]!);   // a new plane gets its own id
        await Send(HttpMethod.Put, "/api/drafts/new-test1", new JObject { ["spec"] = spec, ["mesh_set"] = "", ["plane_id"] = null });
        var d = await Get("/api/drafts/new-test1");
        Assert.Equal("Fighter", (string)d["draft"]!["spec"]!["name"]!);

        var saved = await Send(HttpMethod.Post, "/api/planes", new JObject { ["spec"] = spec, ["mesh_set"] = "" });
        var path = (string)saved["plane"]!["path"]!;
        Assert.True(File.Exists(path));
        var opened = BuilderPackage.Read(path);
        Assert.True(opened.FromBuilderData);
        Assert.Equal("Fighter1", opened.Contents.Design.Donor);

        // Renaming on save renames the file.
        var s2 = (JObject)spec.DeepClone();
        s2["name"] = "Viper";
        var renamed = await Send(HttpMethod.Put, $"/api/planes/{saved["plane"]!["id"]}", new JObject { ["spec"] = s2, ["mesh_set"] = "" });
        Assert.Equal("Viper.oplane", (string)renamed["plane"]!["file_name"]!);
        Assert.False(File.Exists(path));

        Assert.Equal((string)spec["id"]!, opened.Contents.Design.Id);   // the first save keeps the new plane's id
        Assert.Single(Directory.GetFiles(Planes, "Viper*.oplane"));    // the rename left no second copy

        // "Save as" (a new plane from the same spec) gets a new id.
        var copy = await Send(HttpMethod.Post, "/api/planes", new JObject { ["spec"] = s2, ["mesh_set"] = "" });
        Assert.NotEqual((string?)renamed["plane"]!["design_id"], (string?)copy["plane"]!["design_id"]);

        // Drafts: one per plane, so a draft of another plane never replaces this one.
        var planeId = (string)renamed["plane"]!["id"]!;
        await Send(HttpMethod.Put, $"/api/drafts/{planeId}", new JObject { ["spec"] = s2, ["mesh_set"] = "", ["plane_id"] = planeId });
        var drafts = (await Get("/api/drafts"))["drafts"]!.ToArray();
        Assert.Equal(2, drafts.Length);
        Assert.Contains(drafts, x => (string)x["id"]! == planeId && (string)x["plane_name"]! == "Viper");
        Assert.Contains(drafts, x => (string)x["id"]! == "new-test1" && x["plane_id"]!.Type == JTokenType.Null);
        await Send(HttpMethod.Put, "/api/drafts/..%2Fevil", new JObject { ["spec"] = s2, ["mesh_set"] = "" }, HttpStatusCode.BadRequest);
        // A plane with a long or non-Latin name has a long id (its path in base64): its draft is still kept.
        var longId = PlaneLibraryIdFor(new string('Ж', 120) + ".oplane");
        Assert.True(longId.Length > 200);
        await Send(HttpMethod.Put, $"/api/drafts/{longId}", new JObject { ["spec"] = s2, ["mesh_set"] = "", ["plane_id"] = null });
        Assert.NotNull((await Get($"/api/drafts/{longId}"))["draft"]);
        await Send(HttpMethod.Delete, $"/api/drafts/{longId}");

        await Send(HttpMethod.Delete, "/api/drafts/new-test1");
        Assert.Equal(JTokenType.Null, (await Get("/api/drafts/new-test1"))["draft"]!.Type);
        Assert.Single((await Get("/api/drafts"))["drafts"]!);
    }

    [Fact]
    public async Task ImportsZippedCraftAndRefusesJunk()
    {
        var bad = await Upload("notes.zip", Encoding.UTF8.GetBytes("hello"), HttpStatusCode.BadRequest);
        Assert.Contains("is neither", (string)bad["error"]!);
        var badPlane = await Upload("broken.oplane", Encoding.UTF8.GetBytes("hello"), HttpStatusCode.BadRequest);
        Assert.Contains("damaged", (string)badPlane["error"]!);
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("no craft here");
            var empty = await Upload("empty.zip", ms.ToArray(), HttpStatusCode.BadRequest);
            Assert.Contains("data.txt", (string)empty["error"]!);
        }

        var folder = TestPaths.CraftFolder("Ta-183");
        if (folder == null) return;
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, true))
            foreach (var f in new[] { "data.txt", "meta.txt" })
                zip.CreateEntryFromFile(Path.Combine(folder, f), "Ta-183/" + f);
        var r = await Upload("Ta-183.zip", zipped.ToArray());
        Assert.Equal("craft", (string)r["kind"]!);
        var id = (string)r["craft"]![0]!["id"]!;
        var list = await Get("/api/flyout");
        Assert.Contains(list["craft"]!, c => (string)c["id"]! == id && (string)c["source"]! == "imported");
        var conv = await Send(HttpMethod.Post, "/api/flyout/convert", new { ids = new[] { id }, builder = "Tester" });
        Assert.True((bool)conv["results"]![0]!["ok"]!);
    }

    private async Task<JToken> PostRaw(string url, byte[] bytes, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var r = await _http.PostAsync(url, content);
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expect, $"POST {url}: {(int)r.StatusCode} {text}");
        return JToken.Parse(text);
    }

    private static byte[] TinyPng(int w, int h)
    {
        var b = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange(new[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, (byte)8, (byte)6, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0 });
        return b.ToArray();
    }

    [Fact]
    public async Task ShapePartsMeshEditsAndReferencePictures()
    {
        // A primitive, then Blender-style edits on it.
        var box = (await Send(HttpMethod.Post, "/api/shapes/primitive", new { kind = "box", size = new[] { 1.0, 0.5, 2.0 } }))["shape"]!;
        Assert.StartsWith("shape-", (string)box["id"]!);
        Assert.Equal(8, box["vertices"]!.Count());
        Assert.Equal(6, box["faces"]!.Count());
        await Send(HttpMethod.Post, "/api/shapes/primitive", new { kind = "teapot" }, HttpStatusCode.BadRequest);

        var ex = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "extrude", selection = new { mode = "face", faces = new[] { 0 } } });
        Assert.Equal(10, ex["shape"]!["faces"]!.Count());
        Assert.Equal(4, ex["motion"]!.Count());
        Assert.Equal(new[] { 0 }, ex["selection"]!["faces"]!.Select(x => (int)x));
        Assert.Equal(4, ex["selection"]!["verts"]!.Count());

        var inset = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "inset", selection = new { mode = "vert", verts = box["faces"]![2]! } });
        Assert.True((double)inset["max_amount"]! > 0);
        Assert.Equal(10, inset["shape"]!["faces"]!.Count());

        var f0 = box["faces"]![0]!.Select(x => (int)x).ToArray();
        var cut = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "loopcut", @params = new { edge = new[] { f0[0], f0[1] }, cuts = 2 } });
        Assert.Equal(6 + 8, cut["shape"]!["faces"]!.Count());
        Assert.Equal(8, cut["selection"]!["verts"]!.Count());

        var merged = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "merge", selection = new { mode = "edge", edges = new[] { new[] { f0[0], f0[1] } } } });
        Assert.Equal(7, merged["shape"]!["vertices"]!.Count());
        var deleted = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "delete", selection = new { mode = "face", faces = new[] { 1 } } });
        Assert.Contains("hole", (string)deleted["note"]!);
        var smooth = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "subdivide" });
        Assert.Equal(24, smooth["shape"]!["faces"]!.Count());
        var none = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "extrude", selection = new { mode = "face", faces = Array.Empty<int>() } }, HttpStatusCode.BadRequest);
        Assert.Contains("Select some faces", (string)none["error"]!);
        await Send(HttpMethod.Post, "/api/shapes/op", new { shape = box, op = "fold" }, HttpStatusCode.BadRequest);
        var broken = (JObject)box.DeepClone();
        broken["faces"]![0]![0] = 99;
        var damaged = await Send(HttpMethod.Post, "/api/shapes/op", new { shape = broken, op = "subdivide" }, HttpStatusCode.BadRequest);
        Assert.Contains("damaged", (string)damaged["error"]!);

        // .obj import.
        var obj = "v -1 -1 -1\nv 1 -1 -1\nv 1 1 -1\nv -1 1 -1\nv -1 -1 1\nv 1 -1 1\nv 1 1 1\nv -1 1 1\nf 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 4 8 7 3\nf 1 5 8 4\nf 2 3 7 6\n";
        var imp = await PostRaw("/api/shapes/import-obj?name=crate.obj", Encoding.UTF8.GetBytes(obj));
        Assert.Equal(12, (int)imp["triangles"]!);
        Assert.Equal("crate", (string)imp["shape"]!["name"]!);
        Assert.Equal("obj", (string)imp["shape"]!["source"]!);
        await PostRaw("/api/shapes/import-obj?name=x.obj", Encoding.UTF8.GetBytes("hello"), HttpStatusCode.BadRequest);

        // A fixed body made editable.
        var loftMesh = FuselageLoft.Build(Templates.Fighter().Fuselages[0]);
        var ms = (string)(await Send(HttpMethod.Post, "/api/meshsets", new { meshes = new[] { loftMesh } }))["mesh_set"]!;
        var fixedBody = new FuselageSpec { Id = "fus1", Name = "Body", Kind = FuselageSpec.MeshKind, MeshIndex = 0, Position = new double[] { 0, 1, 0 }, Mirror = false };
        var editable = (await Send(HttpMethod.Post, "/api/shapes/from-mesh", new { mesh_set = ms, fuselage = fixedBody }))["shape"]!;
        Assert.Equal("mesh", (string)editable["source"]!);
        Assert.True(editable["faces"]!.Count() > 100);
        Assert.True(editable["vertices"]!.Max(v => (double)v[1]!) > 1.5);   // the move was baked in
        await Send(HttpMethod.Post, "/api/shapes/from-mesh", new { mesh_set = "nope", fuselage = fixedBody }, HttpStatusCode.Conflict);

        // Reference pictures: stored by content, refused when they aren't pictures.
        var png = TinyPng(640, 320);
        var up = await PostRaw("/api/references?name=side.png", png);
        var image = (string)up["image"]!;
        Assert.Matches("^[0-9a-f]{40}\\.png$", image);
        Assert.Equal(640, (int)up["width"]!);
        Assert.Equal(png, await _http.GetByteArrayAsync($"/api/references/{image}"));
        await PostRaw("/api/references", Encoding.UTF8.GetBytes("not a picture"), HttpStatusCode.BadRequest);
        await Get("/api/references/..%2F..%2Fsettings.json", HttpStatusCode.NotFound);

        // Save a plane with a shape part and a reference; it reopens with both, and shares without the picture by default.
        var spec = (JObject)(await Get("/api/templates/fighter"))["spec"]!;
        Assert.Equal(PlaneSpec.CurrentFormat, (string)spec["format"]!);
        int kitShapes = ((JArray)spec["shapes"]!).Count;   // the template's canopy, seat and panel
        ((JArray)spec["shapes"]!).Add(imp["shape"]!);
        ((JArray)spec["references"]!).Add(new JObject { ["id"] = "ref1", ["name"] = "Side", ["image"] = image, ["view"] = "side", ["center"] = new JArray(0, 0), ["width_m"] = 14 });
        ((JArray)spec["references"]!).Add(new JObject { ["id"] = "ref2", ["name"] = "Missing", ["image"] = new string('a', 40) + ".png", ["view"] = "top" });
        var a = await Send(HttpMethod.Post, "/api/analyze", new JObject { ["spec"] = spec, ["mesh_set"] = "", ["include_design"] = true });
        Assert.Equal(2 + kitShapes, a["design"]!["fuselages"]!.Count());   // the loft, the template's kit and the crate
        var saved = await Send(HttpMethod.Post, "/api/planes", new JObject { ["spec"] = spec, ["mesh_set"] = "" });
        var path = (string)saved["plane"]!["path"]!;
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("references/" + image));
            Assert.Equal(png.Length, zip.GetEntry("references/" + image)!.Length);
        }
        var id = (string)saved["plane"]!["id"]!;
        var plane = await Get($"/api/planes/{id}");
        Assert.True((bool)plane["from_builder_data"]!);
        Assert.Equal(kitShapes + 1, plane["spec"]!["shapes"]!.Count());
        Assert.Contains(plane["spec"]!["shapes"]!, s => (string)s["name"]! == "crate");
        Assert.Single(plane["spec"]!["references"]!);   // the one whose picture was missing is dropped
        Assert.Equal(image, (string)plane["spec"]!["references"]![0]!["image"]!);

        var shared = (string)(await Send(HttpMethod.Post, $"/api/planes/{id}/export", new { to = "downloads" }))["path"]!;
        var withRefs = (string)(await Send(HttpMethod.Post, $"/api/planes/{id}/export", new { to = "downloads", include_references = true }))["path"]!;
        using (var zip = ZipFile.OpenRead(shared)) Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("references/"));
        using (var zip = ZipFile.OpenRead(withRefs)) Assert.Contains(zip.Entries, e => e.FullName == "references/" + image);
        var sharedOpen = BuilderPackage.Read(shared);
        Assert.True(sharedOpen.FromBuilderData);   // aircraft.json untouched, so the builder data still matches
        Assert.Empty(sharedOpen.Spec.References);
        Assert.Equal(kitShapes + 1, sharedOpen.Spec.Shapes.Count);

        var dup = await Send(HttpMethod.Post, $"/api/planes/{id}/duplicate");
        using (var zip = ZipFile.OpenRead((string)dup["plane"]!["path"]!)) Assert.NotNull(zip.GetEntry("references/" + image));
    }

    [Fact]
    public async Task ConvertingNeverOverwritesOtherPlanes()
    {
        if (TestPaths.CraftRoot == null) { _out.WriteLine("samples-private not found; skipped."); return; }
        var satoria = (await Get("/api/flyout"))["craft"]!.Single(c => (string)c["name"]! == "Satoria-LF-2");
        var sid = (string)satoria["id"]!;
        // A plane built in the builder that happens to have the craft's name.
        var spec = (JObject)(await Get("/api/templates/fighter"))["spec"]!;
        spec["name"] = "Satoria-LF-2";
        var built = await Send(HttpMethod.Post, "/api/planes", new JObject { ["spec"] = spec, ["mesh_set"] = "" });
        var builtPath = (string)built["plane"]!["path"]!;
        var builtBytes = File.ReadAllBytes(builtPath);

        // Without "replace" (the default), every conversion gets its own file; the built plane is untouched.
        await Send(HttpMethod.Post, "/api/flyout/convert", new { ids = new[] { sid }, builder = "Tester" });
        await Send(HttpMethod.Post, "/api/flyout/convert", new { ids = new[] { sid }, builder = "Tester" });
        Assert.Equal(3, Directory.GetFiles(Planes, "Satoria*.oplane").Length);
        Assert.Equal(builtBytes, File.ReadAllBytes(builtPath));

        // With "replace", only planes converted from this craft are replaced (each at most once per batch), never the built one.
        var conv = await Send(HttpMethod.Post, "/api/flyout/convert", new { ids = new[] { sid, sid }, builder = "Tester", replace = true });
        Assert.All(conv["results"]!, r => Assert.NotNull((string?)r["replaced"]?["token"]));
        Assert.Equal(3, Directory.GetFiles(Planes, "Satoria*.oplane").Length);
        Assert.Equal(builtBytes, File.ReadAllBytes(builtPath));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dir, "data", "Deleted planes")).Length);
        // The replaced planes can be brought back.
        var restored = await Send(HttpMethod.Post, "/api/planes/restore", new { token = (string)conv["results"]![0]!["replaced"]!["token"]! });
        Assert.True(File.Exists((string)restored["plane"]!["path"]!));
    }

    [Fact]
    public async Task RestoreAndRevealStayInsideTheirFolders()
    {
        foreach (var token in new[] { "C:OpenPlane.dll", "..\\x.oplane", "../x.oplane", "20260101-000000-000_..\\x.oplane", "20260101-000000-000_x.dll", "x.oplane", "" })
            await Send(HttpMethod.Post, "/api/planes/restore", new { token }, HttpStatusCode.NotFound);

        Directory.CreateDirectory(Planes);
        var sibling = Planes + "2"; // shares the planes folder's name as a prefix
        Directory.CreateDirectory(sibling);
        await Send(HttpMethod.Post, "/api/reveal-path", new { path = sibling }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/reveal-path", new { path = Planes });
        Assert.True(Api.Endpoints.IsInside(Path.Combine(Planes, "a.oplane"), Planes));
        Assert.False(Api.Endpoints.IsInside(sibling, Planes));
    }

    [Fact]
    public async Task ACustomPlanesFolderIsWrittenIntoTheModConfig()
    {
        // The test host's planes folder (from --planes-dir) isn't the default, so the mod must be told about it.
        var config = Path.Combine(Game, "BepInEx", "config", ModInstaller.ModConfigFile);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "## Settings file was created by plugin OpenPlane v0.1.0\r\n## Plugin GUID: com.meta.openplane\r\n\r\n[Developer]\r\n\r\nSelfTest = true\r\n\r\n[Paths]\r\n\r\n## Optional second folder\r\n# Setting type: String\r\n# Default value: \r\nExtraAircraftFolder = \r\n\r\n[Testing]\r\n\r\nCloneTest = false\r\n");
        var cur = (await Get("/api/settings"))["settings"]!;
        var put = await Send(HttpMethod.Put, "/api/settings", cur);
        Assert.Contains("also load planes", (string)put["mod_config_note"]!);
        Assert.Equal(Path.GetFullPath(Planes), ModConfig.Read(config, "Paths", "ExtraAircraftFolder"));
        var text = File.ReadAllText(config);
        Assert.Contains("SelfTest = true\r\n", text);       // other keys and line endings are kept
        Assert.Contains("CloneTest = false\r\n", text);
        Assert.Single(text.Split('\n'), l => l.StartsWith("ExtraAircraftFolder"));
    }

    [Fact]
    public async Task BepInExMustBeCompleteAndNommChoicesAreRespected()
    {
        var core = Path.Combine(Game, "BepInEx", "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "x");
        var st = await Get("/api/mod/status");
        Assert.False((bool)st["bepinex_installed"]!);
        Assert.True((bool)st["bepinex_incomplete"]!);
        Assert.Contains("incomplete", (string)st["summary"]!);
        File.WriteAllText(Path.Combine(Game, "winhttp.dll"), "x");
        File.WriteAllText(Path.Combine(Game, "doorstop_config.ini"), "[General]\nenabled = true\n");
        st = await Get("/api/mod/status");
        Assert.True((bool)st["bepinex_installed"]!);

        // Someone else's Blueprinter older than the one the mod needs is reported.
        var theirs = Path.Combine(Game, "BepInEx", "plugins", "nikkorap-Blueprinter");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "Blueprinter_2.0.3.dll"), "not a real dll");
        st = await Get("/api/mod/status");
        Assert.True((bool)st["blueprinter_too_old"]!);
        Assert.NotEmpty(st["warnings"]!);

        // OpenPlane turned off in NOMM: installing would make a second copy, so it's refused.
        if (ModBundle() == null) return;
        Directory.CreateDirectory(Path.Combine(Game, "BepInEx", "disabledPlugins", "OpenPlane"));
        var job = await Send(HttpMethod.Post, "/api/mod/install");
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.Equal("failed", (string)job["state"]!);
        Assert.Contains("NOMM", (string)job["error"]!);
        Assert.False(Directory.Exists(Path.Combine(Game, "BepInEx", "plugins", "OpenPlane")));
    }

    [Fact]
    public async Task InstallAndRemoveTheModInAFakeGame()
    {
        if (ModBundle() == null) { _out.WriteLine("The mod isn't built on this machine; install test skipped."); return; }
        var job = await Send(HttpMethod.Post, "/api/mod/install");
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.True((string)job["state"]! == "done", job.ToString());
        Assert.True((bool)job["partial"]!);   // no NuclearOption.exe here, so BepInEx is skipped with a message
        Assert.Contains(job["steps"]!, s => (string)s["state"]! == "skipped" && ((string?)s["detail"] ?? "").Contains("NuclearOption.exe"));
        var plugins = Path.Combine(Game, "BepInEx", "plugins");
        Assert.True(File.Exists(Path.Combine(plugins, "OpenPlane", "OpenPlane.Mod.dll")));
        Assert.True(File.Exists(Path.Combine(plugins, "OpenPlane", "OpenPlane.Core.dll")));
        Assert.True(File.Exists(Path.Combine(plugins, "Blueprinter", "LICENSE.txt")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(plugins, "Blueprinter"), "Blueprinter*.dll"));
        var st = await Get("/api/mod/status");
        Assert.True((bool)st["mod_installed"]!);
        Assert.False((bool)st["bepinex_installed"]!);
        Assert.Contains("BepInEx", (string)st["summary"]!);

        // Another mod's file is never touched by Remove.
        File.WriteAllText(Path.Combine(plugins, "other.dll"), "x");
        job = await Send(HttpMethod.Post, "/api/mod/remove", new { remove_blueprinter = true });
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.True((string)job["state"]! == "done", job.ToString());
        Assert.False(Directory.Exists(Path.Combine(plugins, "OpenPlane")));
        Assert.False(Directory.Exists(Path.Combine(plugins, "Blueprinter")));
        Assert.True(File.Exists(Path.Combine(plugins, "other.dll")));

        // A Blueprinter the player (or NOMM) put in plugins/Blueprinter is neither replaced nor removed.
        var mine = Path.Combine(plugins, "Blueprinter");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "Blueprinter_9.9.9.dll"), "players own copy");
        job = await Send(HttpMethod.Post, "/api/mod/install");
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.True((string)job["state"]! == "done", job.ToString());
        Assert.Equal(new[] { "Blueprinter_9.9.9.dll" }, Directory.GetFiles(mine, "*.dll").Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(mine, ModInstaller.OwnerMarker)));
        job = await Send(HttpMethod.Post, "/api/mod/remove", new { remove_blueprinter = true });
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.True((string)job["state"]! == "done", job.ToString());
        Assert.True(File.Exists(Path.Combine(mine, "Blueprinter_9.9.9.dll")));
        Assert.False(Directory.Exists(Path.Combine(plugins, "OpenPlane")));

        // A copy of the OpenPlane mod that NOMM installed (it writes meta.json) is neither overwritten nor removed.
        var nomm = Path.Combine(plugins, "OpenPlane");
        Directory.CreateDirectory(nomm);
        File.WriteAllText(Path.Combine(nomm, "meta.json"), "{}");
        File.WriteAllText(Path.Combine(nomm, "OpenPlane.Mod.dll"), "nomm copy");
        job = await Send(HttpMethod.Post, "/api/mod/install");
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.Equal("nomm copy", File.ReadAllText(Path.Combine(nomm, "OpenPlane.Mod.dll")));
        job = await Send(HttpMethod.Post, "/api/mod/remove", new { remove_blueprinter = false });
        for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
        Assert.True(File.Exists(Path.Combine(nomm, "meta.json")));
        Assert.True(File.Exists(Path.Combine(nomm, "OpenPlane.Mod.dll")));
        Directory.Delete(nomm, true);
    }

    [Fact]
    public async Task ACopyNommInstalledUnderItsIdIsFoundAndLeftAlone()
    {
        // NOMM unpacks a mod into BepInEx/plugins/<NOMNOM id> and writes meta.json there. (With NOMM, BepInEx is NOMM's
        // to install, so it's there already.)
        Directory.CreateDirectory(Path.Combine(Game, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(Game, "BepInEx", "core", "BepInEx.dll"), "x");
        File.WriteAllText(Path.Combine(Game, "winhttp.dll"), "x");
        File.WriteAllText(Path.Combine(Game, "doorstop_config.ini"), "[General]\nenabled = true\n");
        var plugins = Path.Combine(Game, "BepInEx", "plugins");
        var nomm = Path.Combine(plugins, ModInstaller.NommId);
        Directory.CreateDirectory(nomm);
        File.WriteAllText(Path.Combine(nomm, "meta.json"), "{\"id\":\"com.meta.openplane\",\"artifact\":{\"version\":\"0.1.0\"}}");
        File.WriteAllText(Path.Combine(nomm, "OpenPlane.Mod.dll"), "nomm copy");
        File.WriteAllText(Path.Combine(nomm, "OpenPlane.Core.dll"), "nomm copy");
        var st = await Get("/api/mod/status");
        Assert.True((bool)st["mod_installed"]!);
        Assert.True((bool)st["mod_managed_elsewhere"]!);
        Assert.False((bool)st["update_available"]!);
        Assert.EndsWith(ModInstaller.NommId, (string)st["mod_folder"]!);

        if (ModBundle() != null)
        {
            // Installing adds no second copy next to NOMM's, and Remove leaves NOMM's copy alone.
            var job = await Send(HttpMethod.Post, "/api/mod/install");
            for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
            Assert.True((string)job["state"]! == "done", job.ToString());
            Assert.False(File.Exists(Path.Combine(plugins, ModInstaller.PluginFolder, "OpenPlane.Mod.dll")));
            Assert.Contains(job["next_steps"]!, n => ((string)n!).Contains("NOMM"));
            job = await Send(HttpMethod.Post, "/api/mod/remove", new { remove_blueprinter = true });
            for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
            Assert.Equal("nomm copy", File.ReadAllText(Path.Combine(nomm, "OpenPlane.Mod.dll")));
        }

        // Turned off in NOMM (moved to disabledPlugins/<id>): installing would make a second copy, so it's refused.
        Directory.Delete(nomm, true);
        var off = Path.Combine(Game, "BepInEx", "disabledPlugins", ModInstaller.NommId);
        Directory.CreateDirectory(off);
        File.WriteAllText(Path.Combine(off, "OpenPlane.Mod.dll"), "nomm copy");
        st = await Get("/api/mod/status");
        Assert.True((bool)st["disabled_in_nomm"]!);
        Assert.False((bool)st["mod_installed"]!);
        if (ModBundle() != null)
        {
            var job = await Send(HttpMethod.Post, "/api/mod/install");
            for (int i = 0; i < 100 && (string)job["state"]! == "running"; i++) { await Task.Delay(100); job = await Get($"/api/jobs/{job["id"]}"); }
            Assert.Equal("failed", (string)job["state"]!);
            Assert.False(Directory.Exists(Path.Combine(plugins, ModInstaller.PluginFolder)));
        }
        Directory.Delete(Path.Combine(Game, "BepInEx", "disabledPlugins"), true);
    }

    [Fact]
    public async Task GameReportIsReadFromTheGameFolder()
    {
        var none = await Get("/api/game-report");
        Assert.False((bool)none["found"]!);
        var file = Path.Combine(Game, "BepInEx", "OpenPlane-planes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"format\":\"openplane-game-report/1\",\"planes\":[{\"file\":\"Kestrel.oplane\",\"name\":\"Kestrel\",\"changes\":[\"moved its lift\"],\"warnings\":[],\"not_offered_at\":[\"highway strip 1 (889 m runway)\"]}]}");
        try
        {
            var r = await Get("/api/game-report");
            Assert.True((bool)r["found"]!);
            Assert.Equal("Kestrel.oplane", (string)r["report"]!["planes"]![0]!["file"]!);
        }
        finally { File.Delete(file); }
    }

    private static string PlaneLibraryIdFor(string relativePath) => OpenPlane.App.Services.PlaneLibrary.IdFor(relativePath);

    /// <summary>A copy of a plane file with another description: the same plane (same id), other contents.</summary>
    private static byte[] WithDescription(byte[] plane, string description)
    {
        using var ms = new MemoryStream();
        ms.Write(plane, 0, plane.Length);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry(OplanePackage.DesignEntry)!;
            string json;
            using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
            var o = JObject.Parse(json);
            o["description"] = description;
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry(OplanePackage.DesignEntry).Open());
            w.Write(o.ToString());
        }
        return ms.ToArray();
    }

    private async Task<JToken> SaveTemplate(string template, string name)
    {
        var spec = (JObject)(await Get($"/api/templates/{template}"))["spec"]!;
        spec["name"] = name;
        return (await Send(HttpMethod.Post, "/api/planes", new JObject { ["spec"] = spec, ["mesh_set"] = "" }))["plane"]!;
    }

    [Fact]
    public async Task PlanePacksGoOutAndComeBackIn()
    {
        var a = await SaveTemplate("fighter", "Kite");
        var b = await SaveTemplate("bomber", "Heron");
        await SaveTemplate("trainer", "Wren");

        // A pack of two chosen planes, then a pack of all of them.
        var two = await Send(HttpMethod.Post, "/api/planes/export-pack", new { ids = new[] { (string)a["id"]!, (string)b["id"]! }, to = "downloads", name = "Kite and Heron" });
        var path = (string)two["path"]!;
        Assert.EndsWith("Kite and Heron.zip", path);
        Assert.Equal(2, (int)two["count"]!);
        using (var zip = ZipFile.OpenRead(path))
            Assert.Equal(new[] { "Heron.oplane", "Kite.oplane" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n));
        var all = await Send(HttpMethod.Post, "/api/planes/export-pack", new { all = true, to = "folder", folder = Path.Combine(_dir, "packs"), name = "../../everything" });
        Assert.Equal(3, (int)all["count"]!);
        Assert.StartsWith(Path.Combine(_dir, "packs"), (string)all["path"]!);   // the name can't climb out of the folder
        await Send(HttpMethod.Post, "/api/planes/export-pack", new { ids = Array.Empty<string>(), to = "downloads" }, HttpStatusCode.BadRequest);

        // Importing the pack here: both planes are already here, unchanged, so nothing is copied.
        var back = await Upload("Kite and Heron.zip", File.ReadAllBytes(path));
        Assert.Equal("pack", (string)back["kind"]!);
        Assert.All(back["results"]!, r => Assert.Equal("same", (string)r["outcome"]!));
        Assert.Equal(3, Directory.GetFiles(Planes, "*.oplane").Length);

        // A host's pack with another version of Kite replaces the local Kite (kept in Deleted planes) and adds a new plane.
        byte[] packBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var kite = WithDescription(File.ReadAllBytes((string)a["path"]!), "the host's version");
                using (var s = zip.CreateEntry("planes/Kite.oplane").Open()) s.Write(kite);
                var osprey = await SaveTemplate("fighter", "Osprey");
                var ospreyBytes = File.ReadAllBytes((string)osprey["path"]!);
                await Send(HttpMethod.Delete, $"/api/planes/{osprey["id"]}");
                using (var s = zip.CreateEntry("planes/Osprey.oplane").Open()) s.Write(ospreyBytes);
                using (var w = new StreamWriter(zip.CreateEntry("planes/Broken.oplane").Open())) w.Write("not a plane at all");
                using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("from the host");
            }
            packBytes = ms.ToArray();
        }
        var hostPack = await Upload("Host planes.zip", packBytes);
        var all3 = hostPack["results"]!.ToArray();
        Assert.Equal(3, all3.Length);
        // The damaged plane is reported on its own; the others still come in.
        var broken = all3.Single(r => (string)r["outcome"]! == "error");
        Assert.Equal("Broken.oplane", (string)broken["file"]!);
        Assert.Contains("Broken.oplane", (string)broken["error"]!);
        var results = all3.Where(r => (string)r["outcome"]! != "error").ToArray();
        var kiteResult = results.Single(r => (string)r["plane"]!["name"]! == "Kite");
        Assert.Equal("replaced", (string)kiteResult["outcome"]!);
        Assert.False((bool)kiteResult["replaced_newer"]!);   // the host's copy was saved after the local one
        Assert.True(File.Exists(Path.Combine(_dir, "data", "Deleted planes", (string)kiteResult["replaced"]!["token"]!)));
        Assert.Equal("added", (string)results.Single(r => (string)r["plane"]!["name"]! == "Osprey")["outcome"]!);
        Assert.Single(Directory.GetFiles(Planes, "Kite*.oplane"));
        Assert.Equal("the host's version", OplanePackage.Read(Path.Combine(Planes, "Kite.oplane")).Design.Description);

        // A zip with neither planes nor Flyout craft is still refused.
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                using (var w = new StreamWriter(zip.CreateEntry("notes/empty.oplane").Open())) { }
            await Upload("empty pack.zip", ms.ToArray(), HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task FilesOpenedFromWindowsAreImportedAndReported()
    {
        var a = await SaveTemplate("trainer", "Swift");
        var copy = Path.Combine(_dir, "incoming", "Swift from a friend.oplane");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, WithDescription(File.ReadAllBytes((string)a["path"]!), "edited by a friend"));
        File.WriteAllText(Path.Combine(_dir, "incoming", "notes.oplane"), "not a plane");
        await Send(HttpMethod.Post, "/api/open-files", new { paths = new[] { copy, Path.Combine(_dir, "incoming", "notes.oplane"), Path.Combine(_dir, "missing.oplane"), Path.Combine(_dir, "x.txt") } });
        var opened = (await Get("/api/opened"))["opened"]!.ToArray();
        Assert.Equal(4, opened.Length);
        Assert.True((bool)opened[0]["ok"]!);
        Assert.Equal("plane", (string)opened[0]["kind"]!);
        Assert.Equal("added", (string)opened[0]["outcome"]!);
        Assert.False((bool)opened[1]["ok"]!);
        Assert.Contains("wasn't found", (string)opened[2]["message"]!);
        Assert.Contains("none of these", (string)opened[3]["message"]!);
        Assert.Empty((await Get("/api/opened"))["opened"]!);   // each result is reported once

        // Requests that don't come from the page are refused (no X-OpenPlane header).
        using var bare = new HttpClient { BaseAddress = new Uri(_host.Url) };
        var refused = await bare.PostAsync("/api/open-files", new StringContent("{\"paths\":[]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task TestFlyAsksTheModForTheFlightAndStartsFreeFlight()
    {
        var plane = await SaveTemplate("fighter", "Gannet");
        var id = (string)plane["id"]!;
        var request = Path.Combine(Game, "BepInEx", "OpenPlane-testfly.json");

        // Without the mod there is nothing to hand the plane over: the game starts at its menu, and says why.
        var noMod = await Send(HttpMethod.Post, "/api/game/launch", new { test_fly = new { plane_id = id } });
        Assert.False((bool)noMod["test_fly"]!);
        Assert.Equal("menu", (string)noMod["how"]!);
        Assert.Contains("isn't installed", (string)noMod["note"]!);
        Assert.False(File.Exists(request));

        Directory.CreateDirectory(Path.Combine(Game, "BepInEx"));
        var cur = (await Get("/api/settings"))["settings"]!;
        cur["test_fly_start"] = "hangar";
        await Send(HttpMethod.Put, "/api/settings", cur);
        var r = await Send(HttpMethod.Post, "/api/game/launch", new { test_fly = new { plane_id = id } });
        Assert.True((bool)r["test_fly"]!);
        var json = JObject.Parse(File.ReadAllText(request));
        Assert.Equal((string)plane["design_id"]!, (string)json["design_id"]!);
        Assert.Equal("Gannet", (string)json["name"]!);
        Assert.Equal("runway", (string)json["start"]!);
        Assert.True(Math.Abs(((DateTime)json["requested_at"]!).ToUniversalTime().Subtract(DateTime.UtcNow).TotalMinutes) < 2);
        var history = (await Get("/api/status"))["external_history"]!.Select(x => (string)x!).ToList();
        if ((string)r["how"]! == "free-flight")
            Assert.Contains(history, h => h.StartsWith("start ") && h.Contains("-applaunch 2168680 -autoHost -mission \"Free Flight - Heartland\" -socket Offline -state SinglePlayer"));
        else
            Assert.Contains("open steam://rungameid/2168680", history);   // no Steam on this machine

        await Send(HttpMethod.Post, "/api/game/launch", new { test_fly = new { plane_id = "bm9uZS5vcGxhbmU" } }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StarterPlanesAreTheSameForEveryone()
    {
        var first = (await Send(HttpMethod.Post, "/api/planes/starters"))["results"]!.ToArray();
        Assert.Equal(new[] { "OP-1 Wren", "OP-2 Kite", "OP-3 Heron" }, first.Select(r => (string)r["plane"]!["name"]!));
        Assert.All(first, r => Assert.Equal("added", (string)r["outcome"]!));
        Assert.Equal(new[] { "57a47e4000000001", "57a47e4000000002", "57a47e4000000003" }, first.Select(r => (string)r["plane"]!["design_id"]!));
        // Adding them again changes nothing.
        var again = (await Send(HttpMethod.Post, "/api/planes/starters"))["results"]!.ToArray();
        Assert.All(again, r => Assert.Equal("same", (string)r["outcome"]!));
        Assert.Equal(3, Directory.GetFiles(Planes, "*.oplane").Length);
        // Exactly the plane the shared code makes (so the release's starter pack and every app agree).
        for (int i = 0; i < 3; i++)
        {
            var spec = Templates.Starter(i);
            var made = DesignGenerator.Generate(spec, new List<MeshData>());
            var tmp = Path.Combine(_dir, $"starter{i}.oplane");
            BuilderPackage.Write(tmp, spec, made, null, null);
            var file = Path.Combine(Planes, spec.Name + ".oplane");
            Assert.Equal(OplanePackage.Read(tmp, OplanePackage.ReadOptions.DesignOnly).ContentHash, OplanePackage.Read(file, OplanePackage.ReadOptions.DesignOnly).ContentHash);
            Assert.Equal("Meta", OplanePackage.Read(file).Design.Author);
        }
    }

    [Fact]
    public async Task ProblemReportIsSafeToPost()
    {
        await SaveTemplate("trainer", "Wren");
        var bep = Path.Combine(Game, "BepInEx");
        Directory.CreateDirectory(bep);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllText(Path.Combine(bep, "LogOutput.log"),
            "[Info   :   BepInEx] Loading [OpenPlane 0.1.0]\r\n[Info   :  OpenPlane] Planes folder: " + profile + "\\AppData\\LocalLow\\Shockfront\\NuclearOption\\OpenPlane\\Aircraft\r\n" +
            "[Message:   Unrelated] something else\r\n[Error  : Unity Log] NullReferenceException: boom\r\n" +
            "[Warning:  OpenPlane] Couldn't read " + profile + ". (Blueprinter saw " + profile + ", too)\r\n");
        File.WriteAllText(Path.Combine(bep, "OpenPlane-planes.json"),
            "{\"openplane_version\":\"0.1.0\",\"game_version\":\"0.34.6\",\"signature\":\"ab12cd34\",\"planes\":[{\"file\":\"Wren.oplane\",\"name\":\"Wren\",\"built_on\":\"CI-22 Cricket\",\"warnings\":[\"short tail\"],\"host_blocked\":\"over the limit\"}]}");
        var r = await Get("/api/problem-report");
        var text = (string)r["text"]!;
        Assert.Contains("OpenPlane 0.1.0", text);
        Assert.Contains("plane set ab12cd34", text);
        Assert.Contains("Wren (Wren.oplane): on CI-22 Cricket; host rules: over the limit", text);
        Assert.Contains("warning: short tail", text);
        Assert.Contains("NullReferenceException: boom", text);
        Assert.DoesNotContain("something else", text);   // other mods' lines stay out
        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%\\AppData\\LocalLow", text);
        Assert.Contains("Couldn't read %USERPROFILE%. (Blueprinter saw %USERPROFILE%, too)", text);   // before punctuation too
        Assert.EndsWith("/issues", (string)r["issues_url"]!);
    }

    [Fact]
    public async Task OnlyOpenPlanesOwnLinksOpen()
    {
        await Send(HttpMethod.Post, "/api/open-link", new { path = "https://github.com/metasprojects/OpenPlane/issues" });
        await Send(HttpMethod.Post, "/api/open-link", new { path = "https://github.com/metasprojects/OpenPlane" });
        await Send(HttpMethod.Post, "/api/open-link", new { path = "https://github.com/metasprojects/OpenPlaneX" }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/open-link", new { path = "https://github.com.evil.example/metasprojects/OpenPlane" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HostRulesAreWrittenToTheModConfig()
    {
        var none = await Get("/api/host-rules");
        Assert.False((bool)none["available"]!);   // no BepInEx in the fake game yet
        await Send(HttpMethod.Put, "/api/host-rules", new { allow = false }, HttpStatusCode.Conflict);

        var config = Path.Combine(Game, "BepInEx", "config", ModInstaller.ModConfigFile);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "[Developer]\r\n\r\nSelfTest = false\r\n");
        var r = await Get("/api/host-rules");
        Assert.True((bool)r["available"]!);
        Assert.True((bool)r["allow"]!);
        Assert.Equal(0, (double)r["max_thrust_to_weight"]!);

        r = await Send(HttpMethod.Put, "/api/host-rules", new { allow = false, max_thrust_to_weight = 1.25, max_hardpoints = 4 });
        Assert.False((bool)r["allow"]!);
        Assert.Equal(1.25, (double)r["max_thrust_to_weight"]!);
        Assert.Equal(4, (int)r["max_hardpoints"]!);
        Assert.Contains("restart", (string)r["note"]!);
        Assert.Equal("false", ModConfig.Read(config, "Multiplayer", "AllowOpenPlanePlanes"));
        Assert.Equal("1.25", ModConfig.Read(config, "Multiplayer", "MaxThrustToWeight"));
        Assert.Equal("4", ModConfig.Read(config, "Multiplayer", "MaxHardpoints"));
        var text = File.ReadAllText(config);
        Assert.Contains("SelfTest = false\r\n", text);                 // the rest stays
        Assert.Contains("# Setting type: Boolean\r\n", text);

        await Send(HttpMethod.Put, "/api/host-rules", new { allow = true, max_thrust_to_weight = -1 }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Put, "/api/host-rules", new { allow = true, max_hardpoints = 500 }, HttpStatusCode.BadRequest);
    }
}

/// <summary>Runs the built OpenPlane executable with --serve, as a player or a script would.</summary>
public class ServeProcessTests
{
    [Fact]
    public async Task ExecutableServesThePageAndStopsCleanly()
    {
        var exe = Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "src", "OpenPlane.App", "bin"), OperatingSystem.IsWindows() ? "OpenPlane.exe" : "OpenPlane", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (exe == null) return;
        var dir = TestPaths.NewScratch("serve");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            ArgumentList = { "--serve", "--port", "0", "--data-dir", Path.Combine(dir, "data"), "--planes-dir", Path.Combine(dir, "planes"), "--no-external" },
        };
        using var p = Process.Start(psi)!;
        try
        {
            string? url = null;
            var read = Task.Run(async () =>
            {
                while (url == null)
                {
                    var line = await p.StandardOutput.ReadLineAsync();
                    if (line == null) break;
                    int at = line.IndexOf("http://127.0.0.1:", StringComparison.Ordinal);
                    if (at >= 0) url = line.Substring(at).Trim();
                }
            });
            await Task.WhenAny(read, Task.Delay(20000));
            Assert.True(url != null, "The server didn't print its address. " + (p.HasExited ? await p.StandardError.ReadToEndAsync() : ""));
            using var http = new HttpClient();
            Assert.Contains("OpenPlane", await http.GetStringAsync(url));
            Assert.Contains("\"version\"", await http.GetStringAsync(url + "api/status"));
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            TestPaths.TryDelete(dir);
        }
    }

    [Fact]
    public void HelpAndBadOptions()
    {
        var exe = Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "src", "OpenPlane.App", "bin"), OperatingSystem.IsWindows() ? "OpenPlane.exe" : "OpenPlane", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (exe == null) return;
        using var p = Process.Start(new ProcessStartInfo(exe, "--frobnicate") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        Assert.Equal(2, p.ExitCode);
        Assert.Contains("Unknown option", err);
    }
}
