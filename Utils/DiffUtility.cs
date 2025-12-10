using Microsoft.AspNetCore.Components;
using System.Text;
using System.Web;

namespace LlmContextCollector.Utils
{
    public static class DiffUtility
    {
        public record DiffOpcode(char Tag, int I1, int I2, int J1, int J2);

        public enum DiffLineType { Context, Add, Delete, Empty }
        public record DiffLineItem(DiffLineType Type, string Content, int? OriginalIndex, int? NewIndex);

        public static (MarkupString unified, MarkupString sbsLeft, MarkupString sbsRight) GenerateDiffMarkup(string oldText, string newText)
        {
            var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
            var newLines = newText.Replace("\r\n", "\n").Split('\n');

            var opcodes = GetOpcodes(oldLines, newLines);

            return (GenerateUnifiedMarkup(opcodes, oldLines, newLines),
                    GenerateSbsMarkup(opcodes, oldLines, newLines, isLeft: true),
                    GenerateSbsMarkup(opcodes, oldLines, newLines, isLeft: false));
        }

        public static List<DiffLineItem> GenerateDiffList(string oldText, string newText)
        {
            var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
            var newLines = newText.Replace("\r\n", "\n").Split('\n');
            var opcodes = GetOpcodes(oldLines, newLines);
            var result = new List<DiffLineItem>();

            foreach (var op in opcodes)
            {
                switch (op.Tag)
                {
                    case 'e':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                        {
                            result.Add(new DiffLineItem(DiffLineType.Context, oldLines[op.I1 + i], op.I1 + i, op.J1 + i));
                        }
                        break;
                    case 'd':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                        {
                            result.Add(new DiffLineItem(DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                        }
                        break;
                    case 'i':
                        for (int j = 0; j < (op.J2 - op.J1); j++)
                        {
                            result.Add(new DiffLineItem(DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
                        }
                        break;
                    case 'r':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                        {
                            result.Add(new DiffLineItem(DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                        }
                        for (int j = 0; j < (op.J2 - op.J1); j++)
                        {
                            result.Add(new DiffLineItem(DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
                        }
                        break;
                }
            }
            return result;
        }

        private static MarkupString GenerateUnifiedMarkup(List<DiffOpcode> opcodes, string[] oldLines, string[] newLines)
        {
            var sb = new StringBuilder();
            foreach (var op in opcodes)
            {
                switch (op.Tag)
                {
                    case 'e':
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine($"  {HttpUtility.HtmlEncode(oldLines[i])}");
                        break;
                    case 'd':
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine($"<span class=\"diff-del\">- {HttpUtility.HtmlEncode(oldLines[i])}</span>");
                        break;
                    case 'i':
                        for (int j = op.J1; j < op.J2; j++)
                            sb.AppendLine($"<span class=\"diff-add\">+ {HttpUtility.HtmlEncode(newLines[j])}</span>");
                        break;
                    case 'r':
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine($"<span class=\"diff-del\">- {HttpUtility.HtmlEncode(oldLines[i])}</span>");
                        for (int j = op.J1; j < op.J2; j++)
                            sb.AppendLine($"<span class=\"diff-add\">+ {HttpUtility.HtmlEncode(newLines[j])}</span>");
                        break;
                }
            }
            return new MarkupString(sb.ToString());
        }

        private static MarkupString GenerateSbsMarkup(List<DiffOpcode> opcodes, string[] oldLines, string[] newLines, bool isLeft)
        {
            var sb = new StringBuilder();
            foreach (var op in opcodes)
            {
                switch (op.Tag)
                {
                    case 'e':
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine(HttpUtility.HtmlEncode(oldLines[i]));
                        break;
                    case 'd':
                        for (int i = op.I1; i < op.I2; i++)
                        {
                            if (isLeft)
                                sb.AppendLine($"<span class=\"sbs-del\">{HttpUtility.HtmlEncode(oldLines[i])}</span>");
                            else
                                sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                        }
                        break;
                    case 'i':
                        for (int j = op.J1; j < op.J2; j++)
                        {
                            if (isLeft)
                                sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                            else
                                sb.AppendLine($"<span class=\"sbs-add\">{HttpUtility.HtmlEncode(newLines[j])}</span>");
                        }
                        break;
                    case 'r':
                        int delCount = op.I2 - op.I1;
                        int addCount = op.J2 - op.J1;
                        int max = Math.Max(delCount, addCount);

                        for (int i = 0; i < max; i++)
                        {
                            if (isLeft)
                            {
                                if (i < delCount)
                                    sb.AppendLine($"<span class=\"sbs-del\">{HttpUtility.HtmlEncode(oldLines[op.I1 + i])}</span>");
                                else
                                    sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                            }
                            else
                            {
                                if (i < addCount)
                                    sb.AppendLine($"<span class=\"sbs-add\">{HttpUtility.HtmlEncode(newLines[op.J1 + i])}</span>");
                                else
                                    sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                            }
                        }
                        break;
                }
            }
            return new MarkupString(sb.ToString());
        }

        public static List<DiffOpcode> GetOpcodes(string[] a, string[] b)
        {
            return MyersDiff.GetDiffOpcodes(a, b);
        }

        private static class MyersDiff
        {
            public static List<DiffOpcode> GetDiffOpcodes(string[] textA, string[] textB)
            {
                var diffs = Compute(textA, textB);
                var opcodes = new List<DiffOpcode>();

                int idxA = 0;
                int idxB = 0;

                foreach (var diff in diffs)
                {
                    if (diff.IndexA > idxA || diff.IndexB > idxB)
                    {
                        int len = diff.IndexA - idxA;
                        if (len > 0)
                        {
                            opcodes.Add(new DiffOpcode('e', idxA, idxA + len, idxB, idxB + len));
                            idxA += len;
                            idxB += len;
                        }
                    }

                    if (diff.Type == ChangeType.Delete)
                    {
                        opcodes.Add(new DiffOpcode('d', idxA, idxA + 1, idxB, idxB));
                        idxA++;
                    }
                    else if (diff.Type == ChangeType.Insert)
                    {
                        opcodes.Add(new DiffOpcode('i', idxA, idxA, idxB, idxB + 1));
                        idxB++;
                    }
                }

                if (idxA < textA.Length || idxB < textB.Length)
                {
                    opcodes.Add(new DiffOpcode('e', idxA, textA.Length, idxB, textB.Length));
                }

                return MergeAdjacentOpcodes(opcodes);
            }

            private static List<DiffOpcode> MergeAdjacentOpcodes(List<DiffOpcode> ops)
            {
                if (ops.Count == 0) return ops;
                var merged = new List<DiffOpcode>();
                var current = ops[0];

                for (int i = 1; i < ops.Count; i++)
                {
                    var next = ops[i];
                    if (current.Tag == next.Tag && current.I2 == next.I1 && current.J2 == next.J1)
                    {
                        current = new DiffOpcode(current.Tag, current.I1, next.I2, current.J1, next.J2);
                    }
                    else
                    {
                        merged.Add(current);
                        current = next;
                    }
                }
                merged.Add(current);
                return merged;
            }

            private enum ChangeType { Insert, Delete }
            private record DiffChange(ChangeType Type, int IndexA, int IndexB);

            private static List<DiffChange> Compute(string[] textA, string[] textB)
            {
                int n = textA.Length;
                int m = textB.Length;
                int max = n + m;
                var v = new int[2 * max + 1];
                var trace = new List<Dictionary<int, int>>();

                for (int d = 0; d <= max; d++)
                {
                    var vCopy = new Dictionary<int, int>();
                    for (int k = -d; k <= d; k += 2)
                    {
                        int x;
                        if (k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]))
                        {
                            x = v[k + 1 + max];
                        }
                        else
                        {
                            x = v[k - 1 + max] + 1;
                        }

                        int y = x - k;
                        while (x < n && y < m && textA[x] == textB[y])
                        {
                            x++;
                            y++;
                        }

                        v[k + max] = x;
                        vCopy[k] = x;

                        if (x >= n && y >= m)
                        {
                            return Backtrack(trace, textA, textB, vCopy, d);
                        }
                    }
                    trace.Add(vCopy);
                }
                return new List<DiffChange>();
            }

            private static List<DiffChange> Backtrack(List<Dictionary<int, int>> trace, string[] textA, string[] textB, Dictionary<int, int> lastV, int d)
            {
                var changes = new List<DiffChange>();
                int x = textA.Length;
                int y = textB.Length;

                for (int k = d; k > 0; k--)
                {
                    var v = trace[k - 1];
                    int diag = x - y;
                    int prevK;

                    if (diag == -k || (diag != k && v.ContainsKey(diag + 1) && v.ContainsKey(diag - 1) && v[diag - 1] < v[diag + 1]))
                    {
                        prevK = diag + 1;
                    }
                    else
                    {
                        prevK = diag - 1;
                    }

                    int prevX = v[prevK];
                    int prevY = prevX - prevK;

                    while (x > prevX && y > prevY)
                    {
                        x--; y--;
                    }

                    if (x == prevX)
                    {
                        changes.Add(new DiffChange(ChangeType.Insert, x, y - 1));
                        y--;
                    }
                    else
                    {
                        changes.Add(new DiffChange(ChangeType.Delete, x - 1, y));
                        x--;
                    }
                }
                changes.Reverse();
                return changes;
            }
        }
    }
}