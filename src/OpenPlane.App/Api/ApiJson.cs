using System.Text;
using Newtonsoft.Json;
using OpenPlane.Core.Builder;
using OpenPlane.Core.Design;
using OpenPlane.Core.Physics;

namespace OpenPlane.App.Api;

/// <summary>JSON for the web API: the same snake_case style as the .oplane file, without indentation.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = OplanePackage.Json.ContractResolver,
        NullValueHandling = NullValueHandling.Include,
        Formatting = Formatting.None,
        FloatFormatHandling = FloatFormatHandling.DefaultValue,
        Culture = System.Globalization.CultureInfo.InvariantCulture,
    };

    public static string Serialize(object? value) => JsonConvert.SerializeObject(value, Settings);

    public static IResult Ok(object? value, int status = 200)
        => Results.Content(Serialize(value), "application/json", Encoding.UTF8, status);

    public static IResult Error(string message, int status = 400)
        => Results.Content(Serialize(new { error = message }), "application/json", Encoding.UTF8, status);

    /// <summary>
    /// The request body in one memory stream, read no further than <paramref name="limit"/> bytes (the request's own size
    /// limit is raised to it first, for routes that take more than the server's default).
    /// </summary>
    public static async Task<MemoryStream> ReadBody(HttpRequest request, long limit, Func<Exception> tooBig)
    {
        var feature = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        if (request.ContentLength > limit) throw tooBig();
        var ms = new MemoryStream(request.ContentLength is > 0 && request.ContentLength <= limit ? (int)request.ContentLength.Value : 0);
        var buffer = new byte[81920];
        int n;
        while ((n = await request.Body.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + n > limit) { ms.Dispose(); throw tooBig(); }
            ms.Write(buffer, 0, n);
        }
        ms.Position = 0;
        return ms;
    }

    public static async Task<T> Body<T>(HttpRequest request) where T : class
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) throw new Services.UserFacingException("The request was empty.");
        try { return JsonConvert.DeserializeObject<T>(text, Settings) ?? throw new Services.UserFacingException("The request was empty."); }
        catch (JsonException e) { throw new Services.UserFacingException("The request wasn't valid JSON: " + e.Message); }
    }

    public static object Report(ImportReport? report)
        => (report?.Items ?? new List<ReportItem>()).Select(i => new { level = i.Level.ToString().ToLowerInvariant(), message = i.Message }).ToList();

    public static object Flight(FlightReport f) => new
    {
        empty_mass_kg = f.EmptyMassKg,
        fuel_mass_kg = f.FuelMassKg,
        gross_mass_kg = f.GrossMassKg,
        center_of_mass = f.CenterOfMass,
        neutral_point_z = f.NeutralPointZ,
        mean_chord_m = f.MeanChordM,
        static_margin_percent = f.StaticMarginPercent,
        stability = f.Stability,
        wing_area_m2 = f.WingAreaM2,
        span_m = f.SpanM,
        aspect_ratio = f.AspectRatio,
        wing_loading_kg_m2 = f.WingLoadingKgM2,
        thrust_to_weight = f.ThrustToWeight,
        stall_speed_ms = f.StallSpeedMs,
        stall_speed_knots = f.StallSpeedKnots,
        // How hard the pitch controls can lift the nose against the plane's stability (null when it has no pitch control), and
        // the runway the game will want: the takeoff panel in the builder shows both.
        pitch_reach = double.IsInfinity(f.PitchReach) || double.IsNaN(f.PitchReach) ? (double?)null : System.Math.Round(f.PitchReach, 2),
        runway_needed_m = f.RunwayNeededM,
        has_problems = f.HasProblems,
        checks = f.Checks.Select(c => new { level = c.Level.ToString().ToLowerInvariant(), message = c.Message }).ToList(),
        mass_breakdown = f.MassBreakdown.Select(m => new { name = m.Name, kg = m.Kg, position = m.Position }).ToList(),
    };

    /// <summary>Request body for analyse/save: the editable plane and the id of its fixed meshes.</summary>
    public sealed class SpecRequest
    {
        public PlaneSpec Spec { get; set; } = new PlaneSpec();
        public string? MeshSet { get; set; }
        public string? PlaneId { get; set; }
        public bool IncludeDesign { get; set; }
        /// <summary>When the builder opened (or last saved) the file; saving refuses if the file changed since, unless <see cref="Force"/>.</summary>
        public DateTime? BaseModified { get; set; }
        public bool Force { get; set; }
    }
}
