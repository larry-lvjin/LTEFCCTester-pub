using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;

namespace RFTestTool
{
    // ── Loss row (one Freq/Input/Output entry) ────────────────
    public class LossRow : INotifyPropertyChanged
    {
        private double _freq, _input, _output;
        public double Freq   { get => _freq;   set { _freq   = value; OnChanged(); } }
        public double Input  { get => _input;  set { _input  = value; OnChanged(); } }
        public double Output { get => _output; set { _output = value; OnChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public LossRow Clone() => new() { Freq = Freq, Input = Input, Output = Output };
    }

    // ── Loss table (named collection of rows) ─────────────────
    public class LossTable : INotifyPropertyChanged
    {
        private string _name = "New Table";
        public string Name
        {
            get => _name;
            set { _name = value; OnChanged(); }
        }

        public ObservableCollection<LossRow> Rows { get; set; } = new();

        // Interpolate loss at given freq (MHz), linear between points
        public (double ul, double dl) GetLossAt(double freqMHz)
        {
            if (Rows.Count == 0) return (0, 0);
            var sorted = Rows.OrderBy(r => r.Freq).ToList();
            if (freqMHz <= sorted[0].Freq)  return (sorted[0].Input,  sorted[0].Output);
            if (freqMHz >= sorted[^1].Freq) return (sorted[^1].Input, sorted[^1].Output);
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (freqMHz >= sorted[i].Freq && freqMHz <= sorted[i+1].Freq)
                {
                    double t = (freqMHz - sorted[i].Freq) / (sorted[i+1].Freq - sorted[i].Freq);
                    double ul = sorted[i].Input  + t * (sorted[i+1].Input  - sorted[i].Input);
                    double dl = sorted[i].Output + t * (sorted[i+1].Output - sorted[i].Output);
                    return (Math.Round(ul, 2), Math.Round(dl, 2));
                }
            }
            return (0, 0);
        }

        public LossTable Clone(string newName)
        {
            var t = new LossTable { Name = newName };
            foreach (var r in Rows) t.Rows.Add(r.Clone());
            return t;
        }

        // ── JSON serialisation (no external deps) ─────────────
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{Esc(Name)}\",");
            sb.AppendLine("  \"rows\": [");
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                sb.Append($"    {{\"freq\":{r.Freq},\"input\":{r.Input},\"output\":{r.Output}}}");
                if (i < Rows.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        public static LossTable? FromJson(string json)
        {
            try
            {
                var t = new LossTable();
                var nameM = System.Text.RegularExpressions.Regex.Match(json, "\"name\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (nameM.Success) t.Name = Unesc(nameM.Groups[1].Value);
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(json,
                    "\\{\"freq\":([\\d.eE+-]+),\"input\":([\\d.eE+-]+),\"output\":([\\d.eE+-]+)\\}"))
                {
                    t.Rows.Add(new LossRow
                    {
                        Freq   = double.Parse(m.Groups[1].Value),
                        Input  = double.Parse(m.Groups[2].Value),
                        Output = double.Parse(m.Groups[3].Value)
                    });
                }
                return t;
            }
            catch { return null; }
        }

        public void SaveToFile(string folder)
        {
            Directory.CreateDirectory(folder);
            string safe = string.Concat(Name.Select(c =>
                char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
            string path = Path.Combine(folder, safe + ".loss.json");
            File.WriteAllText(path, ToJson(), Encoding.UTF8);
        }

        public static LossTable? LoadFromFile(string path)
        {
            try { return FromJson(File.ReadAllText(path, Encoding.UTF8)); }
            catch { return null; }
        }

        public static List<LossTable> LoadAll(string folder)
        {
            if (!Directory.Exists(folder)) return new();
            return Directory.GetFiles(folder, "*.loss.json")
                .Select(LoadFromFile)
                .Where(t => t != null)
                .Cast<LossTable>()
                .OrderBy(t => t.Name)
                .ToList();
        }

        private static string Esc(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Unesc(string s) =>
            s.Replace("\\\"", "\"").Replace("\\\\", "\\");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
