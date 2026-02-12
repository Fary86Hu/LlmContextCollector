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
                            result.Add(new DiffLineItem(DiffLineType.Context, oldLines[op.I1 + i], op.I1 + i, op.J1 + i));
                        break;
                    case 'd':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                            result.Add(new DiffLineItem(DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                        break;
                    case 'i':
                        for (int j = 0; j < (op.J2 - op.J1); j++)
                            result.Add(new DiffLineItem(DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
                        break;
                    case 'r':
                        for (int i = 0; i < (op.I2 - op.I1); i++)
                            result.Add(new DiffLineItem(DiffLineType.Delete, oldLines[op.I1 + i], op.I1 + i, null));
                        for (int j = 0; j < (op.J2 - op.J1); j++)
                            result.Add(new DiffLineItem(DiffLineType.Add, newLines[op.J1 + j], null, op.J1 + j));
                        break;
                }
            }
            return result;
        }

        public static List<DiffOpcode> GetOpcodes(string[] a, string[] b)
        {
            return MyersDiff.GetDiffOpcodes(a, b);
        }

        public static Task<List<DiffOpcode>> GetOpcodesAsync(string[] a, string[] b)
        {
            return Task.Run(() => GetOpcodes(a, b));
        }

        private static class MyersDiff
        {
            public static List<DiffOpcode> GetDiffOpcodes(string[] textA, string[] textB)
            {
                var diffs = Compute(textA, textB);
                var opcodes = new List<DiffOpcode>();
                int idxA = 0, idxB = 0;

                foreach (var diff in diffs)
                {
                    if (diff.IndexA > idxA || diff.IndexB > idxB)
                    {
                        int len = diff.IndexA - idxA;
                        if (len > 0)
                        {
                            opcodes.Add(new DiffOpcode('e', idxA, idxA + len, idxB, idxB + len));
                            idxA += len; idxB += len;
                        }
                    }

                    if (diff.Type == ChangeType.Delete) { opcodes.Add(new DiffOpcode('d', idxA, idxA + 1, idxB, idxB)); idxA++; }
                    else if (diff.Type == ChangeType.Insert) { opcodes.Add(new DiffOpcode('i', idxA, idxA, idxB, idxB + 1)); idxB++; }
                }

                if (idxA < textA.Length || idxB < textB.Length)
                    opcodes.Add(new DiffOpcode('e', idxA, textA.Length, idxB, textB.Length));

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
                        current = new DiffOpcode(current.Tag, current.I1, next.I2, current.J1, next.J2);
                    else { merged.Add(current); current = next; }
                }
                merged.Add(current);
                return merged;
            }

            private enum ChangeType { Insert, Delete }
            private record DiffChange(ChangeType Type, int IndexA, int IndexB);

            private static List<DiffChange> Compute(string[] textA, string[] textB)
            {
                int n = textA.Length, m = textB.Length, max = n + m;
                var v = new int[2 * max + 1];
                var trace = new List<int[]>();

                for (int d = 0; d <= max; d++)
                {
                    int[] vCopy = new int[2 * d + 1];
                    for (int k = -d; k <= d; k += 2)
                    {
                        int x = (k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max])) ? v[k + 1 + max] : v[k - 1 + max] + 1;
                        int y = x - k;
                        while (x < n && y < m && textA[x] == textB[y]) { x++; y++; }
                        v[k + max] = x;
                        vCopy[k + d] = x;
                        if (x >= n && y >= m) return Backtrack(trace, textA, textB, vCopy, d);
                    }
                    trace.Add(vCopy);
                }
                return new List<DiffChange>();
            }

            private static List<DiffChange> Backtrack(List<int[]> trace, string[] textA, string[] textB, int[] lastV, int d)
            {
                var changes = new List<DiffChange>();
                int x = textA.Length, y = textB.Length;
                for (int k = d; k > 0; k--)
                {
                    var v = trace[k - 1];
                    int diag = x - y, prevK;
                    bool canGoUp = (diag + 1) >= -(k - 1) && (diag + 1) <= (k - 1);
                    bool canGoLeft = (diag - 1) >= -(k - 1) && (diag - 1) <= (k - 1);

                    if (diag == -k || (diag != k && canGoUp && canGoLeft && v[diag - 1 + (k - 1)] < v[diag + 1 + (k - 1)])) prevK = diag + 1;
                    else prevK = diag - 1;

                    int prevX = v[prevK + (k - 1)], prevY = prevX - prevK;
                    while (x > prevX && y > prevY) { x--; y--; }
                    if (x == prevX) { changes.Add(new DiffChange(ChangeType.Insert, x, y - 1)); y--; }
                    else { changes.Add(new DiffChange(ChangeType.Delete, x - 1, y)); x--; }
                }
                changes.Reverse();
                return changes;
            }
        }
    }
}