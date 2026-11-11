using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenPlane.Core.Design;

namespace OpenPlane.Core.Builder
{
    /// <summary>JSON helpers that use the .oplane file's own settings (snake_case).</summary>
    public static class BuilderJson
    {
        public static T Clone<T>(T value) => OplanePackage.FromJson<T>(OplanePackage.ToJson(value!));

        public static string Sha1(string text)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text)).Select(b => b.ToString("x2")));
        }

        public static string Sha1(byte[] bytes)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>
    /// Saves and opens builder planes: a normal .oplane file (OplanePackage) with extra entries the game never reads:
    /// builder.json, the editable <see cref="PlaneSpec"/>, and references/&lt;image&gt; for its blueprint pictures. Files
    /// without builder.json (converted planes, other tools) open through <see cref="SpecFromDesign"/>.
    /// </summary>
    public static class BuilderPackage
    {
        public const string SpecEntry = "builder.json";
        public const string ReferencesFolder = "references/";

        private static readonly Regex ImageName = new Regex("^[0-9a-f]{40}\\.(png|jpg|webp)$", RegexOptions.CultureInvariant);

        /// <summary>True for a reference picture name the builder writes: "&lt;sha1&gt;.png", ".jpg" or ".webp".</summary>
        public static bool IsImageName(string? name) => name != null && ImageName.IsMatch(name);

        public sealed class Opened
        {
            public Opened(PlaneSpec spec, List<MeshData> sourceMeshes, OplanePackage.Contents contents, bool fromBuilderData, string? note,
                Dictionary<string, byte[]>? referenceImages = null)
            {
                Spec = spec; SourceMeshes = sourceMeshes; Contents = contents; FromBuilderData = fromBuilderData; Note = note;
                ReferenceImages = referenceImages ?? new Dictionary<string, byte[]>();
            }

            public PlaneSpec Spec { get; }
            /// <summary>Meshes that fixed fuselage parts in <see cref="Spec"/> refer to (the file's meshes.json).</summary>
            public List<MeshData> SourceMeshes { get; }
            public OplanePackage.Contents Contents { get; }
            /// <summary>True when the spec came from builder.json (false: rebuilt from the design).</summary>
            public bool FromBuilderData { get; }
            /// <summary>Why builder data was not used, when it was present but out of date.</summary>
            public string? Note { get; }
            /// <summary>The blueprint pictures stored in the file, by image name (empty when not asked for).</summary>
            public Dictionary<string, byte[]> ReferenceImages { get; }
        }

        /// <summary>
        /// Writes the plane atomically: the file is complete or untouched. Reference pictures are written from
        /// <paramref name="referenceImages"/> (by image name); references whose picture isn't given are left out, and so
        /// are pictures beyond the reading limits (<see cref="OplanePackage.Limits"/>), so the file always reopens whole.
        /// </summary>
        public static void Write(string path, PlaneSpec spec, GeneratedPlane plane, ImportReport? report = null, byte[]? thumbnailPng = null,
            IDictionary<string, byte[]>? referenceImages = null)
        {
            var baked = Bake(spec, plane);
            baked.Id = plane.Design.Id;
            baked.DesignHash = BuilderJson.Sha1(OplanePackage.ToJson(plane.Design));
            var kept = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var image in baked.References.Where(r => r != null && IsImageName(r.Image) && referenceImages != null && referenceImages.ContainsKey(r.Image)).Select(r => r.Image).Distinct())
            {
                long size = referenceImages![image].LongLength;
                if (kept.Count >= OplanePackage.Limits.MaxReferenceImages || size > OplanePackage.Limits.MaxReferenceImageBytes
                    || total + size > OplanePackage.Limits.MaxReferenceTotalBytes) continue;
                kept.Add(image);
                total += size;
            }
            baked.References = baked.References.Where(r => r != null && kept.Contains(r.Image)).ToList();
            var full = Path.GetFullPath(path);
            var temp = full + ".saving";
            try
            {
                OplanePackage.Write(temp, plane.Design, plane.Meshes, report, thumbnailPng);
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
                {
                    WriteBytes(zip, SpecEntry, new UTF8Encoding(false).GetBytes(OplanePackage.ToJson(baked)), CompressionLevel.Optimal);
                    foreach (var image in baked.References.Select(r => r.Image).Distinct())
                        WriteBytes(zip, ReferencesFolder + image, referenceImages![image], CompressionLevel.NoCompression);
                }
                OplanePackage.ReplaceFile(temp, full);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void WriteBytes(ZipArchive zip, string name, byte[] bytes, CompressionLevel level)
        {
            var e = zip.CreateEntry(name, level);
            using (var s = e.Open()) s.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// The spec as saved: fixed meshes and panels have their moves, turns and scales applied and point at the output
        /// meshes, so the file's meshes.json is all the next session needs. Shape parts are saved as they are. A fixed
        /// fuselage whose mesh wasn't made (it was missing) keeps no mesh index (-1), so it never reopens as a copy of
        /// another body; the builder keeps saying it's missing until the player removes it.
        /// </summary>
        public static PlaneSpec Bake(PlaneSpec spec, GeneratedPlane plane)
        {
            var baked = PlaneSpec.Upgrade(BuilderJson.Clone(spec));
            baked.Format = PlaneSpec.CurrentFormat;
            foreach (var f in baked.Fuselages.Where(f => f.Kind == FuselageSpec.MeshKind))
            {
                if (!plane.MeshIndexByItem.TryGetValue(f.Id, out var index)) { f.MeshIndex = -1; continue; }
                if (FuselageLoft.IsTurned(f.RotationDeg))
                {
                    var body = plane.Design.Fuselages.FirstOrDefault(b => b.MeshIndex == index);
                    f.SourceFrontalArea = body?.FrontalArea ?? f.SourceFrontalArea * f.Scale * f.Scale;
                }
                else f.SourceFrontalArea *= f.Scale * f.Scale;
                f.MeshIndex = index;
                f.Scale = 1;
                f.Position = new double[3];
                f.RotationDeg = new double[3];
            }
            foreach (var w in baked.Wings.Where(w => w.Kind == WingSpec.PanelsKind))
            {
                int count = w.Panels.Count;
                w.Panels = DesignGenerator.BuildWing(w).Take(count).ToList();
                w.Offset = new double[3];
                w.Scale = 1;
            }
            return baked;
        }

        public static Opened Read(string path, bool includeReferences = true)
        {
            using (var fs = File.OpenRead(path)) return Read(fs, includeReferences);
        }

        /// <summary>
        /// Opens a plane for the builder. Reading is bounded like <see cref="OplanePackage.Read(Stream)"/> (a seekable stream
        /// is read in place, never copied); reference pictures are limited in number, in size each and in size in all
        /// (pictures over the limits are left out).
        /// </summary>
        public static Opened Read(Stream stream, bool includeReferences = true)
        {
            var s = stream;
            if (!s.CanSeek)
            {
                var copy = new MemoryStream();
                var bounded = new BoundedStream(new NonClosing(stream), OplanePackage.Limits.MaxFileBytes, "The plane file");
                bounded.CopyTo(copy);
                copy.Position = 0;
                s = copy;
            }
            long start = s.Position;
            var contents = OplanePackage.Read(s);
            s.Position = start;
            string? specText = null, designHash = null, note = null;
            var images = new Dictionary<string, byte[]>();
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true))
            {
                var e = zip.GetEntry(SpecEntry);
                if (e != null)
                {
                    try { specText = new UTF8Encoding(false).GetString(OplanePackage.ReadBytes(e, OplanePackage.Limits.MaxBuilderBytes)); }
                    catch (InvalidDataException) { note = "This plane's builder data couldn't be read, so its parts open as fixed shapes."; }
                }
                var d = zip.GetEntry(OplanePackage.DesignEntry);
                if (d != null) designHash = BuilderJson.Sha1(OplanePackage.ReadBytes(d, OplanePackage.Limits.MaxDesignBytes));
                if (includeReferences)
                {
                    long total = 0;
                    var entries = zip.Entries.Where(x => x.FullName.StartsWith(ReferencesFolder, StringComparison.Ordinal))
                                             .OrderBy(x => x.FullName, StringComparer.Ordinal);
                    foreach (var entry in entries)
                    {
                        var name = entry.FullName.Substring(ReferencesFolder.Length);
                        if (!IsImageName(name) || images.ContainsKey(name)) continue;
                        if (images.Count >= OplanePackage.Limits.MaxReferenceImages) break;
                        if (entry.Length > OplanePackage.Limits.MaxReferenceImageBytes || total + entry.Length > OplanePackage.Limits.MaxReferenceTotalBytes) continue;
                        try
                        {
                            var bytes = OplanePackage.ReadBytes(entry, OplanePackage.Limits.MaxReferenceImageBytes);
                            total += bytes.Length;
                            images[name] = bytes;
                        }
                        catch (InvalidDataException) { /* a damaged picture is left out */ }
                    }
                }
            }

            if (specText != null)
            {
                try
                {
                    var spec = OplanePackage.FromJson<PlaneSpec>(specText);
                    if (!PlaneSpec.IsKnownFormat(spec.Format))
                        note = "This plane's builder data is in a newer format, so its parts open as fixed shapes.";
                    else if (spec.DesignHash != null && spec.DesignHash != designHash)
                        note = "This plane was changed outside OpenPlane's builder, so its wings and fuselage open as fixed shapes.";
                    else
                    {
                        PlaneSpec.Upgrade(spec);
                        // The design's id is the plane's identity (builder data from older files has none).
                        spec.Id = contents.Design.Id;
                        if (includeReferences) spec.References = spec.References.Where(r => r != null && images.ContainsKey(r.Image)).ToList();
                        return new Opened(spec, contents.Meshes, contents, true, null, images);
                    }
                }
                catch (Exception)
                {
                    note = "This plane's builder data couldn't be read, so its parts open as fixed shapes.";
                }
            }
            return new Opened(PlaneSpec.Upgrade(SpecFromDesign.Create(contents.Design, contents.Meshes)), contents.Meshes, contents, false, note);
        }

        /// <summary>
        /// A copy of a .oplane without its reference pictures (for sharing): references/* entries are dropped and builder.json
        /// loses its "references" list; every other key is kept as written, including ones from newer builders.
        /// aircraft.json is copied byte for byte, so the builder data still matches it.
        /// </summary>
        public static byte[] WithoutReferences(byte[] oplane)
        {
            using (var input = new ZipArchive(new MemoryStream(oplane, writable: false), ZipArchiveMode.Read))
            {
                OplanePackage.CheckEntries(input);
                if (!input.Entries.Any(e => e.FullName.StartsWith(ReferencesFolder, StringComparison.Ordinal)))
                    return oplane;
                var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var e in input.Entries)
                    {
                        if (e.FullName.StartsWith(ReferencesFolder, StringComparison.Ordinal)) continue;
                        var bytes = OplanePackage.ReadBytes(e, LimitFor(e.FullName));
                        if (e.FullName == SpecEntry) bytes = WithoutReferenceList(bytes);
                        WriteBytes(zip, e.FullName, bytes, e.FullName == OplanePackage.ThumbnailEntry ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    }
                }
                return output.ToArray();
            }
        }

        /// <summary>builder.json with only its "references" key removed (unreadable builder data is returned as it was).</summary>
        public static byte[] WithoutReferenceList(byte[] builderJson)
        {
            try
            {
                using (var text = new StringReader(new UTF8Encoding(false).GetString(builderJson)))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 64 })
                {
                    var obj = JObject.Load(reader);
                    obj.Remove("references");
                    return new UTF8Encoding(false).GetBytes(obj.ToString(Formatting.Indented));
                }
            }
            catch (JsonException) { return builderJson; }
        }

        private static long LimitFor(string entry)
        {
            switch (entry)
            {
                case OplanePackage.DesignEntry: return OplanePackage.Limits.MaxDesignBytes;
                case OplanePackage.MeshesEntry: return OplanePackage.Limits.MaxMeshesBytes;
                case OplanePackage.ReportEntry: return OplanePackage.Limits.MaxReportBytes;
                case OplanePackage.ThumbnailEntry: return OplanePackage.Limits.MaxThumbnailBytes;
                case SpecEntry: return OplanePackage.Limits.MaxBuilderBytes;
                default: return OplanePackage.Limits.MaxReferenceImageBytes;
            }
        }

        /// <summary>Lets the caller's stream be wrapped (and the wrapper disposed) without closing it.</summary>
        private sealed class NonClosing : Stream
        {
            private readonly Stream _s;
            public NonClosing(Stream s) { _s = s; }
            public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
            public override bool CanRead => _s.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
