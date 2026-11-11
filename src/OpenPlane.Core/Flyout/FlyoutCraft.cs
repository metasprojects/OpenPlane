using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Flyout
{
    /// <summary>One part of a Flyout craft: its place in the part tree and its raw blocks.</summary>
    public sealed class FlyoutPart
    {
        public FlyoutPart(FlyoutBlock block)
        {
            Block = block;
            ResourcePath = block.Get("resource_path") ?? "";
            Title = block.Get("title");
            Id = block.GetInt("id", -1);
            ParentId = block.GetInt("parent_id", -1);
            LocalPose = new Pose(block.GetVec3("position"),
                FlyoutValues.TryQuat(block.Get("rotation"), out var q) ? q : Quat.Identity);
            Mirrored = block.GetBool("mirrored");
            MirroredId = int.TryParse(block.Get("mirrored_id"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : (int?)null;
            var size = block.Child("*Resizable")?.GetNumbers("size");
            ShapeScale = size != null && size.Length >= 3 ? new Vec3(size[0], size[1], size[2]) : new Vec3(1, 1, 1);
        }

        /// <summary>
        /// The part's "*Resizable" size: a per-axis scale on its own shape (vertices). It does not move child parts.
        /// Seen on the He-162 replica: a 23.5 m stored body shown 0.94 m long (size z = 0.04).
        /// </summary>
        public Vec3 ShapeScale { get; }

        public FlyoutBlock Block { get; }
        public string ResourcePath { get; }
        public string? Title { get; }
        public int Id { get; }
        public int ParentId { get; }
        public Pose LocalPose { get; }

        /// <summary>
        /// True when this part's own shape is the mirror image of what its blocks describe (reflect local x).
        /// Positions of child parts are already stored on the correct side and are not reflected.
        /// Checked on 13 craft: mirrored twins land exactly opposite across x = 0.
        /// </summary>
        public bool Mirrored { get; }
        public int? MirroredId { get; }

        /// <summary>World pose (relative to the craft origin), filled in by <see cref="FlyoutCraft"/>.</summary>
        public Pose WorldPose { get; internal set; } = Pose.Identity;

        public FlyoutPart? Parent { get; internal set; }
        public List<FlyoutPart> Children { get; } = new List<FlyoutPart>();

        /// <summary>A module block such as "*ProcWing2" (pass the name without the star).</summary>
        public FlyoutBlock? Module(string name) => Block.Child("*" + name);

        public IEnumerable<FlyoutBlock> Modules(string name) => Block.ChildrenNamed("*" + name);

        /// <summary>Short type name: the resource path without the "Parts/" prefix.</summary>
        public string Kind => ResourcePath.StartsWith("Parts/", StringComparison.Ordinal) ? ResourcePath.Substring(6) : ResourcePath;

        /// <summary>A point from this part's own shape data (its local space) into craft space, applying its size and mirror flag.</summary>
        public Vec3 ShapePointToWorld(Vec3 local)
        {
            var scaled = new Vec3(local.X * ShapeScale.X, local.Y * ShapeScale.Y, local.Z * ShapeScale.Z);
            return WorldPose.TransformPoint(Mirrored ? scaled.MirrorX : scaled);
        }

        public override string ToString() => $"#{Id} {Kind}";
    }

    /// <summary>A Flyout craft folder: data.txt (parts), meta.txt and editor.txt.</summary>
    public sealed class FlyoutCraft
    {
        private FlyoutCraft(string folder, string name, string? saveVersion, FlyoutBlock data, FlyoutBlock? meta, List<FlyoutPart> parts, List<string> warnings)
        {
            Folder = folder; Name = name; SaveVersion = saveVersion; Data = data; Meta = meta; Parts = parts; Warnings = warnings;
        }

        public string Folder { get; }
        public string Name { get; }

        /// <summary>The save format version from data.txt or meta.txt, for example "0.2504".</summary>
        public string? SaveVersion { get; }

        public FlyoutBlock Data { get; }
        public FlyoutBlock? Meta { get; }
        public List<FlyoutPart> Parts { get; }
        public List<string> Warnings { get; }

        public IEnumerable<FlyoutPart> PartsOfKind(string kind) => Parts.Where(p => p.Kind == kind);

        /// <summary>Top-level blocks such as ControlLaw and FuelSystem.</summary>
        public IEnumerable<FlyoutBlock> TopBlocks(string name) => Data.ChildrenNamed(name);

        /// <summary>Loads a craft folder (the folder that holds data.txt).</summary>
        public static FlyoutCraft Load(string folder)
        {
            var dataPath = Path.Combine(folder, "data.txt");
            if (!File.Exists(dataPath))
                throw new FileNotFoundException("This folder has no data.txt, so it isn't a Flyout craft folder.", dataPath);
            return FromText(folder, File.ReadAllText(dataPath),
                File.Exists(Path.Combine(folder, "meta.txt")) ? File.ReadAllText(Path.Combine(folder, "meta.txt")) : null);
        }

        public static FlyoutCraft FromText(string folder, string dataText, string? metaText)
        {
            var parser = new FlyoutParser();
            var data = parser.Parse(dataText);
            FlyoutBlock? meta = metaText != null ? new FlyoutParser().Parse(metaText) : null;

            var warnings = new List<string>(parser.Warnings);
            string name = meta?.Get("name")
                          ?? data.Values.FirstOrDefault(v => v.Value == null).Key
                          ?? Path.GetFileName(folder.TrimEnd('/', '\\'));
            string? version = data.Get("Version") ?? meta?.Get("version");

            var parts = data.ChildrenNamed("Part").Select(b => new FlyoutPart(b)).ToList();
            var byId = new Dictionary<int, FlyoutPart>();
            foreach (var p in parts)
            {
                if (p.Id < 0) { warnings.Add("A part has no id; it was skipped."); continue; }
                if (byId.ContainsKey(p.Id)) { warnings.Add($"Part id {p.Id} appears twice; the second copy was skipped."); continue; }
                byId[p.Id] = p;
            }
            parts = byId.Values.OrderBy(p => p.Id).ToList();

            foreach (var p in parts)
            {
                if (p.ParentId >= 0 && byId.TryGetValue(p.ParentId, out var parent) && parent != p)
                {
                    p.Parent = parent;
                    parent.Children.Add(p);
                }
                else if (p.ParentId >= 0)
                {
                    warnings.Add($"Part #{p.Id} ({p.Kind}) points at missing parent #{p.ParentId}; treated as attached to the craft origin.");
                }
            }

            // World poses. Iterative with cycle protection: a part tree from a hand-edited file may loop.
            var done = new HashSet<int>();
            foreach (var p in parts) ResolveWorld(p, done, warnings);

            return new FlyoutCraft(folder, name, version, data, meta, parts, warnings);
        }

        private static void ResolveWorld(FlyoutPart part, HashSet<int> done, List<string> warnings)
        {
            if (done.Contains(part.Id)) return;
            var chain = new List<FlyoutPart>();
            // Walk up to a resolved ancestor or the root. If the walk loops, cut the loop and walk again from the start,
            // so every part composes with an ancestor whose pose is already known.
            while (true)
            {
                chain.Clear();
                var seen = new HashSet<int>();
                var cur = part;
                bool cut = false;
                while (cur != null && !done.Contains(cur.Id))
                {
                    if (!seen.Add(cur.Id))
                    {
                        warnings.Add($"Part #{cur.Id} is its own ancestor; the loop was cut there.");
                        cur.Parent?.Children.Remove(cur);
                        cur.Parent = null;
                        cut = true;
                        break;
                    }
                    chain.Add(cur);
                    cur = cur.Parent;
                }
                if (!cut) break;
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var p = chain[i];
                p.WorldPose = p.Parent != null ? p.Parent.WorldPose.Compose(p.LocalPose) : p.LocalPose;
                done.Add(p.Id);
            }
        }
    }
}
