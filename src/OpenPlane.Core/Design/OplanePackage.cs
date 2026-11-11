using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace OpenPlane.Core.Design
{
    /// <summary>
    /// Reads and writes .oplane files: a zip holding aircraft.json (the design), meshes.json (fuselage meshes),
    /// and optionally report.json (what changed in conversion) and thumb.png.
    /// Reading is bounded (see <see cref="Limits"/>) and every design is checked (<see cref="DesignValidator"/>), so a
    /// damaged or hostile file is refused with a clear message instead of exhausting memory, in the app or in the game.
    /// </summary>
    public static class OplanePackage
    {
        public const string Extension = ".oplane";
        public const string DesignEntry = "aircraft.json";
        public const string MeshesEntry = "meshes.json";
        public const string ReportEntry = "report.json";
        public const string ThumbnailEntry = "thumb.png";

        /// <summary>The design format major version this code reads and writes ("openplane/1").</summary>
        public const string FormatPrefix = "openplane/";
        public const int FormatMajor = 1;

        /// <summary>Upper bounds for reading a .oplane file. Normal planes are far below them (the converted Su-57 has
        /// about 136,000 triangles in a 4.4 MB meshes.json).</summary>
        public static class Limits
        {
            public const int MaxEntries = 512;
            public const long MaxDesignBytes = 32L * 1024 * 1024;
            public const long MaxMeshesBytes = 128L * 1024 * 1024;
            public const long MaxReportBytes = 8L * 1024 * 1024;
            public const long MaxThumbnailBytes = 8L * 1024 * 1024;
            public const long MaxBuilderBytes = 64L * 1024 * 1024;
            public const long MaxReferenceImageBytes = 64L * 1024 * 1024;
            public const int MaxReferenceImages = 32;
            public const long MaxReferenceTotalBytes = 256L * 1024 * 1024;
            /// <summary>Largest whole file (and largest import) OpenPlane accepts.</summary>
            public const long MaxFileBytes = 400L * 1024 * 1024;
            public const int MaxTotalVertices = 2000000;
            public const int MaxTotalTriangles = 2000000;
        }

        public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            FloatFormatHandling = FloatFormatHandling.DefaultValue,
            Culture = System.Globalization.CultureInfo.InvariantCulture,
        };

        // meshes.json: the settings it has always been written with (compact, nulls kept).
        private static readonly JsonSerializerSettings CompactJson = new JsonSerializerSettings
        {
            ContractResolver = Json.ContractResolver, Formatting = Formatting.None, Culture = System.Globalization.CultureInfo.InvariantCulture,
        };

        public static string ToJson(object value) => JsonConvert.SerializeObject(value, Json);
        public static T FromJson<T>(string json) => JsonConvert.DeserializeObject<T>(json, Json)
                                                     ?? throw new InvalidDataException("The plane file is empty.");

        /// <summary>Reads JSON from a stream without first building one big string.</summary>
        public static T FromJson<T>(Stream stream)
        {
            using (var text = new StreamReader(stream, new UTF8Encoding(false), true, 65536, leaveOpen: true))
            using (var reader = new JsonTextReader(text) { CloseInput = false, MaxDepth = 64 })
            {
                var serializer = JsonSerializer.Create(Json);
                return serializer.Deserialize<T>(reader) ?? throw new InvalidDataException("The plane file is empty.");
            }
        }

        /// <summary>
        /// Writes the plane atomically: the file at <paramref name="path"/> is either the complete new plane or untouched.
        /// Refuses designs with numbers that aren't finite, or meshes that point at missing corners.
        /// </summary>
        public static void Write(string path, AircraftDesign design, IList<MeshData> meshes, ImportReport? report = null, byte[]? thumbnailPng = null)
        {
            DesignValidator.Check(design, meshes);
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = full + ".tmp";
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteText(zip, DesignEntry, ToJson(design));
                    WriteText(zip, MeshesEntry, JsonConvert.SerializeObject(meshes, CompactJson));
                    if (report != null) WriteText(zip, ReportEntry, ToJson(report));
                    if (thumbnailPng != null)
                    {
                        var e = zip.CreateEntry(ThumbnailEntry, CompressionLevel.NoCompression);
                        using (var s = e.Open()) s.Write(thumbnailPng, 0, thumbnailPng.Length);
                    }
                }
                ReplaceFile(temp, full);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Puts <paramref name="source"/> in place of <paramref name="target"/> in one step (File.Replace when the target exists).</summary>
        public static void ReplaceFile(string source, string target)
        {
            if (File.Exists(target)) File.Replace(source, target, null, ignoreMetadataErrors: true);
            else File.Move(source, target);
        }

        public sealed class Contents
        {
            public Contents(AircraftDesign design, List<MeshData> meshes, ImportReport? report, byte[]? thumbnail, string contentHash = "")
            { Design = design; Meshes = meshes; Report = report; Thumbnail = thumbnail; ContentHash = contentHash; }
            public AircraftDesign Design { get; }
            public List<MeshData> Meshes { get; }
            public ImportReport? Report { get; }
            public byte[]? Thumbnail { get; }
            /// <summary>
            /// SHA-256 (hex) of the unzipped aircraft.json and meshes.json (each prefixed with its length): the same plane
            /// gives the same hash on every machine, however the zip around it was written.
            /// </summary>
            public string ContentHash { get; }
        }

        /// <summary>What to read besides the design and meshes.</summary>
        public sealed class ReadOptions
        {
            public bool Thumbnail { get; set; } = true;
            public bool Report { get; set; } = true;
            public static readonly ReadOptions All = new ReadOptions();
            /// <summary>For the game: the design and meshes only.</summary>
            public static readonly ReadOptions DesignOnly = new ReadOptions { Thumbnail = false, Report = false };
        }

        public static Contents Read(string path) => Read(path, ReadOptions.All);

        public static Contents Read(string path, ReadOptions options)
        {
            using (var fs = File.OpenRead(path))
                return Read(fs, options);
        }

        public static Contents Read(Stream stream) => Read(stream, ReadOptions.All);

        public static Contents Read(Stream stream, ReadOptions options)
        {
            if (stream.CanSeek && stream.Length > Limits.MaxFileBytes)
                throw new InvalidDataException($"This plane file is too big ({stream.Length / (1024 * 1024)} MB; OpenPlane reads up to {Limits.MaxFileBytes / (1024 * 1024)} MB).");
            ZipArchive zip;
            try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException) { throw new InvalidDataException("This plane file is damaged (it isn't a readable zip)."); }
            using (zip)
            {
                CheckEntries(zip);
                var designEntry = zip.GetEntry(DesignEntry) ?? throw new InvalidDataException("This isn't an OpenPlane file: aircraft.json is missing.");
                var designBytes = ReadBytes(designEntry, Limits.MaxDesignBytes);
                // The format is checked as written in the file (AircraftDesign itself defaults to the current format).
                CheckFormat(Parse<FormatProbe>(designBytes, DesignEntry).Format);
                var design = Parse<AircraftDesign>(designBytes, DesignEntry);

                byte[] meshesBytes = new byte[0];
                List<MeshData> meshes;
                var m = zip.GetEntry(MeshesEntry);
                if (m != null)
                {
                    meshesBytes = ReadBytes(m, Limits.MaxMeshesBytes);
                    meshes = Parse<List<MeshData>>(meshesBytes, MeshesEntry);
                }
                else meshes = new List<MeshData>();

                DesignValidator.Check(design, meshes);

                ImportReport? report = null;
                var r = options.Report ? zip.GetEntry(ReportEntry) : null;
                if (r != null)
                {
                    try { report = Parse<ImportReport>(ReadBytes(r, Limits.MaxReportBytes), ReportEntry); }
                    catch (InvalidDataException) { report = null; } // the report is only for reading; a bad one isn't fatal
                }

                byte[]? thumb = null;
                var t = options.Thumbnail ? zip.GetEntry(ThumbnailEntry) : null;
                if (t != null)
                {
                    try { thumb = ReadBytes(t, Limits.MaxThumbnailBytes); }
                    catch (InvalidDataException) { thumb = null; }
                }
                return new Contents(design, meshes, report, thumb, HashContent(designBytes, meshesBytes));
            }
        }

        /// <summary>Refuses zips with too many entries, or whose entries add up to more than OpenPlane ever reads.</summary>
        public static void CheckEntries(ZipArchive zip)
        {
            if (zip.Entries.Count > Limits.MaxEntries)
                throw new InvalidDataException($"This plane file has {zip.Entries.Count} entries; an OpenPlane plane has far fewer, so it wasn't read.");
        }

        /// <summary>Accepts "openplane/1" (and "openplane/1.x" minor versions); refuses everything else.</summary>
        public static void CheckFormat(string? format)
        {
            if (string.IsNullOrEmpty(format))
                throw new InvalidDataException("This plane file doesn't say its format (aircraft.json has no \"format\"), so it wasn't read.");
            if (!format!.StartsWith(FormatPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"Unknown plane format \"{format}\".");
            var version = format.Substring(FormatPrefix.Length);
            int dot = version.IndexOf('.');
            var major = dot >= 0 ? version.Substring(0, dot) : version;
            if (major != FormatMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException($"This plane is in format \"{format}\", which this version of OpenPlane can't read. Update OpenPlane.");
        }

        /// <summary>
        /// The key the game mod gives a plane: "op." plus its id (letters, digits, '-' and '_', lower case, at most 48),
        /// or, for files without an id, "op.h" plus the first 16 hex digits of its content hash.
        /// </summary>
        public static string GameKey(AircraftDesign design, string contentHash)
        {
            var id = SanitizeId(design.Id);
            if (id != null) return "op." + id;
            return "op.h" + (contentHash.Length >= 16 ? contentHash.Substring(0, 16) : contentHash);
        }

        /// <summary>An id cleaned for use in keys: lower case [a-z0-9_-], at most 48 characters; null when nothing usable is left.</summary>
        public static string? SanitizeId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var sb = new StringBuilder();
            foreach (var c in id!.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 48) break;
            }
            var s = sb.ToString().Trim('-');
            return s.Length > 0 ? s : null;
        }

        /// <summary>A new random plane id (16 lower-case hex digits).</summary>
        public static string NewId()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(16);
            foreach (var b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>SHA-256 hex of the two entries' bytes, each prefixed with its length (8 bytes, little-endian).</summary>
        public static string HashContent(byte[] designJson, byte[] meshesJson)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var part in new[] { designJson, meshesJson })
                {
                    var len = BitConverter.GetBytes((long)part.Length);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(len);
                    sha.TransformBlock(len, 0, len.Length, null, 0);
                    sha.TransformBlock(part, 0, part.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var sb = new StringBuilder(64);
                foreach (var b in sha.Hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private sealed class FormatProbe { public string? Format { get; set; } }

        private static T Parse<T>(byte[] bytes, string entry)
        {
            try
            {
                using (var ms = new MemoryStream(bytes, writable: false)) return FromJson<T>(ms);
            }
            catch (JsonException e) { throw new InvalidDataException($"{entry} in this plane file is damaged: {e.Message}"); }
        }

        /// <summary>
        /// An entry's bytes, refusing entries whose declared size is over <paramref name="limit"/> and stopping if more data
        /// comes out than the zip declared (a zip can lie about sizes).
        /// </summary>
        public static byte[] ReadBytes(ZipArchiveEntry entry, long limit)
        {
            long declared = entry.Length;
            if (declared < 0 || declared > limit)
                throw new InvalidDataException($"{entry.FullName} in this plane file is too big ({declared / (1024 * 1024)} MB; the limit is {limit / (1024 * 1024)} MB).");
            using (var s = entry.Open())
            using (var bounded = new BoundedStream(s, declared, entry.FullName))
            using (var ms = new MemoryStream((int)System.Math.Min(declared, 16 * 1024 * 1024)))
            {
                bounded.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static void WriteText(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            }
        }
    }

    /// <summary>A read-only stream that fails once more than a set number of bytes come out of the stream it wraps.</summary>
    public sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private readonly string _what;
        private long _read;

        public BoundedStream(Stream inner, long max, string what) { _inner = inner; _max = max; _what = what; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Ask for one byte more than allowed, so data past the limit is noticed instead of silently cut off.
            long room = _max - _read + 1;
            if (room <= 0) throw new InvalidDataException($"{_what} in this plane file is bigger than the zip says; it wasn't read.");
            int n = _inner.Read(buffer, offset, (int)System.Math.Min(count, room));
            _read += n;
            if (_read > _max) throw new InvalidDataException($"{_what} in this plane file is bigger than the zip says; it wasn't read.");
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
