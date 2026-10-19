using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace RFTestTool
{
    /// <summary>
    /// Full-screen loss table manager:
    ///   Left  — list of saved tables (select / copy / delete)
    ///   Right — edit rows (Freq / Input / Output), add / delete rows
    /// Saves tables as *.loss.json in the configured folder.
    /// </summary>
    public class LossEditorWindow : Window
    {
        // ── Storage ───────────────────────────────────────────
        private readonly string _folder;
        private readonly ObservableCollection<LossTable> _tables = new();
        private LossTable? _current;

        // ── Controls ──────────────────────────────────────────
        private readonly ListBox       _tableList = new();
        private readonly DataGrid      _grid      = new();
        private readonly TextBox       _nameBox   = new();
        private readonly TextBlock     _infoLabel = new();

        // ── Entry point ───────────────────────────────────────
        public static void Show(Window owner, string folder)
        {
            new LossEditorWindow(folder) { Owner = owner }.ShowDialog();
        }

        // ── Constructor ───────────────────────────────────────
        public LossEditorWindow(string folder)
        {
            _folder = folder;
            Title   = "Cable Loss Table Editor";
            Width   = 820; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));

            Content = BuildUI();
            Loaded += (_, _) => RefreshTableList();
        }

        // ── UI ────────────────────────────────────────────────
        private FrameworkElement BuildUI()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── LEFT: table list ──────────────────────────────
            var left = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(left, 0);

            var listTitle = new TextBlock
            {
                Text = "Current Loss files:",
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF)),
                FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 12, 12, 8)
            };
            Grid.SetRow(listTitle, 0);
            left.Children.Add(listTitle);

            _tableList.Background = Brushes.Transparent;
            _tableList.BorderThickness = new Thickness(0);
            _tableList.ItemsSource = _tables;
            _tableList.SelectionChanged += TableList_SelectionChanged;
            _tableList.ItemTemplate = MakeTableItemTemplate();
            var scroll = new ScrollViewer { Content = _tableList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            left.Children.Add(scroll);

            // Bottom: add table button
            var addBtn = MakeBtn("＋  New Table", "#1A3A1A", "#30D158");
            addBtn.Margin = new Thickness(10, 6, 10, 10);
            addBtn.Click += AddTable_Click;
            Grid.SetRow(addBtn, 2);
            left.Children.Add(addBtn);

            root.Children.Add(left);

            // Divider
            var div = new Border { Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)) };
            Grid.SetColumn(div, 1);
            root.Children.Add(div);

            // ── RIGHT: editor ─────────────────────────────────
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // name row
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // info
            Grid.SetColumn(right, 2);

            var editTitle = new TextBlock
            {
                Text = "Loss Edit Area",
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF)),
                FontSize = 14, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 8)
            };
            Grid.SetRow(editTitle, 0);
            right.Children.Add(editTitle);

            // Name row
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 8) };
            nameRow.Children.Add(new TextBlock
            {
                Text = "Table Name:",
                Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D)),
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0)
            });
            _nameBox.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            _nameBox.Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xF0));
            _nameBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            _nameBox.BorderThickness = new Thickness(1);
            _nameBox.FontSize = 12; _nameBox.FontFamily = new FontFamily("Segoe UI");
            _nameBox.Padding = new Thickness(8, 4, 8, 4);
            _nameBox.Width = 200;
            _nameBox.TextChanged += (_, _) => { if (_current != null) _current.Name = _nameBox.Text; };
            nameRow.Children.Add(_nameBox);
            Grid.SetRow(nameRow, 1);
            right.Children.Add(nameRow);

            // DataGrid
            BuildDataGrid();
            var gridScroll = new Border
            {
                Margin = new Thickness(16, 0, 16, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                BorderThickness = new Thickness(1),
                Child = _grid
            };
            Grid.SetRow(gridScroll, 2);
            right.Children.Add(gridScroll);

            // Action buttons
            var btnRow = new WrapPanel { Margin = new Thickness(16, 8, 16, 6), Orientation = Orientation.Horizontal };
            var addRowBtn = MakeBtn("＋ Add Row",  "#1A2A1A", "#30D158");
            addRowBtn.Margin = new Thickness(0, 0, 8, 4);
            addRowBtn.Click += (_, _) => { _current?.Rows.Add(new LossRow()); };

            var delRowBtn = MakeBtn("－ Delete Row", "#2A1A1A", "#FF453A");
            delRowBtn.Margin = new Thickness(0, 0, 8, 4);
            delRowBtn.Click += (_, _) =>
            {
                if (_current == null) return;
                if (_grid.SelectedItem is LossRow row) _current.Rows.Remove(row);
                else if (_current.Rows.Count > 0) _current.Rows.RemoveAt(_current.Rows.Count - 1);
            };

            var saveBtn = MakeBtn("💾 Save", "#0A1F35", "#0A84FF");
            saveBtn.Margin = new Thickness(0, 0, 8, 4);
            saveBtn.Click += SaveTable_Click;

            var copyBtn = MakeBtn("⎘ Copy", "#1A1A2A", "#5E5CE6");
            copyBtn.Margin = new Thickness(0, 0, 8, 4);
            copyBtn.Click += CopyTable_Click;

            btnRow.Children.Add(addRowBtn);
            btnRow.Children.Add(delRowBtn);
            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(copyBtn);
            Grid.SetRow(btnRow, 3);
            right.Children.Add(btnRow);

            _infoLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));
            _infoLabel.FontSize = 10; _infoLabel.FontFamily = new FontFamily("Segoe UI");
            _infoLabel.Margin = new Thickness(16, 0, 16, 8);
            Grid.SetRow(_infoLabel, 4);
            right.Children.Add(_infoLabel);

            root.Children.Add(right);
            return root;
        }

        private void BuildDataGrid()
        {
            _grid.Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));
            _grid.Foreground      = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9));
            _grid.BorderThickness = new Thickness(0);
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            _grid.RowBackground           = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));
            _grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18));
            _grid.AutoGenerateColumns  = false;
            _grid.CanUserAddRows       = false;
            _grid.CanUserDeleteRows    = false;
            _grid.SelectionMode        = DataGridSelectionMode.Single;
            _grid.ColumnHeaderHeight   = 36;

            // Style column headers
            var headerStyle = new Style();
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9))));
            headerStyle.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
            headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E))));
            _grid.ColumnHeaderStyle = headerStyle;

            _grid.Columns.Add(MakeCol("Freq.(MHz)", nameof(LossRow.Freq),  120));
            _grid.Columns.Add(MakeCol("Input(dB)",  nameof(LossRow.Input), 120));
            _grid.Columns.Add(MakeCol("Output(dB)", nameof(LossRow.Output),120));
        }

        private static DataGridTextColumn MakeCol(string header, string binding, double width)
        {
            var col = new DataGridTextColumn
            {
                Header  = header,
                Binding = new Binding(binding) { StringFormat = "0.##" },
                Width   = new DataGridLength(width),
            };
            col.CellStyle           = MakeCellStyle();
            col.EditingElementStyle = MakeEditStyle();
            return col;
        }



        private static Style MakeCellStyle()
        {
            var s = new Style(typeof(DataGridCell));
            s.Setters.Add(new Setter(Control.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9))));
            s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Consolas")));
            return s;
        }

        private static Style MakeEditStyle()
        {
            var s = new Style(typeof(TextBox));
            s.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x0A, 0x1F, 0x38))));
            s.Setters.Add(new Setter(Control.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xF0))));
            s.Setters.Add(new Setter(Control.BorderBrushProperty,
                new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Consolas")));
            return s;
        }

        private static DataTemplate MakeTableItemTemplate()
        {
            var dt = new DataTemplate(typeof(LossTable));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.MarginProperty, new Thickness(6, 3, 6, 3));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 6, 6, 6));
            border.SetValue(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0x2C, 0x1A, 0x4A))); // purple tint like image

            var grid = new FrameworkElementFactory(typeof(Grid));
            var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            grid.AppendChild(col1);
            grid.AppendChild(col2);

            var nameTb = new FrameworkElementFactory(typeof(TextBlock));
            nameTb.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            nameTb.SetValue(TextBlock.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xF0)));
            nameTb.SetValue(TextBlock.FontSizeProperty, 12.0);
            nameTb.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI"));
            nameTb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            nameTb.SetValue(Grid.ColumnProperty, 0);
            grid.AppendChild(nameTb);

            border.AppendChild(grid);
            dt.VisualTree = border;
            return dt;
        }

        // ── Events ────────────────────────────────────────────
        private void TableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_tableList.SelectedItem is not LossTable t) return;
            SelectTable(t);
        }

        private void SelectTable(LossTable t)
        {
            _current = t;
            _nameBox.Text = t.Name;
            _grid.ItemsSource = t.Rows;
            _infoLabel.Text = $"Saved: {_folder}";
        }

        private void AddTable_Click(object sender, RoutedEventArgs e)
        {
            var t = new LossTable { Name = "New Table" };
            t.Rows.Add(new LossRow { Freq = 0, Input = 0, Output = 0 });
            _tables.Add(t);
            _tableList.SelectedItem = t;
            _nameBox.Focus();
            _nameBox.SelectAll();
        }

        private void SaveTable_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            try
            {
                _current.SaveToFile(_folder);
                _infoLabel.Text = $"Saved  ·  {Path.Combine(_folder, _current.Name + ".loss.json")}";
                _infoLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            }
            catch (Exception ex)
            {
                _infoLabel.Text = "Save failed: " + ex.Message;
                _infoLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            }
        }

        private void CopyTable_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            string newName = _current.Name + " Copy";
            var copy = _current.Clone(newName);
            _tables.Add(copy);
            _tableList.SelectedItem = copy;
        }

        private void RefreshTableList()
        {
            _tables.Clear();
            foreach (var t in LossTable.LoadAll(_folder))
                _tables.Add(t);

            if (_tables.Count == 0)
            {
                // Add a default starter table
                var def = new LossTable { Name = "Default" };
                def.Rows.Add(new LossRow { Freq =  700, Input = 8.0, Output = 8.0 });
                def.Rows.Add(new LossRow { Freq = 1800, Input = 8.0, Output = 8.0 });
                def.Rows.Add(new LossRow { Freq = 2600, Input = 8.0, Output = 8.0 });
                _tables.Add(def);
            }
            _tableList.SelectedIndex = 0;
        }

        private static Button MakeBtn(string text, string bg, string fg)
        {
            return new Button
            {
                Content    = text,
                Height     = 30,
                Padding    = new Thickness(12, 0, 12, 0),
                FontSize   = 11,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(bg)),
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(fg)),
                BorderThickness = new Thickness(0),
                Cursor     = Cursors.Hand
            };
        }
    }
}
