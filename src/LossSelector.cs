using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.ComponentModel;

namespace RFTestTool
{
    /// <summary>
    /// Drop-in WPF UserControl: replaces the separate UL/DL cable loss fields
    /// with a single dropdown (select loss table) + current value display.
    /// Usage: place in your test parameters panel.
    /// </summary>
    public class LossSelectorControl : UserControl, INotifyPropertyChanged
    {
        // ── Public API ────────────────────────────────────────
        public string LossFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".rftest", "loss-tables");

        private double _ulLoss, _dlLoss;
        public double ULLoss { get => _ulLoss; private set { _ulLoss = value; OnChanged(); } }
        public double DLLoss { get => _dlLoss; private set { _dlLoss = value; OnChanged(); } }

        public LossTable? SelectedTable { get; private set; }

        /// <summary>Get interpolated loss for a specific freq (MHz)</summary>
        public (double ul, double dl) GetLossAt(double freqMHz)
            => SelectedTable?.GetLossAt(freqMHz) ?? (ULLoss, DLLoss);

        // ── Data ──────────────────────────────────────────────
        private readonly ObservableCollection<LossTable> _tables = new();
        private readonly ComboBox  _combo    = new();
        private readonly TextBlock _ulLabel  = new();
        private readonly TextBlock _dlLabel  = new();

        // ── Constructor ───────────────────────────────────────
        public LossSelectorControl()
        {
            Content = BuildUI();
            Loaded += (_, _) => LoadTables();
        }

        // ── UI ────────────────────────────────────────────────
        private FrameworkElement BuildUI()
        {
            // 实现细节未公开（算法核心）
            throw new NotImplementedException("该方法的实现不在开源范围内");
        }

        // ── Logic ─────────────────────────────────────────────
        public void LoadTables()
        {
            // 实现细节未公开（算法核心）
            throw new NotImplementedException("该方法的实现不在开源范围内");
        }

        private void Combo_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            // 实现细节未公开（算法核心）
            throw new NotImplementedException("该方法的实现不在开源范围内");
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            // 实现细节未公开（算法核心）
            throw new NotImplementedException("该方法的实现不在开源范围内");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
