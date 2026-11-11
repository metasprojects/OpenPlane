using System;
using System.Collections.Generic;
using System.IO;

namespace OpenPlane.Core.Flyout
{
    /// <summary>
    /// Reads Flyout's plain-text block format (craft <c>data.txt</c>, <c>editor.txt</c>, fuel files).
    /// The grammar, as observed in save versions 0.2323 to 0.2504:
    /// a line naming a block, then a line "{", then key=value lines and nested blocks, then "}".
    /// Lines made of dashes between pipes (|-----|) separate parts and are ignored.
    /// The parser never throws on odd input: unknown or unbalanced lines are kept or skipped and counted in <see cref="Warnings"/>.
    /// </summary>
    public sealed class FlyoutParser
    {
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Parses a whole file. The returned root block (name "") holds the top-level values and blocks.</summary>
        public FlyoutBlock Parse(string text)
        {
            var root = new FlyoutBlock("");
            var stack = new Stack<FlyoutBlock>();
            stack.Push(root);
            string? pending = null; // a bare word that may be the name of the next block
            int lineNo = 0;

            using (var reader = new StringReader(text))
            {
                string? raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lineNo++;
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("|-", StringComparison.Ordinal) && line.EndsWith("|", StringComparison.Ordinal)) continue;

                    if (line == "{")
                    {
                        var block = new FlyoutBlock(pending ?? "");
                        pending = null;
                        stack.Peek().Children.Add(block);
                        stack.Push(block);
                        continue;
                    }

                    if (line == "}")
                    {
                        FlushPending(stack.Peek(), ref pending);
                        if (stack.Count > 1) stack.Pop();
                        else Warnings.Add($"line {lineNo}: unbalanced '}}' ignored");
                        continue;
                    }

                    FlushPending(stack.Peek(), ref pending);

                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        var key = line.Substring(0, eq).Trim();
                        var value = line.Substring(eq + 1).Trim();
                        stack.Peek().Values.Add(new KeyValuePair<string, string?>(key, value));
                    }
                    else
                    {
                        pending = line;
                    }
                }
            }

            FlushPending(stack.Peek(), ref pending);
            if (stack.Count > 1) Warnings.Add($"{stack.Count - 1} block(s) not closed at end of file");
            return root;
        }

        public FlyoutBlock ParseFile(string path) => Parse(File.ReadAllText(path));

        private static void FlushPending(FlyoutBlock target, ref string? pending)
        {
            if (pending == null) return;
            target.Values.Add(new KeyValuePair<string, string?>(pending, null));
            pending = null;
        }
    }
}
