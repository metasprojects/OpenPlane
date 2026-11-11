using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Builder
{
    /// <summary>
    /// Opens any design in the builder: converted Flyout planes and files from other tools. Wings and fuselages keep
    /// their exact shapes (as fixed panels and meshes that can be moved and scaled); engines, gear, weapons and the
    /// cockpit become normal editable parts. Left/right twins are paired into one mirrored part.
    /// </summary>
    public static class SpecFromDesign
    {
        private const double Tolerance = 2e-3;

        /// <summary>Builds a spec; its fixed fuselages refer to <paramref name="meshes"/> by index.</summary>
        public static PlaneSpec Create(AircraftDesign d, IList<MeshData> meshes)
        {
            var spec = new PlaneSpec
            {
                Id = d.Id, Name = d.Name, Author = d.Author, Description = d.Description, Origin = d.Origin,
                Cockpit = d.Cockpit != null ? new CockpitDef { Position = DesignGenerator.Copy(d.Cockpit.Position), Eye = d.Cockpit.Eye != null ? DesignGenerator.Copy(d.Cockpit.Eye) : null, OwnView = d.Cockpit.OwnView } : null,
                Sensors = d.Sensors ?? new SensorSet(), Handling = d.Handling ?? new HandlingDef(),
                Mass = d.Mass ?? new MassDef(), Paint = d.Paint ?? new PaintDef(),
            };
            spec.Donor = d.Donor == DonorPicker.Pick(d, meshes) ? DonorPicker.Auto : d.Donor;
            // The palette keeps its order (meshes and panels point at it by index); each entry gets an id the builder can use.
            spec.Materials = (d.Materials ?? new List<MaterialDef>()).Select((m, i) => { var c = m.Clone(); c.Id = string.IsNullOrEmpty(c.Id) ? "m" + i : c.Id; return c; }).ToList();
            int ni = 0, nl = 0;
            spec.Instruments = (d.Instruments ?? new List<InstrumentDef>()).Select(x => new InstrumentDef { Id = x.Id ?? "inst" + ++ni, Name = x.Name, Kind = x.Kind, Reads = x.Reads, Position = DesignGenerator.Copy(x.Position), Rotation = DesignGenerator.Copy(x.Rotation, 4), Size = x.Size, Min = x.Min, Max = x.Max, MinDeg = x.MinDeg, MaxDeg = x.MaxDeg, SourcePartId = x.SourcePartId }).ToList();
            spec.Lights = (d.Lights ?? new List<LightDef>()).Select(x => new LightDef { Id = x.Id ?? "light" + ++nl, Name = x.Name, Kind = x.Kind, Position = DesignGenerator.Copy(x.Position), Rotation = DesignGenerator.Copy(x.Rotation, 4), Color = (double[])x.Color.Clone(), Size = x.Size, SourcePartId = x.SourcePartId }).ToList();

            AddFuselages(d, meshes, spec);
            AddWings(d, spec);

            int n = 0;
            foreach (var (e, twin) in Pair(d.Engines, e => e.Position, (a, b) => a.Type == b.Type && Near(a.MaxThrustN, b.MaxThrustN, 1) && Near(a.AfterburnerThrustN, b.AfterburnerThrustN, 1)))
            {
                bool prop = e.Type.EndsWith("prop", System.StringComparison.Ordinal);
                spec.Engines.Add(new EngineSpec
                {
                    Id = "eng" + ++n, Name = string.IsNullOrWhiteSpace(e.Title) ? (prop ? "Propeller engine" : "Jet engine") : e.Title!,
                    Type = prop ? EngineSpec.Prop : EngineSpec.Jet, SourceType = e.Type, Title = e.Title,
                    Position = DesignGenerator.Copy(e.Position), Mirror = twin,
                    ThrustKn = e.MaxThrustN / 1000, AfterburnerKn = e.AfterburnerThrustN / 1000, DiameterM = e.DiameterM,
                    PowerKw = e.PowerKw ?? 0, PropDiameterM = e.PropDiameterM ?? 0, Estimated = e.Estimated, SourcePartId = e.SourcePartId,
                });
            }

            n = 0;
            double frontZ = d.Gear.Count > 0 ? d.Gear.Max(g => g.Position[2]) : 0, backZ = d.Gear.Count > 0 ? d.Gear.Min(g => g.Position[2]) : 0;
            foreach (var (g, twin) in Pair(d.Gear, g => g.Position, (a, b) => Near(a.StrutLength, b.StrutLength, 1e-3) && Near(a.WheelRadius, b.WheelRadius, 1e-3)))
            {
                bool centred = System.Math.Abs(g.Position[0]) <= DesignGenerator.CentreTolerance;
                string kind = !centred ? "main" : g.Position[2] >= frontZ - 1e-6 && d.Gear.Count > 1 ? "nose" : g.Position[2] <= backZ + 1e-6 && d.Gear.Count > 1 ? "tail" : "other";
                spec.Gear.Add(new GearSpec
                {
                    Id = "gear" + ++n, Name = kind == "nose" ? "Nose gear" : kind == "main" ? "Main gear" : kind == "tail" ? "Tail wheel" : "Landing gear",
                    Kind = kind, Position = DesignGenerator.Copy(g.Position), Mirror = twin, StrutLength = g.StrutLength,
                    WheelRadius = g.WheelRadius, Travel = g.Travel, Steerable = g.Steerable, Brakes = g.Brakes, Retractable = g.Retractable,
                    SourcePartId = g.SourcePartId,
                });
            }

            n = 0;
            foreach (var (h, twin) in Pair(d.Hardpoints, h => h.Position, (a, b) => a.OriginalPayload == b.OriginalPayload && a.Allows.SequenceEqual(b.Allows) && a.Internal == b.Internal
                                                                              && SameRotation(DesignGenerator.MirrorRotation(a.Rotation), b.Rotation)))
                spec.Hardpoints.Add(new HardpointSpec
                {
                    Id = "hp" + ++n, Name = "Hardpoint " + n, Position = DesignGenerator.Copy(h.Position), Rotation = DesignGenerator.Copy(h.Rotation, 4),
                    Mirror = twin, Allows = h.Allows.ToList(), Internal = h.Internal, OriginalPayload = h.OriginalPayload, SourcePartId = h.SourcePartId,
                });

            n = 0;
            foreach (var (g, twin) in Pair(d.Guns, g => g.Position, (a, b) => Near(a.CaliberMm, b.CaliberMm, 1e-3)))
                spec.Guns.Add(new GunSpec
                {
                    Id = "gun" + ++n, Name = $"Gun ({g.CaliberMm:0.#} mm)", Position = DesignGenerator.Copy(g.Position), Direction = DesignGenerator.Copy(g.Direction),
                    Mirror = twin, CaliberMm = g.CaliberMm, RoundsPerMinute = g.RoundsPerMinute, MuzzleVelocity = g.MuzzleVelocity, Barrels = g.Barrels,
                    SourcePartId = g.SourcePartId,
                });
            return spec;
        }

        private static void AddFuselages(AircraftDesign d, IList<MeshData> meshes, PlaneSpec spec)
        {
            var used = new HashSet<int>();
            int n = 0;
            for (int i = 0; i < d.Fuselages.Count; i++)
            {
                if (used.Contains(i)) continue;
                var body = d.Fuselages[i];
                if (body.MeshIndex < 0 || body.MeshIndex >= meshes.Count) continue;
                used.Add(i);
                var mesh = meshes[body.MeshIndex];
                // A mirrored twin: same counts and every vertex reflected. A body that is already its own mirror image keeps any
                // overlapping copy as a separate part, so the plane comes back exactly as it was.
                int twin = -1;
                bool selfMirrored = FuselageLoft.IsSelfMirrored(mesh);
                for (int j = i + 1; j < d.Fuselages.Count && twin < 0 && !selfMirrored; j++)
                {
                    if (used.Contains(j)) continue;
                    var other = d.Fuselages[j];
                    if (other.MeshIndex < 0 || other.MeshIndex >= meshes.Count) continue;
                    if (IsMirrorMesh(mesh, meshes[other.MeshIndex])) twin = j;
                }
                int keep = i;
                if (twin >= 0)
                {
                    used.Add(twin);
                    // Keep the right-hand one so the mirrored copy lands on the left.
                    if (FuselageLoft.BoundsCenter(mesh).X < 0) keep = twin;
                }
                var kept = d.Fuselages[keep];
                spec.Fuselages.Add(new FuselageSpec
                {
                    Id = "fus" + ++n, Name = string.IsNullOrWhiteSpace(kept.Name) ? "Fuselage " + n : kept.Name, Kind = FuselageSpec.MeshKind,
                    MeshIndex = kept.MeshIndex, Mirror = twin >= 0, Scale = 1, SourceFrontalArea = kept.FrontalArea,
                    SkinThickness = kept.SkinThickness, SourcePartId = kept.SourcePartId,
                    Interior = meshes[kept.MeshIndex].Role == MeshData.Interior,
                });
            }
        }

        public static bool IsMirrorMesh(MeshData a, MeshData b)
        {
            if (a.Vertices.Length != b.Vertices.Length || a.Triangles.Length != b.Triangles.Length || a.Vertices.Length == 0) return false;
            bool centred = true;
            for (int i = 0; i + 2 < a.Vertices.Length; i += 3)
            {
                if (System.Math.Abs(a.Vertices[i] + b.Vertices[i]) > Tolerance || System.Math.Abs(a.Vertices[i + 1] - b.Vertices[i + 1]) > Tolerance
                    || System.Math.Abs(a.Vertices[i + 2] - b.Vertices[i + 2]) > Tolerance) return false;
                if (System.Math.Abs(a.Vertices[i]) > Tolerance) centred = false;
            }
            return !centred;
        }

        private static void AddWings(AircraftDesign d, PlaneSpec spec)
        {
            var used = new HashSet<int>();
            int wings = 0, tails = 0, fins = 0;
            for (int i = 0; i < d.Wings.Count; i++)
            {
                if (used.Contains(i)) continue;
                used.Add(i);
                var p = d.Wings[i];
                int twin = -1;
                for (int j = i + 1; j < d.Wings.Count && twin < 0; j++)
                    if (!used.Contains(j) && IsMirrorPanel(p, d.Wings[j])) twin = j;
                var keep = p;
                if (twin >= 0)
                {
                    used.Add(twin);
                    if ((p.RootLeading[0] + p.TipLeading[0]) / 2 < 0) keep = d.Wings[twin];
                }
                bool fin = !DesignStats.IsHorizontal(keep);
                bool tail = !fin && (keep.Inputs ?? new List<InputLink>()).Concat(keep.Surfaces.SelectMany(s => s.Inputs)).Any(x => x.Axis == "pitch")
                            && !(keep.Inputs ?? new List<InputLink>()).Concat(keep.Surfaces.SelectMany(s => s.Inputs)).Any(x => x.Axis == "roll");
                string name = fin ? "Fin " + ++fins : tail ? "Tail panel " + ++tails : "Wing panel " + ++wings;
                spec.Wings.Add(new WingSpec
                {
                    Id = "wing" + (spec.Wings.Count + 1), Name = name, Kind = WingSpec.PanelsKind, Mirror = twin >= 0,
                    Panels = new List<WingPanel> { keep }, Offset = new double[3], Scale = 1,
                });
            }
        }

        public static bool IsMirrorPanel(WingPanel a, WingPanel b)
        {
            bool M(double[] x, double[] y) => System.Math.Abs(x[0] + y[0]) < Tolerance && System.Math.Abs(x[1] - y[1]) < Tolerance && System.Math.Abs(x[2] - y[2]) < Tolerance;
            if (!(M(a.RootLeading, b.RootLeading) && M(a.RootTrailing, b.RootTrailing) && M(a.TipLeading, b.TipLeading) && M(a.TipTrailing, b.TipTrailing))) return false;
            if (System.Math.Abs(a.RootLeading[0]) < Tolerance && System.Math.Abs(a.TipLeading[0]) < Tolerance && System.Math.Abs(a.TipTrailing[0]) < Tolerance) return false;
            if (a.Surfaces.Count != b.Surfaces.Count) return false;
            var ai = a.Inputs ?? new List<InputLink>(); var bi = b.Inputs ?? new List<InputLink>();
            if (ai.Count != bi.Count) return false;
            for (int q = 0; q < ai.Count; q++)
            {
                double expected = DesignGenerator.MirrorsGain(ai[q].Axis) ? -ai[q].Gain : ai[q].Gain;
                if (ai[q].Axis != bi[q].Axis || !Near(expected, bi[q].Gain, 1e-6)) return false;
            }
            for (int k = 0; k < a.Surfaces.Count; k++)
            {
                var sa = a.Surfaces[k]; var sb = b.Surfaces[k];
                if (sa.Role != sb.Role || sa.Inputs.Count != sb.Inputs.Count) return false;
                if (!(M(sa.RootLeading, sb.RootLeading) && M(sa.TipTrailing, sb.TipTrailing))) return false;
                for (int q = 0; q < sa.Inputs.Count; q++)
                {
                    var ia = sa.Inputs[q]; var ib = sb.Inputs[q];
                    double expected = DesignGenerator.MirrorsGain(ia.Axis) ? -ia.Gain : ia.Gain;
                    if (ia.Axis != ib.Axis || !Near(expected, ib.Gain, 1e-6)) return false;
                }
            }
            return true;
        }

        /// <summary>Groups items into (item, hasTwin) where the twin is the same item reflected across x = 0.
        /// The kept item is the right-hand one (x &gt;= 0).</summary>
        private static IEnumerable<(T item, bool twin)> Pair<T>(IList<T> items, System.Func<T, double[]> pos, System.Func<T, T, bool> same)
        {
            var used = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var a = items[i]; var pa = pos(a);
                int twin = -1;
                if (System.Math.Abs(pa[0]) > DesignGenerator.CentreTolerance)
                    for (int j = i + 1; j < items.Count && twin < 0; j++)
                    {
                        if (used[j]) continue;
                        var pb = pos(items[j]);
                        if (System.Math.Abs(pa[0] + pb[0]) < Tolerance && System.Math.Abs(pa[1] - pb[1]) < Tolerance && System.Math.Abs(pa[2] - pb[2]) < Tolerance && same(a, items[j]))
                            twin = j;
                    }
                if (twin >= 0)
                {
                    used[twin] = true;
                    yield return (pa[0] >= 0 ? a : items[twin], true);
                }
                else yield return (a, false);
            }
        }

        private static bool Near(double a, double b, double tol) => System.Math.Abs(a - b) <= tol;

        /// <summary>Quaternions q and -q are the same rotation.</summary>
        private static bool SameRotation(double[] a, double[] b)
        {
            if (a.Length < 4 || b.Length < 4) return a.Length == b.Length;
            double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            return System.Math.Abs(System.Math.Abs(dot) - 1) < 1e-3;
        }
    }
}
