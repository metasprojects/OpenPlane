using System.Collections.Generic;
using System.Linq;

namespace OpenPlane.Core.Design
{
    public enum ReportLevel { Info, Approximated, Dropped, Problem }

    public sealed class ReportItem
    {
        public ReportLevel Level { get; set; }
        public string Message { get; set; } = "";
        public override string ToString() => $"[{Level}] {Message}";
    }

    /// <summary>What changed when a plane was converted, in plain words for the player.</summary>
    public sealed class ImportReport
    {
        public List<ReportItem> Items { get; set; } = new List<ReportItem>();

        public void Info(string message) => Items.Add(new ReportItem { Level = ReportLevel.Info, Message = message });
        public void Approximated(string message) => Items.Add(new ReportItem { Level = ReportLevel.Approximated, Message = message });
        public void Dropped(string message) => Items.Add(new ReportItem { Level = ReportLevel.Dropped, Message = message });
        public void Problem(string message) => Items.Add(new ReportItem { Level = ReportLevel.Problem, Message = message });

        public bool HasProblems => Items.Any(i => i.Level == ReportLevel.Problem);

        public IEnumerable<string> Lines() => Items.Select(i => i.ToString());
    }
}
