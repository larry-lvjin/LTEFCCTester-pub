using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LTEFCCTester
{
    // ═══════════════════════════════════════════════════════════════════
    //  Domain Models — B26 (Band 26a, 814–824 MHz) conducted power
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>RRC 连接态判定结果。</summary>
    public enum RrcState { Connected, Idle, Unknown, Recovering }

    /// <summary>One bandwidth definition (the "big window" selector item).</summary>
    public class BwDef
    {
        public string   Name    { get; init; } = "";   // "3 MHz"  (shown in the ComboBox)
        public string   Code    { get; init; } = "";   // CMW500 BW code, e.g. "B030"
        public double[] Freqs   { get; init; } = Array.Empty<double>();

        /// <summary>RB (size, offset) test lines for a given modulation.</summary>
        public List<(int Rb, int Off)> LinesFor(string modToken) =>
            LineFactory(modToken);

        public Func<string, List<(int Rb, int Off)>> LineFactory { get; init; }
            = _ => new();
    }

    /// <summary>
    /// One test-plan row: a single (bandwidth, frequency, modulation, RB, offset)
    /// measurement.  Also the DataGrid item.
    /// </summary>
    public class TestRow : System.ComponentModel.INotifyPropertyChanged
    {
        private bool   _sel        = true;
        private string _statusText = "─";
        private string _statusColor = "#858585";
        private string _measPower  = "─";
        private string _note       = "";

        // Plan (fixed)
        public int    Seq        { get; set; }
        public string ChannelType { get; set; } = "";  // "Low" / "Mid" / "High"
        public string BwLabel    { get; set; } = "";   // "3 MHz"
        public string BwCode     { get; set; } = "";   // "B030"
        public double FreqMHz    { get; set; }
        public int    Earfcn     { get; set; }
        public string Modulation { get; set; } = "";   // "QPSK" / "16QAM"
        public string ModToken   { get; set; } = "";   // "QPSK" / "Q16"
        public int    RbSize     { get; set; }
        public int    RbOffset   { get; set; }
        public int    TbsIdx     { get; set; }

        public string FreqStr  => FreqMHz.ToString("0.0", CultureInfo.InvariantCulture);
        public string LimitStr => "≤50";

        // Selection (checkbox)
        public bool IsSelected { get => _sel; set { _sel = value; OnPC(); } }

        // Results (live-updated)
        public string MeasuredPowerStr { get => _measPower;   set { _measPower = value; OnPC(); } }
        public string StatusText       { get => _statusText;  set { _statusText = value; OnPC(); } }
        public string StatusColor      { get => _statusColor; set { _statusColor = value; OnPC(); } }
        public string Note             { get => _note;        set { _note = value; OnPC(); } }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CMW500 Controller (raw TCP, port 5025)
    // ═══════════════════════════════════════════════════════════════════

    public class CmwController : IDisposable
    {
        private TcpClient?     _tcp;
        private NetworkStream? _ns;
        private StreamReader?  _sr;
        private StreamWriter?  _sw;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public bool IsConnected => _tcp?.Connected ?? false;

        public async Task ConnectAsync(string host, int port = 5025, int timeoutMs = 10_000)
        {
            _tcp = new TcpClient { ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
            await _tcp.ConnectAsync(host, port);
            _ns = _tcp.GetStream();
            _sr = new StreamReader(_ns, Encoding.ASCII);
            _sw = new StreamWriter(_ns, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
        }

        public void Disconnect()
        {
            try { _sw?.Dispose(); _sr?.Dispose(); _ns?.Dispose(); _tcp?.Dispose(); }
            catch { }
            _sw = null; _sr = null; _ns = null; _tcp = null;
        }

        public async Task SendAsync(string cmd, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try { await _sw!.WriteLineAsync(cmd); }
            finally { _lock.Release(); }
        }

        public async Task<string> QueryAsync(string query, int timeoutMs = 10_000, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                await _sw!.WriteLineAsync(query);
                // Link caller token with the response timeout so Stop aborts a
                // pending read immediately, not only when the timeout elapses.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                try
                {
                    var resp = await _sr!.ReadLineAsync(cts.Token);
                    return resp?.Trim() ?? string.Empty;
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;   // external cancel → propagate
                    throw new TimeoutException($"CMW500 did not respond within {timeoutMs}ms to: {query}");
                }
            }
            finally { _lock.Release(); }
        }

        public async Task SendOpcAsync(string cmd, int timeoutMs = 30_000, CancellationToken ct = default)
        {
            await SendAsync(cmd, ct);
            await QueryAsync("*OPC?", timeoutMs, ct);
        }

        // ── State pollers ──────────────────────────────────────────────

        public async Task WaitCellStatAsync(string targetPrefix, int timeoutMs = 30_000,
                                             CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var s = await QueryAsync("SOUR:LTE:SIGN1:CELL:STAT:ALL?", 5000);
                if (s.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase)) return;
                await Task.Delay(150, ct);
            }
            throw new TimeoutException($"Cell stat timeout (wanted: {targetPrefix})");
        }

        /// <summary>
        /// Waits for CEST, handling the post-switch UE state machine:
        ///   ON → SIGN → ATT → [auto-send CONN] → CONN → CEST
        /// </summary>
        public async Task WaitForCestAsync(int timeoutMs = 120_000, CancellationToken ct = default,
                                            Func<CancellationToken, Task>? pauseCheck = null)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            bool connSent = false;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (pauseCheck != null) await pauseCheck(ct);

                var s = await QueryAsync("FETC:LTE:SIGN1:PSW:STAT?", 5000);

                if (s.Equals("CEST", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!connSent && s.Equals("ATT", StringComparison.OrdinalIgnoreCase))
                {
                    await SendAsync("CALL:LTE:SIGN1:PSW:ACT CONN");
                    connSent = true;
                }

                await Task.Delay(200, ct);
            }
            throw new TimeoutException("Timed out waiting for CEST");
        }

        public async Task WaitUeStatAsync(string target, int timeoutMs = 30_000,
                                           CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var s = await QueryAsync("FETC:LTE:SIGN1:PSW:STAT?", 5000);
                if (s.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
                await Task.Delay(200, ct);
            }
            throw new TimeoutException($"UE stat timeout (wanted: {target})");
        }

        public async Task WaitMeasRdyAsync(int timeoutMs = 60_000, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string lastStat = "";
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var s = await QueryAsync("FETC:LTE:MEAS:MEV:STAT:ALL?", 8000);
                if (s.StartsWith("RDY", StringComparison.OrdinalIgnoreCase)) return;
                if (s != lastStat) { lastStat = s; }
                await Task.Delay(200, ct);
            }
            throw new TimeoutException($"Meas RDY timeout ({timeoutMs / 1000}s) — last status: {lastStat}");
        }

        public void Dispose() => Disconnect();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MainWindow
    // ═══════════════════════════════════════════════════════════════════

    public partial class MainWindow : Window
    {
        // ── Operating band ────────────────────────────────────────────
        private const string BandCode = "OB26";       // Band 26 (814–849 UL)
        private const double PMax     = 23.0;          // UL PMAX (dBm)
        private const double LimitHi  = 50.0;          // conducted power limit (≤ 50 dBm)

        // B26 UL EARFCN:  N_UL = 26690 + 10·(F_UL − 814)
        private static int Earfcn(double fMHz) => 26690 + (int)Math.Round((fMHz - 814.0) * 10.0);

        // ── Document test model ───────────────────────────────────────
        //  1.4 MHz (B014)  814.7 / 819 / 823.3 — same lines for QPSK & 16QAM
        //  3   MHz (B030)  815.5 / 819 / 822.5 — same lines for QPSK & 16QAM
        //  5   MHz (B050)  816.5 / 819 / 821.5 — same lines for QPSK & 16QAM
        //  10  MHz (B100)  819 only — QPSK and 16QAM differ
        private static readonly List<BwDef> Bandwidths = new()
        {
            new BwDef
            {
                Name = "1.4 MHz", Code = "B014",
                Freqs = new[] { 814.7, 819.0, 823.3 },
                LineFactory = _ => new()
                {
                    (1,0),(1,2),(1,5),
                    (3,0),(3,2),(3,3),
                    (6,0),
                }
            },
            new BwDef
            {
                Name = "3 MHz", Code = "B030",
                Freqs = new[] { 815.5, 819.0, 822.5 },
                LineFactory = _ => new()
                {
                    (1,0),(1,7),(1,14),
                    (8,0),(8,4),(8,7),
                    (15,0),
                }
            },
            new BwDef
            {
                Name = "5 MHz", Code = "B050",
                Freqs = new[] { 816.5, 819.0, 821.5 },
                LineFactory = _ => new()
                {
                    (1,0),(1,13),(1,24),
                    (12,0),(12,6),(12,13),
                    (25,0),
                }
            },
            new BwDef
            {
                Name = "10 MHz", Code = "B100",
                Freqs = new[] { 819.0 },
                LineFactory = mod => mod == "Q16"
                    ? new()
                    {
                        (1,0),(1,25),(1,49),
                        (12,0),(12,19),(12,38),
                        (27,0),
                    }
                    : new()
                    {
                        (1,0),(1,25),(1,49),
                        (25,0),(25,13),(25,25),
                        (50,0),
                    }
            },
        };

        private static readonly (string Token, string Label)[] Modulations =
        {
            ("QPSK", "QPSK"),
            ("Q16",  "16QAM"),
        };

        // RB → TBS 索引 (I_TBS) 对照表 (不分带宽, 用户实测给定值)。
        private static readonly Dictionary<int, int> QpskTbs = new()
        { {1,5}, {3,6}, {6,6}, {8,6}, {12,6}, {15,5}, {25,5}, {50,6} };
        private static readonly Dictionary<int, int> Q16Tbs = new()
        { {1,19}, {3,19}, {6,19}, {8,19}, {12,19}, {15,14}, {25,11}, {27,10} };

        /// <summary>按调制方式 + RB 数查 TBS 索引; 表中无该 RB 时用默认 (QPSK=6 / 16QAM=15)。</summary>
        private static int TbsFor(string modToken, int rb)
        {
            var tbl = modToken == "Q16" ? Q16Tbs : QpskTbs;
            return tbl.TryGetValue(rb, out int v) ? v : (modToken == "Q16" ? 15 : 6);
        }

        private CmwController?           _cmw;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pauseCts;
        private bool _paused = false;
        private readonly ObservableCollection<TestRow> _rows = new();
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        private List<(double FreqHz, double InLoss, double OutLoss)>? _cmwLossData;
        private string? _cmwLossFileName;
        private bool _applyCableLoss;   // captured from ChkCmwLoss at test start (UI-thread safe)

        // ── Recovery (SIM AT reset, triggered on invalid measurement) ──
        private readonly SemaphoreSlim _cellGate = new(1, 1);  // 小区控制权互斥
        private SimSimulatorClient?    _sim;

        public MainWindow()
        {
            InitializeComponent();
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.png")); } catch { }
            DgResults.ItemsSource = _rows;

            _clockTimer.Tick += (_, _) => TxbClock.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            _clockTimer.Start();

            CbIpAddress.ItemsSource = new[] {
                new { Name = "192.0.2.10" },
                new { Name = "192.0.2.10" },
            };
            CbIpAddress.SelectedIndex = 1;

            BuildBandwidthCheckboxes();   // 带宽勾选框, 默认全选
            RebuildPlan();

            // 默认线损表: 启动即加载, connect 时写入 (勾选状态下)
            try
            {
                if (File.Exists(DefaultLossFile))
                {
                    var d = ParseLossCsv(DefaultLossFile);
                    if (d.Count > 0)
                    {
                        _cmwLossData = d;
                        _cmwLossFileName = Path.GetFileName(DefaultLossFile);
                    }
                }
            }
            catch { }
            RefreshLossButtons();
            if (ChkCmwLoss.IsChecked == true)
                SetLossStatus(_cmwLossData != null
                    ? $"默认线损表已加载: {_cmwLossFileName} (连接后写入)"
                    : "请导入线损表", 0xFF, 0xA5, 0x00);
            else
                SetLossStatus("不设置线损表", 0x85, 0x85, 0x85);
        }

        private const string DefaultLossFile = @"D:\RFtest\RFtestCabloss\LossTable_Template.csv";

        /// <summary>解析线损 CSV (TableName 行 + Freq 表头 + 数据段), 频率转 Hz, 按频率排序。</summary>
        private static List<(double FreqHz, double InLoss, double OutLoss)> ParseLossCsv(string path)
        {
            var rows = new List<(double FreqHz, double InLoss, double OutLoss)>();
            bool inData = false;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith("TableName,", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.StartsWith("Freq", StringComparison.OrdinalIgnoreCase)) { inData = true; continue; }
                if (!inData) continue;

                var c = t.Split(',');
                if (c.Length >= 2
                    && double.TryParse(c[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fMHz)
                    && double.TryParse(c[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double inDb))
                {
                    double outDb = inDb;   // Output 列缺省时沿用 Input
                    if (c.Length >= 3)
                        double.TryParse(c[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out outDb);
                    rows.Add((fMHz * 1_000_000, inDb, outDb));
                }
            }
            rows.Sort((a, b) => a.FreqHz.CompareTo(b.FreqHz));
            return rows;
        }

        private void RefreshLossButtons()
        {
            bool on = ChkCmwLoss.IsChecked == true;
            var active = new SolidColorBrush(Color.FromRgb(0x7B, 0x2F, 0xBE));
            var idle   = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            BtnExportLossTemplate.Background = on ? active : idle;
            BtnImportLoss.Background         = on ? active : idle;
        }

        // ── Test-plan select-all header checkbox ──────────────────────
        private void ChkSelectAll_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool on = (sender as CheckBox)?.IsChecked == true;
            foreach (var r in _rows) r.IsSelected = on;
            UpdateResultSummary();
        }

        // ══════════════════════════════════════════════════════════════
        //  Plan building — 带宽勾选框, 默认全选; 每个勾选的带宽测其全部频率
        // ══════════════════════════════════════════════════════════════

        private void BuildBandwidthCheckboxes()
        {
            BwPanel.Children.Clear();
            foreach (var bw in Bandwidths)
            {
                var cb = new CheckBox
                {
                    Content   = bw.Name,
                    IsChecked = true,           // 默认全部勾选
                    Tag       = bw,
                    Margin    = new Thickness(0, 2, 14, 2),
                    FontSize  = 12,
                    MinWidth  = 74,
                };
                cb.Checked   += BwCheck_Changed;
                cb.Unchecked += BwCheck_Changed;
                BwPanel.Children.Add(cb);
            }
        }

        private void BwCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            RebuildPlan();
        }

        private void RebuildPlan()
        {
            if (BwPanel == null) return;
            _rows.Clear();
            int seq = 0;
            foreach (var cb in BwPanel.Children.OfType<CheckBox>())
            {
                if (cb.IsChecked != true || cb.Tag is not BwDef bw) continue;
                for (int fi = 0; fi < bw.Freqs.Length; fi++)
                {
                    double f = bw.Freqs[fi];
                    int ea = Earfcn(f);
                    // 频率在本带宽中的位置 → 信道类型 (单频率带宽如 10MHz 视为 Low)
                    string chanType = fi == 0 ? "Low" : fi == 1 ? "Mid" : "High";
                    foreach (var (token, label) in Modulations)
                    {
                        foreach (var (rb, off) in bw.LinesFor(token))
                        {
                            _rows.Add(new TestRow
                            {
                                Seq         = ++seq,
                                ChannelType = chanType,
                                BwLabel     = bw.Name,
                                BwCode      = bw.Code,
                                FreqMHz     = f,
                                Earfcn      = ea,
                                Modulation  = label,
                                ModToken    = token,
                                RbSize      = rb,
                                RbOffset    = off,
                                TbsIdx      = TbsFor(token, rb),
                                IsSelected  = true,
                            });
                        }
                    }
                }
            }
            UpdateResultSummary();
        }

        // ══════════════════════════════════════════════════════════════
        //  UI helpers
        // ══════════════════════════════════════════════════════════════

        private async Task WaitIfPausedAsync(CancellationToken ct)
        {
            while (_paused)
            {
                ct.ThrowIfCancellationRequested();
                try { await Task.Delay(100, _pauseCts!.Token); }
                catch (OperationCanceledException) { }
            }
        }

        /// <summary>Cancellation + pause checkpoint — call between SCPI steps.</summary>
        private async Task GuardAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct);
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>可暂停/可停止的等待: 每 100ms 检查一次停止与暂停, 使按钮即时生效。</summary>
        private async Task GuardedDelay(int ms, CancellationToken ct)
        {
            const int step = 100;
            int elapsed = 0;
            while (elapsed < ms)
            {
                ct.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(ct);
                int d = Math.Min(step, ms - elapsed);
                await Task.Delay(d, ct);
                elapsed += d;
            }
        }

        private void SetStatus(string msg) => Dispatcher.InvokeAsync(() => TxbStatus.Text = msg);

        private void AppendLog(string line)
            => Dispatcher.InvokeAsync(() =>
            {
                TxbLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}]  {line}\n");
                TxbLog.ScrollToEnd();
            });

        private void SetConnected(bool on)
            => Dispatcher.InvokeAsync(() =>
            {
                ConnLed.Fill = new SolidColorBrush(on
                    ? Color.FromRgb(0x4E, 0xC9, 0x4E)
                    : Color.FromRgb(0xF4, 0x47, 0x47));
                ConnLabel.Text          = on ? "Connected" : "Disconnected";
                BtnConnect.IsEnabled    = !on;
                BtnDisconnect.IsEnabled = on;
                BtnStartTest.IsEnabled  = on;
            });

        // ══════════════════════════════════════════════════════════════
        //  Connection
        // ══════════════════════════════════════════════════════════════

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            BtnConnect.IsEnabled = false;
            SetStatus("Connecting…");
            try
            {
                _cmw = new CmwController();
                const int port = 5025;   // 端口固定 5025
                string ip = ((dynamic)CbIpAddress.SelectedItem).Name;
                await _cmw.ConnectAsync(ip, port);
                var idn = await _cmw.QueryAsync("*IDN?");
                AppendLog($"Connected → {idn}");
                SetStatus($"Connected: {idn}");
                SetConnected(true);

                // 连接后写入线损表 (勾选且已加载默认/导入的线损表)
                if (ChkCmwLoss.IsChecked == true && _cmwLossData != null && _cmwLossData.Count > 0)
                {
                    try
                    {
                        AppendLog("=== 连接后写入线损表 (FDCorrection) ===");
                        await UploadFdcTablesAsync(_cmw, CancellationToken.None);
                        SetLossStatus("线损表设置成功", 0x4E, 0xC9, 0x4E);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[Loss] 连接后写入线损表失败: {ex.Message}");
                        SetLossStatus("线损表写入失败", 0xF4, 0x47, 0x47);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] {ex.Message}");
                SetStatus("Connection failed.");
                BtnConnect.IsEnabled = true;
                _cmw?.Disconnect(); _cmw = null;
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            _cmw?.Disconnect(); _cmw = null;
            SetConnected(false);
            SetStatus("Disconnected.");
            AppendLog("Disconnected.");
        }

        // ══════════════════════════════════════════════════════════════
        //  Cable-loss table  (CMW500 FDCorrection — 参考 LTESensitivitySearchRE4.0)
        //
        //  导出模板: CSV (UTF-8 BOM) —— TableName + Freq(MHz)/Input/Output。
        //  导入线损: 解析 CSV → 立即上传到 CMW500 并激活 (需已连接):
        //    Input(dB) → RX 路径修正表,  Output(dB) → TX 路径修正表。
        //    CONF:BASE:FDCorrection:CTABle:CREate / SAV / FDCorrection:ACTivate RF1C。
        // ══════════════════════════════════════════════════════════════

        private const string LossTableBaseName = "Cable_Loss";

        private async void ChkCmwLoss_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;   // 忽略 XAML 加载期间 IsChecked="True" 的初始触发
            RefreshLossButtons();

            if (ChkCmwLoss.IsChecked != true)
            {
                SetLossStatus("不设置线损表", 0x85, 0x85, 0x85);
                // 关闭时若已连接, 解除 CMW500 上的修正激活, 使仪器状态与界面一致
                if (_cmw is { IsConnected: true })
                {
                    try { await _cmw.SendAsync("CONFigure:FDCorrection:DEACtivate RF1C"); AppendLog("[Loss] 已解除 RF1C 线损修正"); }
                    catch (Exception ex) { AppendLog($"[Loss] 解除修正失败: {ex.Message}"); }
                }
                return;
            }

            if (_cmwLossData == null) { SetLossStatus("请导入线损表", 0xFF, 0xA5, 0x00); return; }

            // 勾选且有数据: 已连接则立即写入, 否则标记连接后写入
            if (_cmw is { IsConnected: true })
            {
                try { await UploadFdcTablesAsync(_cmw, CancellationToken.None); SetLossStatus("线损表设置成功", 0x4E, 0xC9, 0x4E); }
                catch (Exception ex) { AppendLog($"[Loss] 写入失败: {ex.Message}"); SetLossStatus("线损表写入失败", 0xF4, 0x47, 0x47); }
            }
            else SetLossStatus($"已就绪: {_cmwLossFileName} (连接后写入)", 0xFF, 0xA5, 0x00);
        }

        private void SetLossStatus(string text, byte r, byte g, byte b)
        {
            TxbCmwLossStatus.Text = text;
            TxbCmwLossStatus.Foreground = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void BtnExportLossTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "LossTable_Template", DefaultExt = ".csv",
                Filter = "CSV 文件|*.csv|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                // 布局参考 LTESensitivitySearchRE4.0 (UTF-8 BOM + TableName 行 + Freq(MHz)/Input/Output)
                using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
                writer.WriteLine($"TableName,{LossTableBaseName},");
                writer.WriteLine(",,");
                writer.WriteLine("Freq(MHz),Input(dB),Output(dB)");
                writer.WriteLine("100,0.5,0.5");
                writer.WriteLine("700,0.5,0.5");
                writer.WriteLine("900,0.5,0.5");
                writer.WriteLine("1000,0.8,0.8");
                writer.WriteLine("1500,0.8,0.8");
                writer.WriteLine("1800,0.8,0.8");
                writer.WriteLine("2100,1.2,1.2");
                writer.WriteLine("2600,1.2,1.2");
                writer.WriteLine("3500,1.2,1.2");
                writer.WriteLine("6000,1.2,1.2");
                AppendLog($"线损模板已导出 → {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnImportLoss_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".csv", Filter = "CSV 文件|*.csv|所有文件|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            BtnImportLoss.IsEnabled = false;
            try
            {
                var rows = ParseLossCsv(dlg.FileName);
                if (rows.Count == 0)
                    throw new InvalidDataException("没有有效频点 (格式: Freq(MHz),Input(dB),Output(dB))");

                _cmwLossData     = rows;
                _cmwLossFileName = Path.GetFileName(dlg.FileName);

                AppendLog($"=== 导入线损表: {_cmwLossFileName} ({rows.Count} 频点) ===");
                foreach (var (f, i, o) in rows)
                    AppendLog($"    {f / 1e6:F1} MHz → INP {i:F2} dB / OUTP {o:F2} dB");

                if (ChkCmwLoss.IsChecked != true) ChkCmwLoss.IsChecked = true;   // 未勾选则勾上, 触发写入逻辑
                else if (_cmw is { IsConnected: true })
                {
                    await UploadFdcTablesAsync(_cmw, CancellationToken.None);
                    SetLossStatus("线损表设置成功", 0x4E, 0xC9, 0x4E);
                }
                else SetLossStatus($"已就绪: {_cmwLossFileName} (连接后写入)", 0xFF, 0xA5, 0x00);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog($"[Loss] 导入失败: {ex.Message}");
                _cmwLossData = null; _cmwLossFileName = null;
                if (ChkCmwLoss.IsChecked == true) SetLossStatus("请导入线损表", 0xFF, 0xA5, 0x00);
            }
            finally { BtnImportLoss.IsEnabled = true; }
        }

        /// <summary>把导入的线损表写入 CMW500 并激活为 RF1C 的 External Attenuation 修正表
        /// (User Correction / FDCorrection): INP 列→RX 表, OUT 列→TX 表; 仪器按频率自动插值。</summary>
        private async Task UploadFdcTablesAsync(CmwController cmw, CancellationToken ct)
        {
            var d = _cmwLossData;
            if (d == null || d.Count == 0) return;
            string tblRx = $"{LossTableBaseName}_RX";
            string tblTx = $"{LossTableBaseName}_TX";

            await cmw.SendAsync("CONF:BASE:FDC:RCL", ct);
            await CreateFdcTableAsync(cmw, tblRx, d.Select(r => (r.FreqHz, r.InLoss)), ct);
            await CreateFdcTableAsync(cmw, tblTx, d.Select(r => (r.FreqHz, r.OutLoss)), ct);
            await cmw.SendAsync("CONF:BASE:FDCorrection:SAV", ct);

            await cmw.SendAsync("CONFigure:FDCorrection:DEACtivate RF1C", ct);
            await cmw.SendAsync($"CONFigure:FDCorrection:ACTivate RF1C,\"{tblRx}\",RX", ct);
            await cmw.SendAsync($"CONFigure:FDCorrection:ACTivate RF1C,\"{tblTx}\",TX", ct);

            var usage = await cmw.QueryAsync("CONFigure:FDCorrection:USAGe? RF1C", 5000, ct);
            AppendLog($"  RF1C External Attenuation 修正表: {usage}");
        }

        /// <summary>创建/覆盖一张 FDCorrection 修正表 (freq[Hz], loss[dB] 交替)。</summary>
        private static async Task CreateFdcTableAsync(
            CmwController cmw, string name, IEnumerable<(double FreqHz, double LossDb)> pairs, CancellationToken ct)
        {
            var exists = await cmw.QueryAsync($"CONFigure:BASE:FDCorrection:CTABle:EXISt? \"{name}\"", 5000, ct);
            if (exists.Trim() == "1")
                await cmw.SendAsync($"CONFigure:BASE:FDCorrection:CTABle:DELete \"{name}\"", ct);
            var sb = new StringBuilder($"CONFigure:BASE:FDCorrection:CTABle:CREate \"{name}\"");
            foreach (var (f, l) in pairs)
                sb.Append($",{f.ToString("F0", CultureInfo.InvariantCulture)},{l.ToString("F2", CultureInfo.InvariantCulture)}");
            await cmw.SendAsync(sb.ToString(), ct);
        }

        // ══════════════════════════════════════════════════════════════
        //  Test control
        // ══════════════════════════════════════════════════════════════

        private async void BtnStartTest_Click(object sender, RoutedEventArgs e) => await StartTest();

        private async Task StartTest()
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("未勾选任何测试条目。", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // reset the status of selected rows
            foreach (var r in selected)
            {
                r.MeasuredPowerStr = "─";
                r.StatusText = "─"; r.StatusColor = "#858585"; r.Note = "";
            }

            _cts = new CancellationTokenSource();
            _paused = false;
            _pauseCts?.Cancel();
            _pauseCts = new CancellationTokenSource();
            BtnPause.Content       = "⏸  暂停";
            BtnStartTest.IsEnabled = false;
            BtnStopTest.IsEnabled  = true;
            BtnPause.IsEnabled     = true;
            BtnExport.IsEnabled    = false;
            ProgBar.Value          = 0;

            bool skipInit = true;   // 固定跳过初始化 (样机已连接)
            _applyCableLoss = ChkCmwLoss.IsChecked == true;   // UI-thread capture

            await Task.Run(() => RunTestSequenceAsync(selected, skipInit, _cts.Token));

            BtnStartTest.IsEnabled = true;
            BtnStopTest.IsEnabled  = false;
            BtnPause.IsEnabled     = false;
            BtnPause.Content       = "⏸  暂停";
            BtnExport.IsEnabled    = _rows.Any(r => r.StatusText is "PASS" or "FAIL");
            UpdateResultSummary();
        }

        private void BtnStopTest_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            if (_paused) { _paused = false; _pauseCts?.Cancel(); }
            AppendLog("⚠  Stop requested.");
            SetStatus("Stopping…");
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_paused)
            {
                _paused   = true;
                _pauseCts = new CancellationTokenSource();
                BtnPause.Content = "▶  继续";
                AppendLog("⏸ Paused — click 继续 to continue.");
            }
            else
            {
                _paused = false;
                _pauseCts?.Cancel();
                BtnPause.Content = "⏸  暂停";
                AppendLog("▶ Resumed.");
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e) => ExportExcel();
        private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxbLog.Clear();

        // ══════════════════════════════════════════════════════════════
        //  Recovery — 仅在测量得到 invalid 结果时触发 (无后台 RRC 监控)。
        //  每次: 先 LTE signaling OFF/ON, 再发 SIM AT 复位序列
        //        (cd uart3_proxy / ECSIMCFG / ECRST / CFUN=1) → 等 10s → 查 RRC;
        //  connected 则成功返回, 否则重复, 最多 10 次; 10 次仍未连接则放弃, 切下一信道。
        // ══════════════════════════════════════════════════════════════

        private async Task<bool> RecoverViaSimAsync(CmwController cmw, CancellationToken ct)
        {
            if (!CanInitDut()) return false;   // 静默: 有效期过后不再初始化样机
            _sim ??= new SimSimulatorClient(AppendLog);
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                await GuardAsync(ct);
                AppendLog($"[Recover] 第 {attempt}/10 次: LTE signaling OFF/ON + SIM AT 复位…");

                // 1) LTE signaling OFF/ON
                try
                {
                    await cmw.SendAsync("SOUR:LTE:SIGN1:CELL:STAT OFF", ct);
                    try { await cmw.WaitCellStatAsync("OFF", 8_000, ct); } catch { }
                    await GuardedDelay(1500, ct);
                    await cmw.SendAsync("SOUR:LTE:SIGN1:CELL:STAT ON", ct);
                    try { await cmw.WaitCellStatAsync("ON,ADJ", 15_000, ct); } catch { }
                    await cmw.SendAsync("CLE:LTE:SIGN1:EEL", ct);
                }
                catch (Exception ex) { AppendLog($"[Recover] 小区 OFF/ON 异常 (忽略): {ex.Message}"); }

                // 2) SIM AT 复位序列
                try { await _sim.RunResetSequenceAsync(ct); }
                catch (Exception ex) { AppendLog($"[SIM] 复位异常 (忽略): {ex.Message}"); }

                // 3) 等 10s 让样机重启并重附着, 再查 RRC
                await GuardedDelay(10_000, ct);
                var rrc = await QueryRrcSafeAsync(cmw, ct);
                AppendLog($"[Recover] 第 {attempt} 次后 RRC={rrc}");
                if (rrc == RrcState.Connected) { AppendLog("[Recover] 已恢复连接 ✓"); return true; }
            }
            AppendLog("[Recover] 10 次仍未连接 — 放弃本信道");
            return false;
        }

        /// <summary>查 RRC 连接态: ATT/CATT/CEST/CONN/HAND=Connected; OFF/IDLE/REL/SIGN=Idle; 其它=Unknown。</summary>
        private async Task<RrcState> QueryRrcSafeAsync(CmwController cmw, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = (await cmw.QueryAsync("FETC:LTE:SIGN1:PSW:STAT?", 5_000, ct))
                           .Trim().ToUpperInvariant().Trim('"');
                if (resp.Contains("CEST") || resp.Contains("CONN") || resp.Contains("HAND")
                    || resp == "ATT" || resp == "CATT")
                    return RrcState.Connected;
                if (resp.Contains("IDLE") || resp == "OFF" || resp == "REL" || resp == "SIGN")
                    return RrcState.Idle;
                return RrcState.Unknown;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog($"[RRC] 查询异常 (按未知处理): {ex.Message}"); return RrcState.Unknown; }
        }

        // ══════════════════════════════════════════════════════════════
        //  Build-validity guard (silent).
        //  Each published build embeds an AssemblyMetadata "BuildTimeUtc".
        //  Beyond 518400 minutes (360 days) the DUT can no longer be
        //  initialised.  Fail-safe: missing/unparseable stamp ⇒ expired.
        //  No message is ever shown — before, during, or after.
        // ══════════════════════════════════════════════════════════════
        private const int ValidityMinutes = 518400;

        private static bool CanInitDut()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var a in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (!string.Equals(a.Key, "BuildTimeUtc", StringComparison.Ordinal)) continue;
                    if (DateTime.TryParse(a.Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out var built))
                    {
                        return DateTime.UtcNow <= built.ToUniversalTime().AddMinutes(ValidityMinutes);
                    }
                    return false;   // present but unparseable
                }
                return false;       // stamp missing
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        //  Core test sequence
        // ══════════════════════════════════════════════════════════════

        private async Task RunTestSequenceAsync(
            List<TestRow> rows, bool skipInit,
            CancellationToken ct)
        {
            // Silent build-validity guard — cannot initialise DUT once expired.
            if (!CanInitDut()) return;

            var cmw = _cmw!;
            int total = rows.Count;
            int done  = 0;

            // ── Global init (skip if UE already connected) ───────────
            if (skipInit)
            {
                AppendLog("=== Skip init mode — UE already connected ===");
            }
            else
            {
                try
                {
                    AppendLog("=== Global initialisation ===");
                    SetStatus("Initialising CMW500…");

                    await cmw.SendOpcAsync("SYST:RES:ALL", 30_000);
                    await cmw.SendOpcAsync("*RST", 10_000);
                    AppendLog("Reset done.");

                    await cmw.SendOpcAsync("ROUT:LTE:SIGN1:SCEN:SCEL RF1C,RX1,RF1C,TX1", 10_000);
                    await cmw.SendAsync("CONF:LTE:SIGN1:EMTC:ENAB OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DMOD FDD");

                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:SEC:AUTH OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:SEC:NAS OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:SEC:AS OFF");

                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:MCC 1");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:MNC:DIG TWO");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:MNC 1");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:TAC 1");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:NAS:EPSN OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:NAS:IMSV NSUP");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:NAS:EMCB NSUP");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:NAS:EPCL NSUP");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:NAS:CSLC NSUP");

                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:RLCM UM");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:TMOD ON");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:DLP ON");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:DLE 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:ASEM NS01");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:KRRC OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:AMDB ON");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:STYP UDCH");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:TSCH SISO");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:FCO FC4");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:FCH BHAN");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CONN:OBCH BHAN");

                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:PRAC:PCIN 12");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:PRAC:NRPR OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:TOUT:OSYN 30");
                    await cmw.SendAsync("CONF:LTE:SIGN1:CELL:PCID 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:OCNG OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:AWGN OFF");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:SSS:POFF 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:PBCH:POFF 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:PSS:POFF 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:PCF:POFF 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:PDCC:POFF 0");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:PDSC:PA ZERO");
                    await cmw.SendAsync("CONF:LTE:SIGN1:DL:PDSC:RIND 0");
                    await cmw.QueryAsync("*OPC?");

                    AppendLog("Global init complete.");
                }
                catch (OperationCanceledException) { AppendLog("Cancelled during init."); SetStatus("Cancelled."); return; }
                catch (Exception ex)               { AppendLog($"[ERROR] Init: {ex.Message}"); SetStatus("Init failed."); return; }
            }

            // ── Cable loss (External Attenuation = FDCorrection 修正表) ──
            //  启用: 确保线损表已写入并激活到 RF1C; 未启用: 解除修正。
            //  信令标量外部衰减保持 0, 避免与修正表双重补偿。
            try
            {
                await cmw.SendAsync("CONF:LTE:SIGN1:RFSettings:EATTenuation:INPut 0", ct);
                await cmw.SendAsync("CONF:LTE:SIGN1:RFSettings:EATTenuation:OUTPut 0", ct);
                if (_applyCableLoss && _cmwLossData != null && _cmwLossData.Count > 0)
                {
                    await UploadFdcTablesAsync(cmw, ct);
                    AppendLog("线损已启用: External Attenuation 修正表已激活");
                }
                else
                {
                    await cmw.SendAsync("CONFigure:FDCorrection:DEACtivate RF1C", ct);
                    AppendLog("线损未启用: 已解除 External Attenuation 修正");
                }
            }
            catch (Exception ex) { AppendLog($"[Loss] 初始化异常 (忽略): {ex.Message}"); }

            // ── Per-row loop ─────────────────────────────────────────
            //  Channel = (BwCode, Earfcn). Re-attach only when the channel
            //  changes; within a channel just change the UDCH allocation.
            (string Bw, int Ea) prevChan = ("", -1);
            (string Bw, int Ea) lastGoodChan = ("", -1);
            bool firstChannel = true;

            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) break;
                await WaitIfPausedAsync(ct);

                done++;
                var chan = (row.BwCode, row.Earfcn);
                bool chanChanged = chan != prevChan;

                row.StatusText = "TESTING"; row.StatusColor = "#DCDCAA"; row.Note = "…";
                Dispatcher.Invoke(() => DgResults.ScrollIntoView(row));
                AppendLog("");
                AppendLog($"══ {row.BwLabel}  {row.FreqStr} MHz  {row.Modulation}  RB={row.RbSize}@{row.RbOffset}  ({done}/{total}) ══");
                SetStatus($"Testing {row.BwLabel} {row.FreqStr}MHz {row.Modulation} RB{row.RbSize}@{row.RbOffset}  ({done}/{total})");

                // 独占小区控制权 (与后台 RRC 恢复互斥): 本条目的 setup/measure 期间
                // 后台轮询只观测不恢复; 条目之间空隙才让后台执行恢复。
                try { await _cellGate.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                try
                {
                    if (chanChanged)
                    {
                        await SetupChannelAsync(cmw, row, skipInit, firstChannel,
                                                lastGoodChan, ct);
                        firstChannel = false;
                        lastGoodChan = chan;
                    }

                    await ApplyUdchAsync(cmw, row, ct);
                    await MeasureAndEvaluateAsync(cmw, row, ct);
                    prevChan = chan;
                }
                catch (OperationCanceledException)
                {
                    row.StatusText = "ABORT"; row.StatusColor = "#F44747"; row.Note = "Stopped by user";
                    AppendLog("Aborted."); break;
                }
                catch (Exception ex)
                {
                    row.StatusText = "ERROR"; row.StatusColor = "#F44747"; row.Note = ex.Message;
                    AppendLog($"[ERROR] {ex.Message}");
                    prevChan = ("", -1);   // force re-setup on next row
                }
                finally { _cellGate.Release(); }

                Dispatcher.Invoke(() => { ProgBar.Value = (double)done / total * 100.0; UpdateResultSummary(); });
            }

            // ── Cleanup ──────────────────────────────────────────────
            await _cellGate.WaitAsync(CancellationToken.None);
            try
            {
                AppendLog("=== Cleanup ===");
                var s = await cmw.QueryAsync("FETC:LTE:SIGN1:PSW:STAT?", 3_000);
                if (!s.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    await cmw.SendAsync("CALL:LTE:SIGN1:PSW:ACT DISC");
                    try { await cmw.WaitUeStatAsync("ATT", 3_000); } catch { }   // 短等待: 掉线时不空耗
                }
                await cmw.SendAsync("SOUR:LTE:SIGN1:CELL:STAT OFF");
                try { await cmw.WaitCellStatAsync("OFF", 5_000); } catch { }
                AppendLog("Cell OFF.");
            }
            catch (Exception ex) { AppendLog($"[WARN] Cleanup: {ex.Message}"); }
            finally { _cellGate.Release(); }

            int p = _rows.Count(r => r.StatusText == "PASS");
            int f = _rows.Count(r => r.StatusText == "FAIL");
            SetStatus($"Done — {(f > 0 ? "FAIL" : "PASS")}.  PASS={p}  FAIL={f}");
            AppendLog("=== ALL DONE ===");
        }

        // ══════════════════════════════════════════════════════════════
        //  Channel setup — band / bandwidth / EARFCN + attach to CEST
        // ══════════════════════════════════════════════════════════════

        private async Task SetupChannelAsync(
            CmwController cmw, TestRow row, bool skipInit, bool firstChannel,
            (string Bw, int Ea) prevChan, CancellationToken ct)
        {
            // ── Skip-init fast path ───────────────────────────────────
            // 只设 BAND/带宽/EARFCN; 不再做 CEST 检查或 OFF/ON 重连。
            // 若样机已掉线, 后续测量会得到 invalid → 由 RecoverViaSimAsync 触发 SIM 复位恢复。
            if (skipInit)
            {
                AppendLog($"  [skip] BAND {BandCode}  BW {row.BwCode}  EARFCN {row.Earfcn}");
                await cmw.SendAsync($"CONF:LTE:SIGN1:BAND {BandCode}", ct);
                await cmw.SendAsync($"CONF:LTE:SIGN1:CELL:BAND:DL {row.BwCode}", ct);
                await cmw.SendAsync($"CONF:LTE:SIGN1:CELL:BAND:UL {row.BwCode}", ct);
                await cmw.SendAsync($"CONF:LTE:SIGN1:RFS:CHAN:UL {row.Earfcn}", ct);
                await GuardedDelay(2000, ct);
                AppendLog($"  [skip] BW {row.BwCode} EARFCN {row.Earfcn} ✓");
                return;
            }

            // ── Full path ─────────────────────────────────────────────
            // Disconnect UE if connected
            var ues = await cmw.QueryAsync("FETC:LTE:SIGN1:PSW:STAT?");
            if (!ues.Equals("OFF", StringComparison.OrdinalIgnoreCase)
             && !ues.Equals("ATT", StringComparison.OrdinalIgnoreCase)
             && !ues.Equals("DISC", StringComparison.OrdinalIgnoreCase))
            {
                await cmw.SendAsync("CALL:LTE:SIGN1:PSW:ACT DISC");
                try { await cmw.WaitUeStatAsync("ATT", 10_000, ct); } catch { }
                await Task.Delay(300, ct);
            }

            // Cell OFF
            AppendLog("  [1] Cell OFF");
            await cmw.SendAsync("SOUR:LTE:SIGN1:CELL:STAT OFF");
            try { await cmw.WaitCellStatAsync("OFF", 20_000, ct); } catch { }

            await cmw.SendAsync("CONF:LTE:SIGN1:CONN:KRRC OFF");

            // Band / bandwidth
            AppendLog($"  [2] BAND {BandCode}  BW {row.BwCode}");
            await cmw.SendOpcAsync("ROUT:LTE:SIGN1:SCEN:SCEL RF1C,RX1,RF1C,TX1", 10_000);
            await cmw.SendAsync("CONF:LTE:SIGN1:DMOD FDD");
            await cmw.SendAsync($"CONF:LTE:SIGN1:BAND {BandCode}");
            await cmw.SendAsync($"CONF:LTE:SIGN1:CELL:BAND:DL {row.BwCode}");
            await cmw.SendAsync($"CONF:LTE:SIGN1:CELL:BAND:UL {row.BwCode}");

            // EARFCN
            AppendLog($"  [3] EARFCN {row.Earfcn}  ({row.FreqStr} MHz)");
            await cmw.SendAsync($"CONF:LTE:SIGN1:RFS:CHAN:UL {row.Earfcn}");
            await cmw.QueryAsync("*OPC?");

            // DL level
            await cmw.SendAsync("CONF:LTE:SIGN1:DL:RSEP:LEV -85");

            // Connection channel: UDCH (user-defined) — initial allocation for attach
            await cmw.SendAsync($"CONF:LTE:SIGN1:UL:PMAX {(int)PMax}");
            await cmw.SendAsync("CONF:LTE:SIGN1:CONN:STYP UDCH");
            await cmw.SendAsync($"CONF:LTE:SIGN1:CONN:UDCH:UL {row.RbSize},{row.RbOffset},{row.ModToken},{row.TbsIdx}");

            // UL power control — closed-loop for reliable attach
            AppendLog($"  [4] APP+CLO (PMAX={PMax} dBm)");
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:APP:EAS ON",        5_000);
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:APP:RSP:ADV 18",    5_000);
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:APP:PCAL:ADV DOT8", 5_000);
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:APP:PNP:ADV -85",   5_000);
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:APP:PIRP:ADV -104", 5_000);
            await cmw.SendAsync($"CONF:LTE:SIGN1:UL:PMAX {(int)PMax}");
            await cmw.SendAsync("CONF:LTE:SIGN1:UL:PUSC:TPC:CLTP 0");
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:PUSC:TPC:SET CLO",  5_000);

            await cmw.SendAsync("CONF:LTE:SIGN1:CELL:PRAC:PRST ZERO");
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:CELL:SRS:ENAB OFF", 5_000);
            await cmw.QueryAsync("*OPC?");

            // Cell ON
            AppendLog("  [5] Cell ON");
            await cmw.SendAsync("SOUR:LTE:SIGN1:CELL:STAT ON");
            try { await cmw.WaitCellStatAsync("ON,ADJ", 25_000, ct); } catch { }
            await cmw.SendAsync("CLE:LTE:SIGN1:EEL");

            // Wait for CEST
            AppendLog("  [6] Waiting for CEST…");
            row.Note = "Waiting for UE…";
            try { await cmw.WaitForCestAsync(90_000, ct, WaitIfPausedAsync); }
            catch (TimeoutException)
            {
                AppendLog("  CEST timeout — SIM 复位恢复…");
                await RecoverViaSimAsync(cmw, ct);
            }
            AppendLog("  UE: CEST ✓");

            await Task.Delay(3000, ct);
            var post = await cmw.QueryAsync("FETC:LTE:SIGN1:PSW:STAT?");
            if (!post.Equals("CEST", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"UE dropped from CEST during settle (state={post})");
        }

        // ══════════════════════════════════════════════════════════════
        //  Apply the per-row UDCH allocation (RB size / offset / modulation)
        // ══════════════════════════════════════════════════════════════

        private async Task ApplyUdchAsync(CmwController cmw, TestRow row, CancellationToken ct)
        {
            await GuardAsync(ct);
            AppendLog($"  UDCH UL: {row.RbSize} RB, {row.Modulation}, TBS {row.TbsIdx}, start {row.RbOffset}");
            await cmw.SendAsync("CONF:LTE:SIGN1:CONN:STYP UDCH", ct);
            await cmw.SendAsync($"CONF:LTE:SIGN1:CONN:UDCH:UL {row.RbSize},{row.RbOffset},{row.ModToken},{row.TbsIdx}", ct);
            await cmw.QueryAsync("*OPC?", 10_000, ct);
            // Let the new UL grant take effect on the connected UE.
            await Task.Delay(1500, ct);
        }

        // ══════════════════════════════════════════════════════════════
        //  Measure and evaluate — conducted power ≤ 50 dBm
        // ══════════════════════════════════════════════════════════════

        private async Task MeasureAndEvaluateAsync(
            CmwController cmw, TestRow row, CancellationToken ct)
        {
            // Switch TPC → MAXP so the UE ramps to its maximum output.
            await GuardAsync(ct);
            AppendLog("  TPC → MAXP, ramping to max power…");
            await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:PUSC:TPC:SET MAXP", 5_000, ct);
            await GuardedDelay(3000, ct);

            // Configure Multi-Evaluation
            await cmw.SendOpcAsync("ROUT:LTE:MEAS:SCEN:CSP 'LTE Sig1'", 10_000, ct);
            await cmw.SendAsync("ABOR:LTE:MEAS:MEV", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:REP SING", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:SCON NONE", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:MOD:MSCH AUTO", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:RBAL:AUTO ON", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:CTYP AUTO", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:SOFF 0", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:SLEN 10", ct);
            await cmw.SendAsync("TRIG:LTE:MEAS:MEV:SOUR 'LTE Sig1: FrameTrigger'", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:RES:ALL OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF,OFF", ct);
            await cmw.SendAsync("CONF:LTE:MEAS:MEV:RES:TXM ON", ct);
            await cmw.QueryAsync("*OPC?", 10_000, ct);

            AppendLog("  Initiating measurement…");
            row.Note = "Measuring…";

            async Task<double> MeasureOnceAsync()
            {
                await GuardAsync(ct);
                await cmw.SendAsync("ABOR:LTE:MEAS:MEV", ct);
                await cmw.SendAsync("INIT:LTE:MEAS:MEV", ct);
                await cmw.WaitMeasRdyAsync(30_000, ct);
                await GuardedDelay(500, ct);
                var raw = await cmw.QueryAsync("FETC:LTE:MEAS:MEV:MOD:AVER?", 15_000, ct);
                AppendLog($"  MOD:AVER? → {raw}");
                return ParseMeasuredPower(raw);
            }

            double measPower = await MeasureOnceAsync();

            // invalid result → 触发 SIM AT 复位恢复 (最多 10 次); connected 后重测一次;
            // 恢复失败则放弃本条目, 由主循环切下一信道。
            if (double.IsNaN(measPower))
            {
                AppendLog("  invalid result — 触发 SIM 复位恢复…");
                row.Note = "SIM 复位恢复中…";
                bool recovered = await RecoverViaSimAsync(cmw, ct);
                if (recovered)
                {
                    // 复位后重下 UDCH 分配 + 重置 MAXP, 再测一次
                    await ApplyUdchAsync(cmw, row, ct);
                    await GuardAsync(ct);
                    await cmw.SendOpcAsync("CONF:LTE:SIGN1:UL:PUSC:TPC:SET MAXP", 5_000, ct);
                    await GuardedDelay(3000, ct);
                    measPower = await MeasureOnceAsync();
                }
            }

            bool valid = !double.IsNaN(measPower);
            bool pass  = valid && measPower <= LimitHi;

            row.MeasuredPowerStr = valid ? measPower.ToString("F2", CultureInfo.InvariantCulture) : "NCAP";
            row.StatusText  = !valid ? "ERROR" : pass ? "PASS" : "FAIL";
            row.StatusColor = !valid ? "#858585" : pass ? "#4EC94E" : "#F44747";
            row.Note        = !valid ? "无法解析结果"
                            : pass   ? "≤ 50 dBm"
                            : $"HIGH by {measPower - LimitHi:F1} dB";

            AppendLog($"  → {row.StatusText}  ({(valid ? $"{measPower:F2} dBm" : "NCAP")})");
        }

        // ══════════════════════════════════════════════════════════════
        //  Result parser
        // ══════════════════════════════════════════════════════════════

        private static double ParseMeasuredPower(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return double.NaN;
            var parts = raw.Split(',');

            // Primary: index 17 = TX Power (dBm, cable-loss compensated)
            if (parts.Length > 17)
            {
                var s = parts[17].Trim();
                if (!IsInvalidToken(s)
                    && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    && v > -50 && v < 60)
                    return v;
            }
            // Fallback: scan indices 14–25 for a plausible power value
            for (int i = 14; i < Math.Min(parts.Length, 26); i++)
            {
                var s = parts[i].Trim();
                if (IsInvalidToken(s)) continue;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    && v > 0 && v < 60)
                    return v;
            }
            return double.NaN;
        }

        private static bool IsInvalidToken(string s) =>
            s.Equals("NCAP", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("INV",  StringComparison.OrdinalIgnoreCase) ||
            s.Equals("NAN",  StringComparison.OrdinalIgnoreCase) ||
            s.Equals("NAV",  StringComparison.OrdinalIgnoreCase);

        // ══════════════════════════════════════════════════════════════
        //  Summary & Export
        // ══════════════════════════════════════════════════════════════

        private void UpdateResultSummary()
        {
            int p = _rows.Count(r => r.StatusText == "PASS");
            int f = _rows.Count(r => r.StatusText == "FAIL");
            int e = _rows.Count(r => r.StatusText is "ERROR" or "ABORT");
            int sel = _rows.Count(r => r.IsSelected);
            TxbResultSummary.Text = $"   已选 {sel}/{_rows.Count}   ✓{p}  ✗{f}  !{e}";
        }

        private void ExportExcel()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"LTE_B26_TxPower_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".xlsx", Filter = "Excel Workbook|*.xlsx",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                WriteXlsx(dlg.FileName);
                AppendLog($"Exported → {dlg.FileName}");
                SetStatus($"Exported: {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Minimal xlsx writer (BCL only) ────────────────────────────
        private void WriteXlsx(string path)
        {
            var ss = new List<string>();
            var ssIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            int SI(string v) { if (!ssIdx.TryGetValue(v, out int i)) { i = ss.Count; ss.Add(v); ssIdx[v] = i; } return i; }

            const int S_NORMAL = 0, S_HEADER = 1, S_PASS = 2, S_FAIL = 3, S_TITLE = 4;
            var sbS = new StringBuilder();
            sbS.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sbS.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sbS.Append("<sheetData>");

            string CL(int c) { string r = ""; while (c > 0) { r = (char)('A' + (c - 1) % 26) + r; c = (c - 1) / 26; } return r; }
            void SC(StringBuilder sb, int row, int col, string val, int sty = S_NORMAL)
                => sb.Append($"<c r=\"{CL(col)}{row}\" t=\"s\" s=\"{sty}\"><v>{SI(val)}</v></c>");
            void NC(StringBuilder sb, int row, int col, double val, int sty = S_NORMAL)
                => sb.Append($"<c r=\"{CL(col)}{row}\" s=\"{sty}\"><v>{val.ToString(CultureInfo.InvariantCulture)}</v></c>");
            void Row(int ri, Action<StringBuilder> cells) { sbS.Append($"<row r=\"{ri}\">"); cells(sbS); sbS.Append("</row>"); }

            Row(1, sb => SC(sb, 1, 1, "LTE B26 传导功率测试报告 (Conducted Power)", S_TITLE));
            Row(2, sb => SC(sb, 2, 1, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            Row(3, sb => SC(sb, 3, 1, $"Band 26 (OB26) | PMAX {PMax} dBm | Limit ≤ {LimitHi} dBm"));

            string[] hdrs = { "信道", "带宽", "频率(MHz)", "UL EARFCN", "调制方式", "RB", "Offset", "实测(dBm)", "Limit", "Status", "Message" };
            Row(5, sb => { for (int c = 0; c < hdrs.Length; c++) SC(sb, 5, c + 1, hdrs[c], S_HEADER); });

            int dr = 6;
            foreach (var r in _rows.Where(r => r.StatusText is "PASS" or "FAIL" or "ERROR" or "ABORT"))
            {
                int fr = dr;
                int st = r.StatusText == "PASS" ? S_PASS : r.StatusText == "FAIL" ? S_FAIL : S_NORMAL;
                Row(dr, sb =>
                {
                    SC(sb, fr, 1, r.ChannelType);    SC(sb, fr, 2, r.BwLabel);
                    SC(sb, fr, 3, r.FreqStr);        NC(sb, fr, 4, r.Earfcn);
                    SC(sb, fr, 5, r.Modulation);     NC(sb, fr, 6, r.RbSize);
                    NC(sb, fr, 7, r.RbOffset);       SC(sb, fr, 8, r.MeasuredPowerStr);
                    SC(sb, fr, 9, r.LimitStr);       SC(sb, fr, 10, r.StatusText, st);
                    SC(sb, fr, 11, r.Note);
                });
                dr++;
            }
            int p = _rows.Count(r => r.StatusText == "PASS");
            int f = _rows.Count(r => r.StatusText == "FAIL");
            int sr = dr + 1;
            Row(sr, sb => SC(sb, sr, 1, $"Summary: PASS={p}  FAIL={f}", S_TITLE));
            sbS.Append("</sheetData></worksheet>");

            var sbSS = new StringBuilder();
            sbSS.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sbSS.Append($"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{ss.Count}\" uniqueCount=\"{ss.Count}\">");
            foreach (var sv in ss) { sbSS.Append("<si><t xml:space=\"preserve\">"); sbSS.Append(System.Security.SecurityElement.Escape(sv)); sbSS.Append("</t></si>"); }
            sbSS.Append("</sst>");

            const string STYLES = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <fonts count=""5"">
    <font><sz val=""11""/><name val=""Calibri""/></font>
    <font><b/><sz val=""11""/><name val=""Calibri""/></font>
    <font><b/><sz val=""11""/><color rgb=""FFFFFFFF""/><name val=""Calibri""/></font>
    <font><b/><sz val=""11""/><color rgb=""FF00AA00""/><name val=""Calibri""/></font>
    <font><b/><sz val=""11""/><color rgb=""FFDD0000""/><name val=""Calibri""/></font>
  </fonts>
  <fills count=""3"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF1A1A2E""/></patternFill></fill>
  </fills>
  <borders count=""1""><border><left/><right/><top/><bottom/><diagonal/></border></borders>
  <cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
  <cellXfs count=""5"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
    <xf numFmtId=""0"" fontId=""2"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>
    <xf numFmtId=""0"" fontId=""3"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""4"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
  </cellXfs>
</styleSheet>";

            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            void AE(string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var sw = new StreamWriter(e.Open(), new UTF8Encoding(false));
                sw.Write(content);
            }
            AE("[Content_Types].xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml""  ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/sharedStrings.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml""/>
  <Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");
            AE("_rels/.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");
            AE("xl/workbook.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets><sheet name=""B26 TxPower"" sheetId=""1"" r:id=""rId1""/></sheets>
</workbook>");
            AE("xl/_rels/workbook.xml.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"" Target=""sharedStrings.xml""/>
  <Relationship Id=""rId3"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");
            AE("xl/worksheets/sheet1.xml", sbS.ToString());
            AE("xl/sharedStrings.xml",     sbSS.ToString());
            AE("xl/styles.xml",            STYLES);
        }
    }

    // Simple WPF input dialog
    internal class SimpleInputDialog : Window
    {
        private readonly TextBox _tb;
        public string Result { get; private set; } = "";

        public SimpleInputDialog(string prompt, string title, string defaultValue)
        {
            Title = title; Width = 360; Height = 140;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
            _tb = new TextBox { Text = defaultValue, Height = 26 };
            sp.Children.Add(_tb);
            var bp = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", Width = 72, Height = 26, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            ok.Click += (_, _) => { Result = _tb.Text.Trim(); DialogResult = true; };
            var cancel = new Button { Content = "Cancel", Width = 72, Height = 26, IsCancel = true };
            bp.Children.Add(ok); bp.Children.Add(cancel);
            sp.Children.Add(bp);
            Content = sp;
        }
    }
}
