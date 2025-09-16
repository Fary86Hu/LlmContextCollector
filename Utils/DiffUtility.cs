using Microsoft.AspNetCore.Components;
using System.Text;
using System.Web;

namespace LlmContextCollector.Utils
{
    public static class DiffUtility
    {
        public record DiffOpcode(char Tag, int I1, int I2, int J1, int J2);

        public static (MarkupString unified, MarkupString sbsLeft, MarkupString sbsRight) GenerateDiffMarkup(string oldText, string newText)
        {
            var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
            var newLines = newText.Replace("\r\n", "\n").Split('\n');

            var opcodes = GetOpcodes(oldLines, newLines);

            return (GenerateUnifiedMarkup(opcodes, oldLines, newLines),
                    GenerateSbsMarkup(opcodes, oldLines, newLines, isLeft: true),
                    GenerateSbsMarkup(opcodes, oldLines, newLines, isLeft: false));
        }

        private static MarkupString GenerateUnifiedMarkup(List<DiffOpcode> opcodes, string[] oldLines, string[] newLines)
        {
            var sb = new StringBuilder();
            foreach (var op in opcodes)
            {
                switch (op.Tag)
                {
                    case 'e': // Equal
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine($"  {HttpUtility.HtmlEncode(oldLines[i])}");
                        break;
                    case 'd': // Delete
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine($"<span class=\"diff-del\">- {HttpUtility.HtmlEncode(oldLines[i])}</span>");
                        break;
                    case 'i': // Insert
                        for (int j = op.J1; j < op.J2; j++)
                            sb.AppendLine($"<span class=\"diff-add\">+ {HttpUtility.HtmlEncode(newLines[j])}</span>");
                        break;
                    case 'r': // Replace
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
                    case 'e': // Equal
                        for (int i = op.I1; i < op.I2; i++)
                            sb.AppendLine(HttpUtility.HtmlEncode(oldLines[i]));
                        break;
                    case 'd': // Delete
                        for (int i = op.I1; i < op.I2; i++)
                        {
                            if (isLeft)
                                sb.AppendLine($"<span class=\"sbs-del\">{HttpUtility.HtmlEncode(oldLines[i])}</span>");
                            else
                                sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                        }
                        break;
                    case 'i': // Insert
                        for (int j = op.J1; j < op.J2; j++)
                        {
                            if (isLeft)
                                sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                            else
                                sb.AppendLine($"<span class=\"sbs-add\">{HttpUtility.HtmlEncode(newLines[j])}</span>");
                        }
                        break;
                    case 'r': // Replace
                        int delCount = op.I2 - op.I1;
                        int addCount = op.J2 - op.J1;
                        int max = Math.Max(delCount, addCount);

                        for (int i = 0; i < max; i++)
                        {
                            if (isLeft) // Bal oldali panel
                            {
                                if (i < delCount)
                                    sb.AppendLine($"<span class=\"sbs-del\">{HttpUtility.HtmlEncode(oldLines[op.I1 + i])}</span>");
                                else
                                    sb.AppendLine("<span class=\"sbs-empty\">&nbsp;</span>");
                            }
                            else // Jobb oldali panel
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

        // Egyszerűsített SequenceMatcher a Python difflib mintájára
        public static List<DiffOpcode> GetOpcodes(string[] a, string[] b)
        {
            var matcher = new SequenceMatcher(a, b);
            return matcher.GetOpcodes();
        }

        private class SequenceMatcher
        {
            private readonly string[] _a;
            private readonly string[] _b;
            private readonly List<DiffOpcode> _opcodes = new();

            public SequenceMatcher(string[] a, string[] b)
            {
                _a = a;
                _b = b;
            }

            public List<DiffOpcode> GetOpcodes()
            {
                if (_opcodes.Any()) return _opcodes;

                var matchingBlocks = GetMatchingBlocks();

                int i1 = 0, j1 = 0;
                foreach (var (aPos, bPos, size) in matchingBlocks)
                {
                    int i2 = aPos;
                    int j2 = bPos;

                    char tag = ' ';
                    if (i1 < i2 && j1 < j2) tag = 'r';      // replace
                    else if (i1 < i2) tag = 'd';           // delete
                    else if (j1 < j2) tag = 'i';           // insert

                    if (tag != ' ')
                    {
                        _opcodes.Add(new DiffOpcode(tag, i1, i2, j1, j2));
                    }

                    if (size > 0)
                    {
                        _opcodes.Add(new DiffOpcode('e', i2, i2 + size, j2, j2 + size));
                    }

                    i1 = i2 + size;
                    j1 = j2 + size;
                }
                return _opcodes;
            }

            private List<(int a, int b, int size)> GetMatchingBlocks()
            {
                var n = _a.Length;
                var m = _b.Length;
                var matchingBlocks = new List<(int, int, int)>();
                var queue = new Queue<(int, int, int, int)>();
                queue.Enqueue((0, n, 0, m));
                var b2j = CreateB2J(_b);

                while (queue.Any())
                {
                    var (alo, ahi, blo, bhi) = queue.Dequeue();
                    var (i, j, k) = FindLongestMatch(alo, ahi, blo, bhi, b2j);
                    if (k > 0)
                    {
                        matchingBlocks.Add((i, j, k));
                        if (alo < i && blo < j)
                            queue.Enqueue((alo, i, blo, j));
                        if (i + k < ahi && j + k < bhi)
                            queue.Enqueue((i + k, ahi, j + k, bhi));
                    }
                }
                matchingBlocks.Sort();

                var finalBlocks = new List<(int, int, int)>();
                if (matchingBlocks.Any())
                {
                    var lastA = -1; var lastB = -1; var lastSize = -1;
                    foreach (var (a, b, size) in matchingBlocks)
                    {
                        if (lastA + lastSize == a && lastB + lastSize == b)
                        {
                            lastSize += size;
                        }
                        else
                        {
                            if (lastSize > 0) finalBlocks.Add((lastA, lastB, lastSize));
                            lastA = a; lastB = b; lastSize = size;
                        }
                    }
                    if (lastSize > 0) finalBlocks.Add((lastA, lastB, lastSize));
                }

                finalBlocks.Add((n, m, 0));
                return finalBlocks;
            }

            private Dictionary<string, List<int>> CreateB2J(string[] b)
            {
                var b2j = new Dictionary<string, List<int>>();
                for (int i = 0; i < b.Length; i++)
                {
                    if (!b2j.ContainsKey(b[i]))
                        b2j[b[i]] = new List<int>();
                    b2j[b[i]].Add(i);
                }
                return b2j;
            }

            private (int, int, int) FindLongestMatch(int alo, int ahi, int blo, int bhi, Dictionary<string, List<int>> b2j)
            {
                int best_i = alo, best_j = blo, best_size = 0;
                var j2len = new Dictionary<int, int>();

                for (int i = alo; i < ahi; i++)
                {
                    var new_j2len = new Dictionary<int, int>();
                    if (b2j.TryGetValue(_a[i], out var js))
                    {
                        foreach (int j in js)
                        {
                            if (j < blo) continue;
                            if (j >= bhi) break;

                            int k = (j2len.ContainsKey(j - 1) ? j2len[j - 1] : 0) + 1;
                            new_j2len[j] = k;
                            if (k > best_size)
                            {
                                best_i = i - k + 1;
                                best_j = j - k + 1;
                                best_size = k;
                            }
                        }
                    }
                    j2len = new_j2len;
                }
                return (best_i, best_j, best_size);
            }
        }
    }
}