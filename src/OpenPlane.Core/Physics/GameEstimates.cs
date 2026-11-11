using System;
using System.Linq;

namespace OpenPlane.Core.Physics
{
    /// <summary>
    /// Numbers both the app (to tell players what will happen) and the mod (to set the plane up in the game) use, so the two
    /// always agree.
    /// </summary>
    public static class GameEstimates
    {
        /// <summary>
        /// The least "pitch reach" a plane gets in the game: how hard its pitch controls can turn it compared with how hard it
        /// resists, as Nuclear Option's flight model sees it. Every plane in the in-game tests that ended at 1.5 or more flew and
        /// took off; the two that ended near 0.6 dived and couldn't lift their nose.
        /// </summary>
        public const double MinPitchReach = 1.5;

        /// <summary>The takeoff runways of Nuclear Option's Heartland map (longest at each airbase, metres), as the mod measured them.</summary>
        public static readonly (string Name, double LengthM)[] HeartlandRunways =
        {
            ("the assault carrier", 158), ("the fleet carrier", 211), ("highway strip 2", 834), ("highway strip 1", 889),
            ("the city airstrip", 1199), ("the desert airbase", 1306), ("the naval base", 1907), ("the city airbase", 2006),
            ("the mountain airbase", 2310), ("Boscali North", 2416),
        };

        /// <summary>Readable names for the Heartland airbases the game knows by id.</summary>
        public static string AirbaseName(string id)
        {
            switch (id)
            {
                case "airbase_desert1": return "the desert airbase";
                case "airbase_mountains": return "the mountain airbase";
                case "airbase_city": return "the city airbase";
                case "airbase_boscali_north": return "Boscali North";
                case "highwaystrip1": return "highway strip 1";
                case "highwaystrip2": return "highway strip 2";
                case "airstrip_city2": return "the city airstrip";
                case "navalbase": return "the naval base";
                case "FleetCarrier1": return "the fleet carrier";
                case "AssaultCarrier1": return "the assault carrier";
                default: return id;
            }
        }

        /// <summary>The stock planes' own takeoff distance setting (m): the game never offers a plane at a shorter runway.</summary>
        public static double StockTakeoffDistanceM(string? donor) => donor == "COIN" ? 100 : 1200;

        /// <summary>The longest takeoff runway in the game (Boscali North on Heartland).</summary>
        public static double LongestRunwayM => HeartlandRunways.Max(r => r.LengthM);

        /// <summary>The runway the game will want for a design: its own takeoff run, but never less than its stock plane's setting.</summary>
        public static double RunwayInGameM(string? donor, double stallSpeedMs, double thrustToWeight)
            => System.Math.Max(StockTakeoffDistanceM(donor), RunwayNeededM(stallSpeedMs, thrustToWeight));

        /// <summary>
        /// Runway a plane needs to take off: the ground roll to 1.15 times its stall speed, accelerating with its thrust per
        /// weight less rolling friction and drag (85 %), plus half again for rotating and climbing over the end. 0 when unknown.
        /// </summary>
        public static double RunwayNeededM(double stallSpeedMs, double thrustToWeight)
        {
            if (!(stallSpeedMs > 0) || !(thrustToWeight > 0) || double.IsInfinity(stallSpeedMs)) return 0;
            double v = 1.15 * stallSpeedMs;
            double a = 0.85 * FlightModel.Gravity * System.Math.Max(0.02, thrustToWeight - 0.03);
            return 1.5 * v * v / (2 * a);
        }

        /// <summary>Which Heartland airbases can launch a plane needing <paramref name="runwayM"/> of runway, in plain words.</summary>
        public static string WhereItCanTakeOff(double runwayM)
        {
            if (runwayM <= 0) return "";
            var ok = HeartlandRunways.Where(r => r.LengthM >= runwayM).Select(r => r.Name).ToList();
            var no = HeartlandRunways.Where(r => r.LengthM < runwayM).Select(r => r.Name).ToList();
            if (ok.Count == 0) return "No runway in the game is that long, so no airbase offers it; it can only start in the air.";
            if (no.Count == 0) return "Every airbase on the Heartland map can launch it, carriers included.";
            return $"On the Heartland map {ok.Count} of {HeartlandRunways.Length} airbases can launch it; too short: {string.Join(", ", no)}.";
        }
    }
}
