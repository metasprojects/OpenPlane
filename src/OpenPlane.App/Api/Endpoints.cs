using System.Collections.Concurrent;
using System.IO.Compression;
using OpenPlane.App.Services;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Flyout;
using OpenPlane.Core.Physics;
using OpenPlane.Core.Preview;

namespace OpenPlane.App.Api;

/// <summary>The JSON API the page uses. Every error comes back as { "error": "plain message" }.</summary>
public static class Endpoints
{
    private static readonly ConcurrentDictionary<string, byte[]> TemplateThumbs = new();
    /// <summary>Files this run of the app exported, so "Show in folder" also works for a folder the player chose.</summary>
    private static readonly ConcurrentDictionary<string, bool> Exported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Links the app may open in the system browser (About page).</summary>
    private static readonly string[] AllowedLinks =
    {
        "https://github.com/nikkorap/NOBlueprinter-Releases", "https://github.com/BepInEx/BepInEx", "https://threejs.org/",
        "https://github.com/tryphotino/photino.NET", "https://store.steampowered.com/app/2168680/", "https://github.com/Combat787/NOMM",
        "https://github.com/metasprojects/OpenPlane",
    };

    public static void Map(WebApplication app, OpenPlaneServices s)
    {
        // ------------------------------------------------------------ status and settings
        app.MapGet("/api/status", () => ApiJson.Ok(Status(s)));

        app.MapGet("/api/settings", () => ApiJson.Ok(SettingsView(s)));

        app.MapPut("/api/settings", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<AppSettings>(req);
            string? Clean(string? p) => string.IsNullOrWhiteSpace(p) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')));
            var game = Clean(body.GameDir);
            if (game != null && !Directory.Exists(game)) throw new UserFacingException($"The folder \"{game}\" doesn't exist. Check the game folder path.");
            if (game != null && !SteamLocator.IsNuclearOptionFolder(game))
                throw new UserFacingException($"\"{game}\" doesn't look like Nuclear Option's folder: it should contain NuclearOption.exe and NuclearOption_Data.");
            var planes = Clean(body.PlanesDir);
            if (planes != null)
            {
                try { Directory.CreateDirectory(planes); }
                catch (Exception e) { throw new UserFacingException($"OpenPlane can't use \"{planes}\" as the planes folder: {e.Message}"); }
            }
            var theme = body.Theme is "light" or "system" ? body.Theme : "dark";
            var testFlyStart = body.TestFlyStart == "hangar" ? "hangar" : "air";
            s.Settings.Update(cur =>
            {
                cur.GameDir = game; cur.PlanesDir = planes; cur.Theme = theme; cur.TestFlyStart = testFlyStart; cur.CheckForUpdates = body.CheckForUpdates; cur.GettingStartedHidden = body.GettingStartedHidden;
                cur.YourName = string.IsNullOrWhiteSpace(body.YourName) ? null : body.YourName.Trim();
                cur.FlyoutDirs = (body.FlyoutDirs ?? new List<string>()).Select(Clean).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()!;
                cur.HiddenFlyoutDirs = body.HiddenFlyoutDirs ?? new List<string>();
            });
            s.Paths.Refresh();
            // A planes folder the player chose is written into the mod's config, so the game loads it too.
            var note = s.Installer.SyncPlanesFolder();
            return ApiJson.Ok(SettingsView(s, note));
        });

        app.MapPost("/api/settings/detect", () =>
        {
            s.Paths.Refresh();
            var found = s.Steam.FindApp(SteamLocator.NuclearOptionAppId);
            return ApiJson.Ok(new
            {
                game_dir = found?.InstallPath,
                planes_dir = s.Paths.DefaultPlanesDir(),
                flyout_dirs = s.Paths.DefaultFlyoutDirs(),
                steam_roots = s.Steam.SteamRoots(),
                message = found != null ? "Found Nuclear Option through Steam." : "Steam doesn't list Nuclear Option on this computer. Choose its folder yourself.",
            });
        });

        // ------------------------------------------------------------ Flyout craft
        app.MapGet("/api/flyout", () =>
        {
            var crafts = s.Flyout.Scan();
            var converted = s.Planes.List().Where(p => p.Origin?.Source == "flyout" && p.Origin.CraftName != null)
                .GroupBy(p => p.Origin!.CraftName!).ToDictionary(g => g.Key, g => g.First());
            return ApiJson.Ok(new
            {
                folders = s.Paths.FlyoutFolders(),
                craft = crafts.Select(c => new
                {
                    id = c.Id, name = c.Name, folder = c.Folder, version = c.Version, modified = c.Modified, source = c.Source,
                    helicopter = c.Helicopter, uses_mod_parts = c.UsesModParts,
                    converted_plane_id = converted.TryGetValue(c.Name, out var p) ? p.Id : null,
                }),
            });
        });

        app.MapGet("/api/flyout/{id}/thumb", async (string id) =>
        {
            var craft = s.Flyout.Find(id) ?? throw new UserFacingException("That Flyout craft wasn't found.", 404);
            try { return Results.File(await s.Flyout.Thumbnail(craft), "image/png"); }
            catch (Exception) { return Results.NotFound(); }
        });

        app.MapPost("/api/flyout/folders", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<PathRequest>(req);
            var path = body.Path?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path)) throw new UserFacingException("Type or choose a folder first.");
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!Directory.Exists(path)) throw new UserFacingException($"The folder \"{path}\" doesn't exist.");
            s.Settings.Update(cur =>
            {
                if (!cur.FlyoutDirs.Contains(path, StringComparer.OrdinalIgnoreCase)) cur.FlyoutDirs.Add(path);
                cur.HiddenFlyoutDirs.RemoveAll(h => h.Equals(path, StringComparison.OrdinalIgnoreCase));
            });
            var count = s.Flyout.Scan().Count(c => c.Folder.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            return ApiJson.Ok(new { folders = s.Paths.FlyoutFolders(), found = count });
        });

        app.MapPost("/api/flyout/folders/remove", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<PathRequest>(req);
            var path = body.Path ?? "";
            s.Settings.Update(cur =>
            {
                int removed = cur.FlyoutDirs.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (removed == 0 && !cur.HiddenFlyoutDirs.Contains(path, StringComparer.OrdinalIgnoreCase)) cur.HiddenFlyoutDirs.Add(path);
            });
            return ApiJson.Ok(new { folders = s.Paths.FlyoutFolders() });
        });

        app.MapPost("/api/flyout/convert", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<ConvertRequest>(req);
            if (body.Ids.Count == 0) throw new UserFacingException("Choose at least one craft to convert.");
            var builder = string.IsNullOrWhiteSpace(body.Builder) ? null : body.Builder.Trim();
            var results = await Task.Run(() => ConvertAll(s, body.Ids, builder, body.Replace));
            return ApiJson.Ok(new { results });
        });

        // ------------------------------------------------------------ planes
        app.MapGet("/api/planes", () => ApiJson.Ok(new { folder = s.Paths.Planes(), planes = s.Planes.List() }));

        // What the OpenPlane mod did with each plane the last time Nuclear Option started (BepInEx/OpenPlane-planes.json).
        app.MapGet("/api/game-report", () => ApiJson.Ok(GameReport(s)));

        app.MapGet("/api/planes/{id}", (string id) =>
        {
            var opened = s.Planes.Open(id);
            var meshSet = opened.SourceMeshes.Count > 0 ? s.Meshes.Register(opened.SourceMeshes) : "";
            s.References.AddAll(opened.ReferenceImages);
            var path = s.Planes.PathFor(id);
            return ApiJson.Ok(new
            {
                id, path, file_name = Path.GetFileName(path), modified = File.GetLastWriteTimeUtc(path), spec = opened.Spec, mesh_set = meshSet, source_meshes = opened.SourceMeshes,
                report = ApiJson.Report(opened.Contents.Report), from_builder_data = opened.FromBuilderData, note = opened.Note,
            });
        });

        app.MapGet("/api/planes/{id}/thumb", (string id) =>
        {
            var png = s.Planes.Thumbnail(id);
            return png != null ? Results.File(png, "image/png") : Results.NotFound();
        });

        app.MapGet("/api/planes/{id}/file", (string id) =>
        {
            var path = s.Planes.PathFor(id);
            return Results.File(File.ReadAllBytes(path), "application/octet-stream", Path.GetFileName(path));
        });

        app.MapPost("/api/planes", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<ApiJson.SpecRequest>(req);
            var plane = s.Planes.Save(null, body.Spec, MeshesFor(s, body.MeshSet), s.References.For(body.Spec));
            return ApiJson.Ok(new { plane });
        });

        app.MapPut("/api/planes/{id}", async (string id, HttpRequest req) =>
        {
            var body = await ApiJson.Body<ApiJson.SpecRequest>(req);
            if (!body.Force && body.BaseModified is DateTime opened)
            {
                var now = File.GetLastWriteTimeUtc(s.Planes.PathFor(id));
                if (Math.Abs((now - opened.ToUniversalTime()).TotalSeconds) > 2)
                    throw new UserFacingException("This plane's file was changed outside the builder after you opened it (another window, a copy from a friend, or a restore).", 409);
            }
            var plane = s.Planes.Save(id, body.Spec, MeshesFor(s, body.MeshSet), s.References.For(body.Spec));
            return ApiJson.Ok(new { plane });
        });

        app.MapPost("/api/planes/{id}/duplicate", (string id) => ApiJson.Ok(new { plane = s.Planes.Duplicate(id) }));

        app.MapDelete("/api/planes/{id}", (string id) =>
        {
            var (token, name) = s.Planes.Delete(id);
            return ApiJson.Ok(new { token, name });
        });

        app.MapPost("/api/planes/restore", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<TokenRequest>(req);
            return ApiJson.Ok(new { plane = s.Planes.Restore(body.Token ?? "") });
        });

        app.MapPost("/api/planes/{id}/reveal", (string id) =>
        {
            s.Shell.Reveal(s.Planes.PathFor(id));
            return ApiJson.Ok(new { ok = true });
        });

        app.MapPost("/api/planes/{id}/export", async (string id, HttpRequest req) =>
        {
            var body = await ApiJson.Body<ExportRequest>(req);
            string folder;
            if (body.To == "downloads") folder = s.Paths.DownloadsDir();
            else if (!string.IsNullOrWhiteSpace(body.Folder)) folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(body.Folder.Trim().Trim('"')));
            else throw new UserFacingException("Choose a folder to save the plane in.");
            var path = s.Planes.Export(id, folder, body.IncludeReferences);
            s.Settings.Update(cur => cur.SharedOnce = true);
            Exported[Path.GetFullPath(path)] = true;
            return ApiJson.Ok(new { path, folder });
        });

        app.MapPost("/api/reveal-path", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<PathRequest>(req);
            var path = body.Path ?? "";
            // Only folders and files the app itself uses or wrote.
            var allowed = new List<string?> { s.Paths.Planes().Path, s.Paths.Game().Path, s.DataDir, s.Paths.DownloadsDir() };
            allowed.AddRange(s.Paths.FlyoutFolders().Select(f => f.Path));
            var full = Path.GetFullPath(path);
            if (!allowed.Any(a => a != null && IsInside(full, a)) && !Exported.ContainsKey(full))
                throw new UserFacingException("OpenPlane only opens its own folders.");
            if (!File.Exists(full) && !Directory.Exists(full)) throw new UserFacingException($"\"{full}\" doesn't exist yet.");
            s.Shell.Reveal(full);
            return ApiJson.Ok(new { ok = true });
        });

        // ------------------------------------------------------------ import (drag and drop, file picker)
        app.MapPost("/api/import", async (HttpRequest req) =>
        {
            var name = Path.GetFileName(req.Query["name"].ToString());
            // One copy in memory, read no further than the import limit (the request limit for this route is the same).
            // One copy in memory, read no further than the import limit.
            using var ms = await ApiJson.ReadBody(req, ImportLimit, () => TooBig(name));
            if (ms.Length == 0) throw new UserFacingException($"\"{name}\" is empty.");
            var head = ms.GetBuffer();
            if (ms.Length < 4 || head[0] != 'P' || head[1] != 'K')
                throw new UserFacingException(name.EndsWith(OplanePackage.Extension, StringComparison.OrdinalIgnoreCase)
                    ? $"\"{name}\" is damaged or isn't really an OpenPlane plane file, so it wasn't imported. Ask whoever sent it for a fresh copy."
                    : $"OpenPlane can import .oplane plane files and .zip files of Flyout craft folders. \"{name}\" is neither.");
            ms.Position = 0;
            ZipArchive zip;
            try { zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException) { throw new UserFacingException($"\"{name}\" is damaged (it isn't a readable zip)."); }
            bool isPlane;
            using (zip)
            {
                isPlane = zip.GetEntry(OplanePackage.DesignEntry) != null;
                if (!isPlane && PlaneLibrary.IsPack(zip))
                {
                    var results = s.Planes.ImportPack(zip, name, ImportLimit);
                    return ApiJson.Ok(new { kind = "pack", results = results.Select(PackResult) });
                }
                if (!isPlane)
                {
                    var craft = s.Flyout.ImportZip(zip, name);
                    return ApiJson.Ok(new { kind = "craft", craft = craft.Select(c => new { id = c.Id, name = c.Name, helicopter = c.Helicopter }) });
                }
            }
            var one = s.Planes.ImportPlane(ms, name, replaceOlder: false);
            return ApiJson.Ok(new { kind = "plane", plane = one.Plane, outcome = one.Outcome });
        });

        // The starter planes (the same for everyone, so they match in multiplayer).
        app.MapPost("/api/planes/starters", () => ApiJson.Ok(new { results = s.Planes.AddStarters().Select(PackResult) }));

        // Several planes as one plane pack (a .zip of .oplane files), e.g. every plane a host flies with.
        app.MapPost("/api/planes/export-pack", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<PackRequest>(req);
            string folder;
            if (body.To == "downloads") folder = s.Paths.DownloadsDir();
            else if (!string.IsNullOrWhiteSpace(body.Folder)) folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(body.Folder.Trim().Trim('"')));
            else throw new UserFacingException("Choose a folder to save the pack in.");
            var ids = body.All ? s.Planes.List().Where(p => p.Error == null).Select(p => p.Id).ToList() : body.Ids;
            var path = s.Planes.ExportPack(ids, folder, body.Name ?? "");
            s.Settings.Update(cur => cur.SharedOnce = true);
            Exported[Path.GetFullPath(path)] = true;
            return ApiJson.Ok(new { path, folder, count = ids.Count });
        });

        // Files opened with OpenPlane (double-click, "Open with", or dropped on its icon) since the page last asked.
        app.MapGet("/api/opened", () => ApiJson.Ok(new { opened = s.Opened.Take() }));
        // A second OpenPlane started with files (a double-click while OpenPlane is open) hands them to this one.
        app.MapPost("/api/open-files", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<OpenFilesRequest>(req);
            var paths = body.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).Take(64).ToList();
            // The OpenPlane that forwarded the files waits for this answer: a big plane pack goes on importing here after a
            // few seconds (the page reports it when it's done), so that one never gives up and opens a second window.
            var work = Task.Run(() => s.Opened.Import(s, paths, ImportLimit));
            await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(3)));
            return ApiJson.Ok(new { ok = true, count = paths.Count, done = work.IsCompleted });
        });

        // ------------------------------------------------------------ multiplayer: what this computer allows when it hosts
        app.MapGet("/api/host-rules", () => ApiJson.Ok(s.Installer.HostRules()));
        app.MapPut("/api/host-rules", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<ModInstaller.HostRulesView>(req);
            return ApiJson.Ok(s.Installer.SetHostRules(body));
        });

        // ------------------------------------------------------------ is a newer OpenPlane out? (once per start, if allowed)
        app.MapGet("/api/update", async () =>
            ApiJson.Ok(await s.Updates.Get(s.Settings.Get().CheckForUpdates && !s.Shell.DryRun)));

        // ------------------------------------------------------------ help: a report to paste into a bug report
        app.MapGet("/api/problem-report", () => ApiJson.Ok(new { text = ProblemReport.Build(s), issues_url = IssuesUrl }));

        // ------------------------------------------------------------ Windows: open .oplane files with OpenPlane
        app.MapGet("/api/file-association", () => ApiJson.Ok(FileAssociation.Status()));
        app.MapPost("/api/file-association", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<AssociationRequest>(req);
            if (body.On) FileAssociation.Register(); else FileAssociation.Unregister();
            return ApiJson.Ok(FileAssociation.Status());
        });

        // ------------------------------------------------------------ builder
        app.MapGet("/api/templates", () => ApiJson.Ok(Templates.All.Select(t => new { id = t.Id, name = t.Name, description = t.Description })));

        app.MapGet("/api/templates/{id}", (string id) =>
        {
            var t = Templates.Find(id) ?? throw new UserFacingException("That template doesn't exist.", 404);
            var spec = t.Create();
            spec.Id = OplanePackage.NewId(); // a new plane gets its own identity
            if (!string.IsNullOrWhiteSpace(s.Settings.Get().YourName)) spec.Author = s.Settings.Get().YourName!;
            return ApiJson.Ok(new { spec });
        });

        app.MapGet("/api/templates/{id}/thumb", (string id) =>
        {
            var t = Templates.Find(id) ?? throw new UserFacingException("That template doesn't exist.", 404);
            var png = TemplateThumbs.GetOrAdd(id, _ =>
            {
                var g = DesignGenerator.Generate(t.Create());
                return ThreeView.RenderPng(g.Design, g.Meshes, 220);
            });
            return Results.File(png, "image/png");
        });

        app.MapPost("/api/analyze", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<ApiJson.SpecRequest>(req);
            var meshes = MeshesFor(s, body.MeshSet);
            return ApiJson.Ok(Analyze(body.Spec, meshes, body.IncludeDesign));
        });

        app.MapPost("/api/autogear", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<ApiJson.SpecRequest>(req);
            var gear = GearPlacer.AutoPlace(body.Spec, MeshesFor(s, body.MeshSet));
            return ApiJson.Ok(new { gear });
        });

        app.MapPost("/api/meshsets", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<MeshSetRequest>(req);
            return ApiJson.Ok(new { mesh_set = body.Meshes.Count > 0 ? s.Meshes.Register(body.Meshes) : "" });
        });

        // Unsaved builder work: one draft per plane ("<plane id>") or per new plane ("new-...").
        app.MapGet("/api/drafts", () => ApiJson.Ok(new
        {
            drafts = s.Drafts.List().Select(d =>
            {
                string? planeName = null; var planeId = d.PlaneId;
                if (planeId != null)
                    try { planeName = s.Planes.Summary(s.Planes.PathFor(planeId)).Name; } catch { planeId = null; }
                return new { id = d.Id, name = d.Name, plane_id = planeId, plane_name = planeName, saved_at = d.SavedAt };
            }).ToList(),
        }));

        app.MapGet("/api/drafts/{id}", (string id) =>
        {
            var d = s.Drafts.Load(id);
            if (d == null) return ApiJson.Ok(new { draft = (object?)null });
            var meshSet = d.Value.meshes.Count > 0 ? s.Meshes.Register(d.Value.meshes) : "";
            string? planeName = null;
            if (d.Value.draft.PlaneId != null)
                try { planeName = s.Planes.Summary(s.Planes.PathFor(d.Value.draft.PlaneId)).Name; } catch { d.Value.draft.PlaneId = null; }
            return ApiJson.Ok(new
            {
                draft = new { id = d.Value.draft.Id, spec = d.Value.draft.Spec, plane_id = d.Value.draft.PlaneId, plane_name = planeName, saved_at = d.Value.draft.SavedAt, mesh_set = meshSet },
                source_meshes = d.Value.meshes,
            });
        });

        app.MapPut("/api/drafts/{id}", async (string id, HttpRequest req) =>
        {
            if (!DraftStore.IsValidId(id)) throw new UserFacingException("That draft name isn't valid.");
            var body = await ApiJson.Body<ApiJson.SpecRequest>(req);
            var meshes = MeshesFor(s, body.MeshSet);
            s.Drafts.Save(new Draft { Id = id, Spec = body.Spec, PlaneId = body.PlaneId, MeshSet = body.MeshSet }, meshes);
            return ApiJson.Ok(new { ok = true });
        });

        app.MapDelete("/api/drafts/{id}", (string id) => { s.Drafts.Clear(id); return ApiJson.Ok(new { ok = true }); });

        ShapeEndpoints.Map(app, s);

        // ------------------------------------------------------------ mod, game, jobs
        app.MapGet("/api/mod/status", () => ApiJson.Ok(s.Installer.Status()));

        app.MapPost("/api/mod/install", () =>
        {
            var job = s.Jobs.TryStart("mod", j => s.Installer.Install(j)) ?? throw new UserFacingException("An install is already running.");
            return ApiJson.Ok(job.Snapshot());
        });

        app.MapPost("/api/mod/remove", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<RemoveRequest>(req);
            var job = s.Jobs.TryStart("mod", j => { s.Installer.Remove(j, body.RemoveBlueprinter); return Task.CompletedTask; }) ?? throw new UserFacingException("An install is already running.");
            return ApiJson.Ok(job.Snapshot());
        });

        app.MapGet("/api/jobs/{id}", (string id) =>
        {
            var job = s.Jobs.Get(id) ?? throw new UserFacingException("That task wasn't found.", 404);
            return ApiJson.Ok(job.Snapshot());
        });

        // Play (no body), or Test fly ({"test_fly": {"plane_id": ...}}): straight into a single-player free flight, where
        // the mod hands the player that plane (see the mod's TestFly).
        app.MapPost("/api/game/launch", async (HttpRequest req) =>
        {
            LaunchRequest? body = null;
            if (req.ContentLength is > 0) body = await ApiJson.Body<LaunchRequest>(req);
            string? testFlyNote = null;
            bool testFly = false;
            if (body?.TestFly?.PlaneId is string planeId && planeId.Length > 0)
            {
                var plane = s.Planes.List().FirstOrDefault(p => p.Id == planeId && p.Error == null)
                            ?? throw new UserFacingException("That plane wasn't found. Save it first.", 404);
                testFlyNote = TestFlight.Prepare(s, plane, s.Settings.Get().TestFlyStart);
                testFly = testFlyNote == null;
            }
            var how = TestFlight.Launch(s, testFly);
            return ApiJson.Ok(new { ok = true, dry_run = s.Shell.DryRun, test_fly = testFly, note = testFlyNote, how });
        });

        app.MapPost("/api/open-link", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<PathRequest>(req);
            var url = body.Path ?? "";
            if (!AllowedLinks.Any(a => IsLinkUnder(url, a))) throw new UserFacingException("OpenPlane only opens its own help links.");
            s.Shell.OpenUrl(url);
            return ApiJson.Ok(new { ok = true });
        });

        app.MapPost("/api/dialog/folder", async (HttpRequest req) =>
        {
            var body = await ApiJson.Body<DialogRequest>(req);
            if (!s.Dialogs.Available) return ApiJson.Error("Folder dialogs are only available in the OpenPlane window. Type the folder path instead.", 409);
            var path = await s.Dialogs.PickFolder(body.Title ?? "Choose a folder", body.DefaultPath);
            return ApiJson.Ok(new { path });
        });
    }

    // ---------------------------------------------------------------- helpers
    /// <summary>The link itself or a page under it ("…/OpenPlane/issues"), not a look-alike ("…/OpenPlaneX").</summary>
    internal static bool IsLinkUnder(string url, string allowed)
    {
        if (!url.StartsWith(allowed, StringComparison.Ordinal)) return false;
        if (url.Length == allowed.Length || allowed.EndsWith("/", StringComparison.Ordinal)) return true;
        char next = url[allowed.Length];
        return next == '/' || next == '#' || next == '?';
    }

    /// <summary>Where players report problems (the project's GitHub issues).</summary>
    public const string IssuesUrl = "https://github.com/metasprojects/OpenPlane/issues";

    /// <summary>Largest file the import route accepts (.oplane files and zipped Flyout craft).</summary>
    public const long ImportLimit = 256L * 1024 * 1024;

    /// <summary>Largest request any other route accepts (JSON with a plane's meshes, .obj models, pictures).</summary>
    public const long RequestLimit = 128L * 1024 * 1024;

    private static UserFacingException TooBig(string name)
        => new($"\"{name}\" is over {ImportLimit / (1024 * 1024)} MB, too big to be a plane or a Flyout craft, so it wasn't imported.", 413);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it (a whole-name match, so "C:\Planes2" isn't inside "C:\Planes").</summary>
    public static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(root, cmp) || full.StartsWith(root + Path.DirectorySeparatorChar, cmp);
    }


    private static List<MeshData> MeshesFor(OpenPlaneServices s, string? meshSet)
        => s.Meshes.Get(meshSet) ?? throw new UserFacingException("mesh_set_unknown", 409);

    public static object Analyze(PlaneSpec spec, List<MeshData> meshes, bool includeDesign)
    {
        var plane = DesignGenerator.Generate(spec, meshes);
        var flight = FlightModel.Analyze(plane.Design, plane.Meshes);
        return new
        {
            stats = plane.Design.Stats,
            donor = plane.Design.Donor,
            flight = ApiJson.Flight(flight),
            notes = plane.Notes,
            ground_y = GearPlacer.GroundLevel(plane.Design),
            takeoff = new
            {
                min_pitch_reach = GameEstimates.MinPitchReach,
                // The plane's own takeoff run, and the least runway the game ever offers a plane built on this stock plane.
                own_run_m = System.Math.Round(GameEstimates.RunwayNeededM(flight.StallSpeedMs, flight.ThrustToWeight)),
                stock_min_m = GameEstimates.StockTakeoffDistanceM(plane.Design.Donor),
                runways = GameEstimates.HeartlandRunways.Select(r => new { name = r.Name, length_m = r.LengthM }),
            },
            design = includeDesign ? plane.Design : null,
        };
    }

    /// <summary>
    /// Converts a batch. With <paramref name="replace"/>, each craft's new plane takes the place of the plane converted
    /// from a craft of that name before (the newest one); each old plane is replaced at most once per batch, so two craft
    /// with the same name never overwrite each other. Planes made in the builder are never replaced.
    /// </summary>
    private static List<object> ConvertAll(OpenPlaneServices s, List<string> ids, string? builder, bool replace)
    {
        var previous = replace
            // Only planes still exactly as converted: one changed in the builder is the player's work and is never replaced.
            ? s.Planes.List().Where(p => p.Error == null && p.Origin?.Source == "flyout" && p.Origin.CraftName != null && !p.HasBuilderData)
                .GroupBy(p => p.Origin!.CraftName!).ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToList())
            : new Dictionary<string, List<string>>();
        var used = new HashSet<string>();
        var results = new List<object>();
        foreach (var id in ids)
        {
            string? Target(string craftName)
            {
                if (!previous.TryGetValue(craftName, out var candidates)) return null;
                var t = candidates.FirstOrDefault(c => !used.Contains(c));
                if (t != null) used.Add(t);
                return t;
            }
            results.Add(ConvertOne(s, id, builder, Target));
        }
        return results;
    }

    private static object ConvertOne(OpenPlaneServices s, string id, string? builder, Func<string, string?> replaceTarget)
    {
        var craft = s.Flyout.Find(id);
        if (craft == null) return new { craft_id = id, ok = false, error = "That craft wasn't found any more. It may have been moved or deleted." };
        if (craft.Helicopter)
            return new
            {
                craft_id = id, craft_name = craft.Name, ok = false, helicopter = true,
                error = "This is a helicopter. OpenPlane builds fixed-wing planes only for now, so it can't convert helicopters yet.",
            };
        try
        {
            var result = FlyoutConverter.Convert(FlyoutCraft.Load(craft.Folder), builder);
            var (plane, replaced) = s.Planes.SaveConverted(result.Design, result.Meshes, result.Report, replaceTarget(result.Design.Origin?.CraftName ?? craft.Name));
            var flight = FlightModel.Analyze(result.Design, result.Meshes);
            return new
            {
                craft_id = id, craft_name = craft.Name, ok = true, plane, report = ApiJson.Report(result.Report),
                flight = ApiJson.Flight(flight), stats = result.Design.Stats, donor = result.Design.Donor,
                replaced = replaced == null ? null : new { token = replaced.Token, name = replaced.Name },
            };
        }
        catch (Exception e)
        {
            return new { craft_id = id, craft_name = craft.Name, ok = false, error = $"\"{craft.Name}\" couldn't be converted: {e.Message}" };
        }
    }

    /// <summary>The mod's last report, as written in the game folder; found = false when the game hasn't run with the mod yet.</summary>
    private static object GameReport(OpenPlaneServices s)
    {
        var game = s.Paths.Game().Path;
        var file = game == null ? null : Path.Combine(game, "BepInEx", "OpenPlane-planes.json");
        if (file == null || !File.Exists(file)) return new { found = false };
        try
        {
            if (new FileInfo(file).Length > 4 * 1024 * 1024) return new { found = false, error = "The game's plane report is too big to read." };
            var doc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
            return new { found = true, written_at = File.GetLastWriteTimeUtc(file), report = doc };
        }
        catch (Exception e) { return new { found = false, error = "The game's plane report couldn't be read: " + e.Message }; }
    }

    private static object Status(OpenPlaneServices s)
    {
        var game = s.Paths.Game();
        return new
        {
            version = OpenPlaneServices.Version,
            platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
            native_dialogs = s.Dialogs.Available,
            dry_run = s.Shell.DryRun,
            paths = new
            {
                game = new { game.Path, game.Source, game.Exists, valid = SteamLocator.IsNuclearOptionFolder(game.Path) },
                planes = s.Paths.Planes(),
                flyout = s.Paths.FlyoutFolders(),
                data_dir = s.DataDir,
                downloads_dir = s.Paths.DownloadsDir(),
            },
            mod = s.Installer.Status(),
            settings = s.Settings.Get(),
            external_history = s.Shell.DryRun ? s.Shell.History.ToArray() : Array.Empty<string>(),
        };
    }

    private static object SettingsView(OpenPlaneServices s, string? modConfigNote = null) => new
    {
        settings = s.Settings.Get(),
        effective = new { game = s.Paths.Game(), planes = s.Paths.Planes(), flyout = s.Paths.FlyoutFolders() },
        overridden = new
        {
            game = s.Options.GameDir != null, planes = s.Options.PlanesDir != null, flyout = s.Options.FlyoutDirs.Count > 0,
        },
        settings_file = s.Settings.FilePath,
        mod_config_note = modConfigNote,
    };

    private sealed class PathRequest { public string? Path { get; set; } }
    private sealed class TokenRequest { public string? Token { get; set; } }
    /// <summary>Replace: put each new plane in place of the one converted from the same craft before (that one goes to "Deleted planes"). Off unless asked for.</summary>
    private sealed class ConvertRequest { public List<string> Ids { get; set; } = new(); public string? Builder { get; set; } public bool Replace { get; set; } }
    private sealed class ExportRequest { public string? To { get; set; } public string? Folder { get; set; } public bool IncludeReferences { get; set; } }
    private sealed class PackRequest { public List<string> Ids { get; set; } = new(); public bool All { get; set; } public string? To { get; set; } public string? Folder { get; set; } public string? Name { get; set; } }
    private sealed class AssociationRequest { public bool On { get; set; } }
    private sealed class OpenFilesRequest { public List<string> Paths { get; set; } = new(); }
    private sealed class LaunchRequest { public TestFlyRequest? TestFly { get; set; } }
    private sealed class TestFlyRequest { public string? PlaneId { get; set; } }

    private static object PackResult(PlaneLibrary.ImportOutcome r) => new
    {
        plane = r.Plane, outcome = r.Outcome, replaced = r.Replaced == null ? null : new { token = r.Replaced.Token, name = r.Replaced.Name },
        replaced_newer = r.ReplacedNewer, error = r.Error, file = r.File,
    };
    private sealed class RemoveRequest { public bool RemoveBlueprinter { get; set; } = true; }
    private sealed class DialogRequest { public string? Title { get; set; } public string? DefaultPath { get; set; } }
    private sealed class MeshSetRequest { public List<MeshData> Meshes { get; set; } = new(); }
}
