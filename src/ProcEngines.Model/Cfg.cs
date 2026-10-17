using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ProcEngines.Model
{
    /// <summary>
    /// Minimal reader for KSP's ConfigNode text format, so the model can load its data files without
    /// Unity or KSP. In game the KSP layer converts ConfigNodes into CfgNodes instead of parsing text.
    /// </summary>
    public sealed class CfgNode
    {
        public string Name;
        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
        public readonly List<CfgNode> Nodes = new List<CfgNode>();

        public CfgNode(string name) { Name = name; }

        public string Get(string key) => Values.Where(v => v.Key == key).Select(v => v.Value).FirstOrDefault();
        public IEnumerable<string> GetAll(string key) => Values.Where(v => v.Key == key).Select(v => v.Value);
        public bool Has(string key) => Values.Any(v => v.Key == key);
        public CfgNode Node(string name) => Nodes.FirstOrDefault(n => n.Name == name);
        public IEnumerable<CfgNode> NodesNamed(string name) => Nodes.Where(n => n.Name == name);

        public double Double(string key, double fallback = double.NaN)
        {
            string s = Get(key);
            return s == null ? fallback : ParseDouble(s);
        }

        public double[] Doubles(string key)
        {
            string s = Get(key);
            if (s == null) return null;
            return s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(ParseDouble).ToArray();
        }

        public static double ParseDouble(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

        public void Add(string key, string value) => Values.Add(new KeyValuePair<string, string>(key, value));

        /// <summary>Parses a cfg text. Returns a root node whose children are the top-level nodes.</summary>
        public static CfgNode Parse(string text)
        {
            var root = new CfgNode("root");
            var stack = new Stack<CfgNode>();
            stack.Push(root);
            string pendingName = null;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine;
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) line = line.Substring(0, c);
                line = line.Trim();
                while (line.Length > 0)
                {
                    if (line[0] == '{')
                    {
                        var n = new CfgNode(pendingName ?? "");
                        stack.Peek().Nodes.Add(n);
                        stack.Push(n);
                        pendingName = null;
                        line = line.Substring(1).Trim();
                        continue;
                    }
                    if (line[0] == '}')
                    {
                        if (stack.Count > 1) stack.Pop();
                        line = line.Substring(1).Trim();
                        continue;
                    }
                    int brace = line.IndexOfAny(new[] { '{', '}' });
                    string head = brace >= 0 ? line.Substring(0, brace).Trim() : line;
                    line = brace >= 0 ? line.Substring(brace).Trim() : "";
                    int eq = head.IndexOf('=');
                    if (eq >= 0) stack.Peek().Add(head.Substring(0, eq).Trim(), head.Substring(eq + 1).Trim());
                    else if (head.Length > 0) pendingName = head;
                }
            }
            return root;
        }
    }
}
