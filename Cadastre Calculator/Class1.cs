#pragma warning disable CA1416, CS8618, CS8600, CS8601, CS8602, CS8612, CS8625

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Data;
using System.Xml.Serialization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Win32;
using System.Data;

// AutoCAD Namespaces
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

[assembly: CommandClass(typeof(CadastreTools.LKeyinCommand))]

namespace CadastreTools
{
    // --- 1. GLOBAL CONSTANTS ---
    public static class CadConstants
    {
        public const string LAY_TXT_BRG = "BEARING";
        public const string LAY_TXT_DIST = "DISTANCE";
        public const string LAY_TXT_SYMB = "SYMB TEXT";
        public const string LAY_TXT_PTNUM = "POINT_NUMBERS";
        public const string VAR_PT_COUNTER = "CADASTRE_PT_NUM";
        public const string DICT_NOD_NAME = "CADASTRE_PRO_DATA";
        public const string DICT_KEY_TRAVERSES = "TRAVERSE_DATA_XML";
    }

    // --- 2. DATA TRANSFER OBJECTS (FOR SAVING TO DWG) ---
    [Serializable]
    public class DtoSegment
    {
        public string TraverseId;
        public int FromPoint;
        public int ToPoint;
        public int PointNumber;
        public int ParentStationId;
        public double RawAzimuth;
        public double Distance;
        public string Comment;
        public bool IsRadiation;
        public string HandleLine; // Store Hex Handle instead of ObjectId
        public string HandleTxtBrg;
        public string HandleTxtDist;
        public string HandleTxtPt;
        public string HandleTxtComm;
        // Geometry saved to verify integrity on load
        public double StartX, StartY, StartZ;
        public double EndX, EndY, EndZ;
    }

    [Serializable]
    public class DtoChain
    {
        public int ChainIndex;
        public string Id;
        public double OriginX, OriginY, OriginZ;
        public List<DtoSegment> Segments = new List<DtoSegment>();
        public List<DtoSegment> Radiations = new List<DtoSegment>();
    }

    [Serializable]
    public class DtoSaveData
    {
        public List<DtoChain> Chains = new List<DtoChain>();
    }

    // --- 3. DATA MODELS (RUNTIME) ---
    public class TraverseSegment : INotifyPropertyChanged
    {
        public string TraverseId { get; set; }
        public int ParentStationId { get; set; }
        public int FromPoint { get; set; }
        public int ToPoint { get; set; }
        public int PointNumber { get; set; }
        public double RawAzimuth { get; set; }
        public double Distance { get; set; }
        public string Comment { get; set; }

        public Point3d StartPoint { get; set; }
        public Point3d EndPoint { get; set; }
        public bool IsRadiation { get; set; } = false;

        // Runtime ObjectIds
        public ObjectId LineId { get; set; }
        public ObjectId TextBrgId { get; set; }
        public ObjectId TextDistId { get; set; }
        public ObjectId TextPtId { get; set; }
        public ObjectId TextCommId { get; set; }

        public string DisplayAzimuth => CadMath.DegreesToDmsFormatted(CadMath.ParseDmsToDegrees(RawAzimuth));
        public string DisplayDist => $"{Distance:0.000}m";
        public string DisplayLine => IsRadiation ? $"RAD {ParentStationId}->{PointNumber}" : $"{FromPoint} -> {PointNumber}";

        public event PropertyChangedEventHandler PropertyChanged;
        public void NotifyUpdate()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayAzimuth"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayDist"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("DisplayLine"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Comment"));
        }
    }

    public class TraverseChain
    {
        public int ChainIndex { get; set; }
        public string Id => $"TRAV {ChainIndex}";
        public Point3d OriginPoint { get; set; }
        public List<TraverseSegment> Segments { get; set; } = new List<TraverseSegment>();
        public List<TraverseSegment> Radiations { get; set; } = new List<TraverseSegment>();
        public bool IsVisible { get; set; } = true;
    }

    // --- 4. PERSISTENCE ENGINE (UPDATED TO USE XML FILE) ---
    public static class PersistenceManager
    {
        // SAVE: Writes the complex object structure to a file
        public static void SaveToDwg(List<TraverseChain> traverses)
        {
            try
            {
                // 1. Get current drawing path
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                string dwgPath = doc.Name;

                // Safety: Don't save if drawing hasn't been saved to disk yet
                if (string.IsNullOrEmpty(dwgPath) || !dwgPath.Contains("\\")) return;

                string xmlFile = dwgPath + ".database.xml";

                // 2. Convert Runtime Objects (TraverseChain) to Saveable Data (Dto)
                DtoSaveData data = new DtoSaveData();
                foreach (var t in traverses)
                {
                    DtoChain dt = new DtoChain() { ChainIndex = t.ChainIndex, Id = t.Id, OriginX = t.OriginPoint.X, OriginY = t.OriginPoint.Y, OriginZ = t.OriginPoint.Z };
                    foreach (var s in t.Segments) dt.Segments.Add(ConvertSeg(s));
                    foreach (var r in t.Radiations) dt.Radiations.Add(ConvertSeg(r));
                    data.Chains.Add(dt);
                }

                // 3. Write to XML File
                XmlSerializer xs = new XmlSerializer(typeof(DtoSaveData));
                using (StreamWriter wr = new StreamWriter(xmlFile))
                {
                    xs.Serialize(wr, data);
                }
            }
            catch (System.Exception ex)
            {
                // Optional: AcApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nSave Error: " + ex.Message);
            }
        }

        // LOAD: Reads the file and reconstructs the objects for the UI
        public static List<TraverseChain> LoadFromDwg()
        {
            List<TraverseChain> list = new List<TraverseChain>();
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                string dwgPath = doc.Name;
                if (string.IsNullOrEmpty(dwgPath) || !File.Exists(dwgPath + ".database.xml")) return list;

                string xmlFile = dwgPath + ".database.xml";
                DtoSaveData data;

                // 1. Read from XML File
                XmlSerializer xs = new XmlSerializer(typeof(DtoSaveData));
                using (StreamReader rd = new StreamReader(xmlFile))
                {
                    data = (DtoSaveData)xs.Deserialize(rd);
                }

                // 2. Convert Saveable Data (Dto) back to Runtime Objects (TraverseChain)
                Database db = doc.Database;
                foreach (var dc in data.Chains)
                {
                    TraverseChain tc = new TraverseChain() { ChainIndex = dc.ChainIndex, OriginPoint = new Point3d(dc.OriginX, dc.OriginY, dc.OriginZ) };
                    foreach (var ds in dc.Segments) tc.Segments.Add(RebuildSeg(ds, db));
                    foreach (var dr in dc.Radiations) tc.Radiations.Add(RebuildSeg(dr, db));
                    list.Add(tc);
                }
            }
            catch { }
            return list;
        }

        // --- HELPER CONVERTERS (Keep these exactly as they were) ---
        private static DtoSegment ConvertSeg(TraverseSegment s)
        {
            return new DtoSegment()
            {
                TraverseId = s.TraverseId,
                FromPoint = s.FromPoint,
                ToPoint = s.ToPoint,
                PointNumber = s.PointNumber,
                ParentStationId = s.ParentStationId,
                RawAzimuth = s.RawAzimuth,
                Distance = s.Distance,
                Comment = s.Comment,
                IsRadiation = s.IsRadiation,
                StartX = s.StartPoint.X,
                StartY = s.StartPoint.Y,
                StartZ = s.StartPoint.Z,
                EndX = s.EndPoint.X,
                EndY = s.EndPoint.Y,
                EndZ = s.EndPoint.Z,
                HandleLine = s.LineId.IsValid ? s.LineId.Handle.ToString() : "",
                HandleTxtBrg = s.TextBrgId.IsValid ? s.TextBrgId.Handle.ToString() : "",
                HandleTxtDist = s.TextDistId.IsValid ? s.TextDistId.Handle.ToString() : "",
                HandleTxtPt = s.TextPtId.IsValid ? s.TextPtId.Handle.ToString() : "",
                HandleTxtComm = s.TextCommId.IsValid ? s.TextCommId.Handle.ToString() : ""
            };
        }

        private static TraverseSegment RebuildSeg(DtoSegment d, Database db)
        {
            ObjectId Resolve(string h)
            {
                if (string.IsNullOrEmpty(h)) return ObjectId.Null;
                try { return db.GetObjectId(false, new Handle(Convert.ToInt64(h, 16)), 0); } catch { return ObjectId.Null; }
            }
            return new TraverseSegment()
            {
                TraverseId = d.TraverseId,
                FromPoint = d.FromPoint,
                ToPoint = d.ToPoint,
                PointNumber = d.PointNumber,
                ParentStationId = d.ParentStationId,
                RawAzimuth = d.RawAzimuth,
                Distance = d.Distance,
                Comment = d.Comment,
                IsRadiation = d.IsRadiation,
                StartPoint = new Point3d(d.StartX, d.StartY, d.StartZ),
                EndPoint = new Point3d(d.EndX, d.EndY, d.EndZ),
                LineId = Resolve(d.HandleLine),
                TextBrgId = Resolve(d.HandleTxtBrg),
                TextDistId = Resolve(d.HandleTxtDist),
                TextPtId = Resolve(d.HandleTxtPt),
                TextCommId = Resolve(d.HandleTxtComm)
            };
        }
    }
    // --- 5. SETTINGS ---
    public class TextSettings
    {
        public string Style { get; set; } = "Standard";
        public double Size { get; set; } = 1.0;
        public bool IsMText { get; set; } = false;
        public bool Masking { get; set; } = false;
        public short ColorIndex { get; set; } = 256;
        public bool Visible { get; set; } = true;
        public void Reset(short def) { Style = "Standard"; Size = 1.0; IsMText = false; Masking = false; ColorIndex = def; Visible = true; }
    }

    public class AppSettings
    {
        public string LayQ { get; set; } = "NOT SET";
        public string LayW { get; set; } = "BOUNDARY_SUBJECT";
        public string LayE { get; set; } = "NOT SET";
        public string LayA { get; set; } = "BOUNDARY_ADJOINING";
        public string LayS { get; set; } = "CONNECTIONS";
        public string LayD { get; set; } = "BDY_EASEMENT";
        public bool AudioFeedback { get; set; } = true;
        public string AudioSound { get; set; } = "Asterisk";
        public double SnapTolerance { get; set; } = 0.005;
        public TextSettings TextBrg { get; set; } = new TextSettings();
        public TextSettings TextDist { get; set; } = new TextSettings();
        public TextSettings TextPt { get; set; } = new TextSettings() { ColorIndex = 1 };
        public TextSettings TextComm { get; set; } = new TextSettings() { ColorIndex = 7 };

        public void ResetLayers() { LayQ = "NOT SET"; LayW = "BOUNDARY_SUBJECT"; LayE = "NOT SET"; LayA = "BOUNDARY_ADJOINING"; LayS = "CONNECTIONS"; LayD = "BDY_EASEMENT"; }
        public void ResetText() { TextBrg.Reset(256); TextDist.Reset(256); TextPt.Reset(1); TextComm.Reset(7); }

        public static void Save(AppSettings settings)
        {
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadastreTools");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder, "CadastreKeyinSettings.xml");
                XmlSerializer xs = new XmlSerializer(typeof(AppSettings));
                using (StreamWriter wr = new StreamWriter(path)) { xs.Serialize(wr, settings); }
            }
            catch { }
        }
        public static AppSettings Load()
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadastreTools", "CadastreKeyinSettings.xml");
                if (File.Exists(path))
                {
                    XmlSerializer xs = new XmlSerializer(typeof(AppSettings));
                    using (StreamReader rd = new StreamReader(path)) { return (AppSettings)xs.Deserialize(rd) ?? new AppSettings(); }
                }
            }
            catch { }
            return new AppSettings();
        }
    }

    // --- 6. MATH UTILS ---
    public static class CadMath
    {
        public static double ParseDmsToDegrees(double rawInput)
        {
            int degrees = (int)rawInput; double decimals = rawInput - degrees;
            string decimalStr = decimals.ToString("F6").Substring(2).PadRight(6, '0');
            double minutes = double.Parse(decimalStr.Substring(0, 2));
            double seconds = double.Parse(decimalStr.Substring(2, 2) + "." + decimalStr.Substring(4));
            return degrees + (minutes / 60.0) + (seconds / 3600.0);
        }
        public static string DegreesToDmsFormatted(double decimalDegrees)
        {
            int d = (int)decimalDegrees; double remainder = (decimalDegrees - d) * 60.0;
            int m = (int)remainder; double s = (remainder - m) * 60.0;
            return $"{d}\u00B0{m:00}'{Math.Round(s):00}\"";
        }
        public static string DmsToString(double dmsValue) { return dmsValue.ToString("0.0000"); }
        public static string DegreesToDmsString(double decimalDegrees)
        {
            int d = (int)decimalDegrees; double remainder = (decimalDegrees - d) * 60.0;
            int m = (int)remainder; double s = (remainder - m) * 60.0;
            string sStr = s.ToString("00.00").Replace(".", ""); return $"{d}.{m:00}{sStr}";
        }
        public static double AddSubDms(double dms1, double dms2, bool add)
        {
            double deg1 = ParseDmsToDegrees(dms1); double deg2 = ParseDmsToDegrees(dms2);
            double res = add ? deg1 + deg2 : deg1 - deg2;
            res = res % 360; if (res < 0) res += 360;
            int d = (int)res; double rem = (res - d) * 60.0; int m = (int)rem; double s = (rem - m) * 60.0;
            string sStr = s.ToString("00.00").Replace(".", ""); return double.Parse($"{d}.{m:00}{sStr}");
        }
        public static bool TryParseAzimuth(string input, out double result)
        {
            result = 0; if (string.IsNullOrWhiteSpace(input)) return false;
            input = input.Replace("-", ".").Replace(" ", ".");
            string[] parts = input.Split('.');
            if (parts.Length == 3) return double.TryParse(parts[0] + "." + parts[1] + parts[2], out result);
            return double.TryParse(input, out result);
        }
        public static string EvaluateMath(string expression)
        {
            try
            {
                System.Data.DataTable dt = new System.Data.DataTable();
                var v = dt.Compute(expression, "");
                return Convert.ToDouble(v).ToString("0.####");
            }
            catch { return expression; }
        }
    }

    // --- 7. UI THEME ---
    public static class UITheme
    {
        public static Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        public static Brush CardBrush = new SolidColorBrush(Color.FromRgb(45, 45, 48));
        public static Brush InputBackground = new SolidColorBrush(Color.FromRgb(20, 20, 20));
        public static Brush AccentColor = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        public static Brush GuideColor = new SolidColorBrush(Color.FromRgb(0, 200, 0));
        public static Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0, 150, 136));
        public static Brush DimBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80));

        public static Border CreateCard()
        {
            return new Border() { Background = CardBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Effect = new DropShadowEffect() { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3 } };
        }
        public static TextBox CreateInputBox()
        {
            return new TextBox()
            {
                Background = InputBackground,
                Foreground = Brushes.Cyan,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Height = 40,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Padding = new Thickness(8, 0, 8, 0),
                CaretBrush = Brushes.White
            };
        }
        public static ComboBox CreateLayerCombo() { return new ComboBox() { Height = 30, Margin = new Thickness(2), IsEditable = true, Foreground = Brushes.Black, FontSize = 12 }; }
        public static Label CreateLabel(string text) { return new Label() { Content = text, Foreground = Brushes.LightGray, FontSize = 11, FontWeight = FontWeights.Bold, Padding = new Thickness(0, 5, 0, 2) }; }
        public static TextBlock CreateFooterText(string text, Brush color)
        {
            return new TextBlock() { Text = text, Foreground = color, FontSize = 11, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2), TextAlignment = System.Windows.TextAlignment.Center };
        }
        public static Button CreateLayerBtn(string key)
        {
            return new Button()
            {
                Content = new TextBlock() { Text = key, FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = System.Windows.TextAlignment.Center },
                Height = 60,
                Margin = new Thickness(3),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Foreground = Brushes.White,
                Background = DimBrush
            };
        }
        public static CheckBox CreateToggle(string text) { return new CheckBox() { Content = text, Foreground = Brushes.White, Margin = new Thickness(5), FontSize = 14 }; }
        public static Button CreateActionBtn(string text, Brush bg)
        {
            return new Button()
            {
                Content = text,
                Height = 45,
                Background = bg,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 5, 0, 5),
                BorderThickness = new Thickness(0)
            };
        }
        public static Button CreateColorBtn(short colorIndex)
        {
            Button b = new Button() { Width = 40, Height = 25, Margin = new Thickness(2) };
            if (colorIndex == 256) b.Content = "ByL"; else if (colorIndex == 0) b.Content = "ByB"; else b.Background = new SolidColorBrush(GetWpfColor(colorIndex));
            return b;
        }
        public static Color GetWpfColor(short index) { try { var acCol = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index); return Color.FromRgb(acCol.ColorValue.R, acCol.ColorValue.G, acCol.ColorValue.B); } catch { return Colors.Gray; } }
    }

    // --- 8. COMMAND CLASS ---
    public class LKeyinCommand
    {
        static Autodesk.AutoCAD.Windows.PaletteSet _ps = null;
        static CadastreControl _view = null;
        [CommandMethod("LKW", CommandFlags.Session)]
        public void RunLKeyin()
        {
            try
            {
                if (_ps == null)
                {
                    _ps = new Autodesk.AutoCAD.Windows.PaletteSet("Cadastre Pro");
                    _ps.Size = new System.Drawing.Size(500, 900); // User requested larger default
                    _ps.DockEnabled = (DockSides)((int)DockSides.Left | (int)DockSides.Right);
                    _view = new CadastreControl();
                    _ps.AddVisual("Input", _view);
                    // Guid ensures docking/size is remembered by AutoCAD
                    _ps.TitleBarLocation = PaletteSetTitleBarLocation.Left;
                }
                _ps.KeepFocus = true;
                _ps.Visible = true;
            }
            catch (System.Exception ex) { MessageBox.Show("Startup Error: " + ex.Message); }
        }
    }

    // --- 9. MAIN UI CONTROL ---
    public class CadastreControl : UserControl
    {
        private Point3d _currentPoint;
        private int _lastPtNum = 0;
        private TraverseChain _currentTraverse;
        private List<TraverseChain> _allTraverses = new List<TraverseChain>();
        private string _currentLayer = "BOUNDARY_SUBJECT";
        private AppSettings _config;
        private ObservableCollection<object> _logItems = new ObservableCollection<object>();
        private ICollectionView _logView; // For filtering

        private Stack<List<ObjectId>> _undoStack = new Stack<List<ObjectId>>();
        private List<Point3d> _traversePath = new List<Point3d>();

        // Saved inputs for persistence
        private string _savedRadAz = "";
        private string _savedRadDist = "";
        private string _savedRadComm = "";

        private TextBox txtAzimuth, txtDistance;
        private ListView lstHistory;
        private Label lblStatus;
        private TextBlock txtRunningClosure, txtAreaInfo, lblGuide;
        private Button btnQ, btnW, btnE, btnA, btnS, btnD;
        private Grid _overlayContainer;
        private StackPanel _traverseListPanel;

        private ComboBox cmbLayQ, cmbLayW, cmbLayE, cmbLayA, cmbLayS, cmbLayD, cmbSound;
        private CheckBox setChkAudio;
        private List<TextUiRow> _textUiRows = new List<TextUiRow>();
        private class TextUiRow { public ComboBox CmbStyle; public TextBox TxtSize; public Button BtnColor; public CheckBox ChkMText; public CheckBox ChkMask; public CheckBox ChkVisible; public TextSettings SettingsRef; public string AssociatedLayer; }

        public CadastreControl()
        {
            try
            {
                _config = AppSettings.Load();
                _currentLayer = _config.LayW;
                InitializeCustomUI();
                this.Loaded += OnControlLoaded;
                AcApp.DocumentManager.DocumentActivated += (s, e) => ResetForNewDocument();
            }
            catch (System.Exception ex)
            {
                this.Content = new TextBlock() { Text = "UI Error: " + ex.ToString(), Foreground = Brushes.Red };
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AcApp.DocumentManager.MdiActiveDocument == null) return;
                PopulateComboBoxes();
                UpdateUIFromConfig();
                HighlightActiveLayer(btnW);
                // LOAD DATA FROM DWG
                ReloadDataFromDwg();
                if (txtAzimuth != null) txtAzimuth.Focus();
            }
            catch { }
        }

        private void ResetForNewDocument()
        {
            _allTraverses.Clear(); _logItems.Clear(); _undoStack.Clear(); _traversePath.Clear();
            _currentTraverse = null;
            lblStatus.Content = "Document Switched. Loading...";
            ReloadDataFromDwg();
            UpdateGuideText("PICK START POINT");
        }

        private void ReloadDataFromDwg()
        {
            var loaded = PersistenceManager.LoadFromDwg();
            _allTraverses.Clear(); _logItems.Clear();
            if (loaded.Count > 0)
            {
                _allTraverses.AddRange(loaded);
                foreach (var t in _allTraverses)
                {
                    // Populate Log in reverse order (newest top)
                    // Actually, let's just dump all segments
                    foreach (var s in t.Segments) _logItems.Add(s);
                    foreach (var r in t.Radiations) _logItems.Add(r);
                }
                // Sort Log by ID desc? No, let's keep it simple
                RefreshTraverseList();
                SyncCurrentState();
                lblStatus.Content = "Data Loaded from DWG.";
            }
        }

        private void SyncCurrentState()
        {
            if (_allTraverses.Count > 0)
            {
                _currentTraverse = _allTraverses.Last();
                if (_currentTraverse.Segments.Count > 0)
                {
                    var lastSeg = _currentTraverse.Segments.Last();
                    _currentPoint = lastSeg.EndPoint;
                    _lastPtNum = lastSeg.PointNumber;
                    _traversePath.Clear();
                    _traversePath.Add(_currentTraverse.OriginPoint);
                    foreach (var s in _currentTraverse.Segments) _traversePath.Add(s.EndPoint);
                }
                else
                {
                    _currentPoint = _currentTraverse.OriginPoint;
                    _traversePath.Clear(); _traversePath.Add(_currentPoint);
                }
                UpdateRunningMisclosure(); CalculateArea();
            }
            else
            {
                _currentTraverse = null;
                _traversePath.Clear();
            }
        }

        private void SaveState()
        {
            PersistenceManager.SaveToDwg(_allTraverses);
        }

        private void InitializeCustomUI()
        {
            this.Background = UITheme.BackgroundBrush;
            Grid rootGrid = new Grid();
            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });

            // HEADER
            Grid header = new Grid() { Background = UITheme.CardBrush, Height = 50 };
            header.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            TextBlock title = new TextBlock() { Text = " CADASTRE PRO", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 18, Margin = new Thickness(10, 0, 0, 0) };
            Button btnSettings = new Button() { Content = "⚙", Width = 40, Height = 40, Margin = new Thickness(5), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 20 };
            btnSettings.Click += (s, e) => ShowSettingsOverlay();
            Button btnAbout = new Button() { Content = "ℹ", Width = 40, Height = 40, Margin = new Thickness(5), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 20 };
            btnAbout.Click += (s, e) => MessageBox.Show("Cadastre Pro V3.5\nData Persistence Active");
            Grid.SetColumn(title, 0); Grid.SetColumn(btnSettings, 1); Grid.SetColumn(btnAbout, 2);
            header.Children.Add(title); header.Children.Add(btnSettings); header.Children.Add(btnAbout);
            Grid.SetRow(header, 0); mainGrid.Children.Add(header);

            // INPUTS
            StackPanel inputPnl = new StackPanel() { Margin = new Thickness(10) };
            lblGuide = new TextBlock() { Text = "READY", Foreground = UITheme.HighlightBrush, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 10) };
            inputPnl.Children.Add(lblGuide);
            Border cardData = UITheme.CreateCard(); StackPanel spData = new StackPanel();

            Grid gAz = new Grid(); gAz.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) }); gAz.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(50) });
            txtAzimuth = UITheme.CreateInputBox(); txtAzimuth.PreviewKeyDown += Input_PreviewKeyDown;
            Button btnCalcAz = new Button() { Content = "C", Height = 40, Margin = new Thickness(5, 0, 0, 0), Background = Brushes.DimGray, Foreground = Brushes.White };
            btnCalcAz.Click += (s, e) => OpenCalculator(txtAzimuth, true);
            Grid.SetColumn(txtAzimuth, 0); Grid.SetColumn(btnCalcAz, 1); gAz.Children.Add(txtAzimuth); gAz.Children.Add(btnCalcAz);

            Grid gDist = new Grid(); gDist.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) }); gDist.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(50) });
            txtDistance = UITheme.CreateInputBox(); txtDistance.PreviewKeyDown += Input_PreviewKeyDown;
            Button btnCalcDist = new Button() { Content = "C", Height = 40, Margin = new Thickness(5, 0, 0, 0), Background = Brushes.DimGray, Foreground = Brushes.White };
            btnCalcDist.Click += (s, e) => OpenCalculator(txtDistance, false);
            Grid.SetColumn(txtDistance, 0); Grid.SetColumn(btnCalcDist, 1); gDist.Children.Add(txtDistance); gDist.Children.Add(btnCalcDist);

            spData.Children.Add(UITheme.CreateLabel("AZIMUTH (DDD.MMSS)")); spData.Children.Add(gAz);
            spData.Children.Add(new Border() { Height = 10 });
            spData.Children.Add(UITheme.CreateLabel("DISTANCE (m)")); spData.Children.Add(gDist);
            cardData.Child = spData; inputPnl.Children.Add(cardData);
            Grid.SetRow(inputPnl, 1); mainGrid.Children.Add(inputPnl);

            // LIST VIEW (MODIFIED FOR COLUMNS + COMMENTS)
            lstHistory = new ListView() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.LightGray, FontSize = 12 };
            lstHistory.ItemsSource = _logItems;
            // FILTERING VIEW
            _logView = CollectionViewSource.GetDefaultView(_logItems);
            _logView.Filter = o => {
                if (o is TraverseSegment s)
                {
                    var chain = _allTraverses.FirstOrDefault(t => t.Id == s.TraverseId);
                    return chain != null && chain.IsVisible;
                }
                return true;
            };

            GridView grid = new GridView();
            grid.Columns.Add(new GridViewColumn() { Header = "LINE / PT", Width = 110, DisplayMemberBinding = new Binding("DisplayLine") });
            grid.Columns.Add(new GridViewColumn() { Header = "AZIMUTH", Width = 90, DisplayMemberBinding = new Binding("DisplayAzimuth") });
            grid.Columns.Add(new GridViewColumn() { Header = "DIST", Width = 70, DisplayMemberBinding = new Binding("DisplayDist") });
            grid.Columns.Add(new GridViewColumn() { Header = "COMMENT", Width = 100, DisplayMemberBinding = new Binding("Comment") });
            lstHistory.View = grid;
            lstHistory.MouseDoubleClick += LstHistory_MouseDoubleClick;
            lstHistory.SelectionChanged += LstHistory_SelectionChanged;
            Grid.SetRow(lstHistory, 2); mainGrid.Children.Add(lstHistory);

            // TRAVERSE TOGGLE
            Expander travExpander = new Expander() { Header = "TRAVERSE LIST (Hide/Show)", IsExpanded = false, Foreground = Brushes.Cyan, Margin = new Thickness(10, 5, 10, 0) };
            _traverseListPanel = new StackPanel() { Margin = new Thickness(5) };
            travExpander.Content = _traverseListPanel;
            Grid.SetRow(travExpander, 3); mainGrid.Children.Add(travExpander);

            // LAYERS (MODIFIED WITH EXPANDER)
            Expander layerExpander = new Expander() { Header = "LAYERS (Click to Hide/Show)", IsExpanded = true, Foreground = Brushes.Gray, Margin = new Thickness(10, 5, 10, 0) };
            StackPanel layPnl = new StackPanel() { Margin = new Thickness(0, 5, 0, 5) };
            Border cardLay = UITheme.CreateCard(); StackPanel spLay = new StackPanel();
            Grid gl = new Grid();
            gl.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); gl.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            for (int i = 0; i < 3; i++) gl.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            btnQ = UITheme.CreateLayerBtn("Q"); btnQ.Click += (s, e) => SetCurrentLayer(_config.LayQ, btnQ);
            btnW = UITheme.CreateLayerBtn("W"); btnW.Click += (s, e) => SetCurrentLayer(_config.LayW, btnW);
            btnE = UITheme.CreateLayerBtn("E"); btnE.Click += (s, e) => SetCurrentLayer(_config.LayE, btnE);
            btnA = UITheme.CreateLayerBtn("A"); btnA.Click += (s, e) => SetCurrentLayer(_config.LayA, btnA);
            btnS = UITheme.CreateLayerBtn("S"); btnS.Click += (s, e) => SetCurrentLayer(_config.LayS, btnS);
            btnD = UITheme.CreateLayerBtn("D"); btnD.Click += (s, e) => SetCurrentLayer(_config.LayD, btnD);
            Grid.SetRow(btnQ, 0); Grid.SetColumn(btnQ, 0); Grid.SetRow(btnW, 0); Grid.SetColumn(btnW, 1); Grid.SetRow(btnE, 0); Grid.SetColumn(btnE, 2);
            Grid.SetRow(btnA, 1); Grid.SetColumn(btnA, 0); Grid.SetRow(btnS, 1); Grid.SetColumn(btnS, 1); Grid.SetRow(btnD, 1); Grid.SetColumn(btnD, 2);
            gl.Children.Add(btnQ); gl.Children.Add(btnW); gl.Children.Add(btnE); gl.Children.Add(btnA); gl.Children.Add(btnS); gl.Children.Add(btnD);
            spLay.Children.Add(gl); cardLay.Child = spLay; layPnl.Children.Add(cardLay);
            layerExpander.Content = layPnl;
            Grid.SetRow(layerExpander, 4); mainGrid.Children.Add(layerExpander);

            // STATS & FOOTER
            Border closureBorder = new Border() { Background = new SolidColorBrush(Color.FromRgb(25, 25, 25)), Padding = new Thickness(8) };
            StackPanel spClose = new StackPanel();
            txtRunningClosure = new TextBlock() { Text = "Misclosure: N/A", Foreground = Brushes.Cyan, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
            txtAreaInfo = new TextBlock() { Text = "Area: 0 m²", Foreground = Brushes.Yellow, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
            spClose.Children.Add(txtRunningClosure); spClose.Children.Add(txtAreaInfo);
            closureBorder.Child = spClose; Grid.SetRow(closureBorder, 5); mainGrid.Children.Add(closureBorder);

            StackPanel fs = new StackPanel() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(5) };
            fs.Children.Add(UITheme.CreateFooterText("PGUP: Start | PGDN: Rad | INS: Note | DEL: Undo", Brushes.WhiteSmoke));
            fs.Children.Add(UITheme.CreateFooterText("ARROWS: Rot | QWE-ASD: Layers", Brushes.LightGray));
            Grid.SetRow(fs, 6); mainGrid.Children.Add(fs);

            // STATUS
            Border st = new Border() { Background = UITheme.AccentColor };
            lblStatus = new Label() { Content = "Ready", Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            st.Child = lblStatus; Grid.SetRow(st, 7); mainGrid.Children.Add(st);

            // OVERLAY
            _overlayContainer = new Grid() { Background = new SolidColorBrush(Color.FromArgb(245, 30, 30, 30)), Visibility = System.Windows.Visibility.Collapsed };
            rootGrid.Children.Add(mainGrid);
            rootGrid.Children.Add(_overlayContainer);
            this.Content = rootGrid;
            this.PreviewKeyDown += Control_PreviewKeyDown;
        }

        private void RefreshTraverseList()
        {
            _traverseListPanel.Children.Clear();
            foreach (var t in _allTraverses)
            {
                CheckBox chk = new CheckBox() { Content = t.Id, IsChecked = t.IsVisible, Foreground = Brushes.White, Margin = new Thickness(5) };
                chk.Click += (s, e) => {
                    t.IsVisible = (chk.IsChecked == true);
                    ToggleTraverseVisibility(t, t.IsVisible);
                    _logView.Refresh(); // REFRESH LOG
                };
                _traverseListPanel.Children.Add(chk);
            }
        }

        private void ToggleTraverseVisibility(TraverseChain chain, bool vis)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                void SetVis(ObjectId id) { if (id.IsValid && !id.IsErased) ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Visible = vis; }
                foreach (var seg in chain.Segments) { SetVis(seg.LineId); SetVis(seg.TextBrgId); SetVis(seg.TextDistId); SetVis(seg.TextPtId); SetVis(seg.TextCommId); }
                foreach (var rad in chain.Radiations) { SetVis(rad.LineId); SetVis(rad.TextBrgId); SetVis(rad.TextDistId); SetVis(rad.TextPtId); SetVis(rad.TextCommId); }
                tr.Commit(); doc.Editor.Regen();
            }
        }

        private void ShowOverlay(object content) { _overlayContainer.Children.Clear(); _overlayContainer.Children.Add((UIElement)content); _overlayContainer.Visibility = System.Windows.Visibility.Visible; }
        private void HideOverlay()
        {
            _overlayContainer.Visibility = System.Windows.Visibility.Collapsed;
            txtAzimuth.Focus();
        }

        private void ShowSettingsOverlay()
        {
            ScrollViewer scroll = new ScrollViewer() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel pnl = new StackPanel() { Margin = new Thickness(20), Background = UITheme.BackgroundBrush };
            pnl.Children.Add(new TextBlock() { Text = "SETTINGS", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 20) });

            // LAYERS
            Border cardL = UITheme.CreateCard(); StackPanel spL = new StackPanel(); spL.Children.Add(UITheme.CreateLabel("LAYER MAPPING"));
            Grid gl = new Grid(); gl.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); gl.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            for (int i = 0; i < 3; i++) gl.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            cmbLayQ = UITheme.CreateLayerCombo(); cmbLayW = UITheme.CreateLayerCombo(); cmbLayE = UITheme.CreateLayerCombo();
            cmbLayA = UITheme.CreateLayerCombo(); cmbLayS = UITheme.CreateLayerCombo(); cmbLayD = UITheme.CreateLayerCombo();
            void AddL(ComboBox cb, string lbl, int r, int c) { StackPanel sp = new StackPanel(); sp.Children.Add(new Label() { Content = lbl, Foreground = Brushes.Gray }); sp.Children.Add(cb); Grid.SetRow(sp, r); Grid.SetColumn(sp, c); gl.Children.Add(sp); }
            AddL(cmbLayQ, "Key Q", 0, 0); AddL(cmbLayW, "Key W", 0, 1); AddL(cmbLayE, "Key E", 0, 2); AddL(cmbLayA, "Key A", 1, 0); AddL(cmbLayS, "Key S", 1, 1); AddL(cmbLayD, "Key D", 1, 2);
            spL.Children.Add(gl);
            Button btnResetL = UITheme.CreateActionBtn("RESET LAYERS DEFAULT", Brushes.DimGray); btnResetL.Click += (s, e) => { _config.ResetLayers(); UpdateUIFromConfig(); }; spL.Children.Add(btnResetL);
            cardL.Child = spL; pnl.Children.Add(cardL);

            // TEXT CONFIG
            Border cardT = UITheme.CreateCard(); StackPanel spT = new StackPanel(); spT.Children.Add(UITheme.CreateLabel("TEXT CONFIG"));
            Grid gh = new Grid();
            gh.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(70) });
            gh.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(100) });
            gh.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(50) });
            gh.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(40) });
            gh.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            Label h1 = new Label() { Content = "TYPE", Foreground = Brushes.Gray, FontSize = 10 }; Grid.SetColumn(h1, 0); gh.Children.Add(h1);
            Label h2 = new Label() { Content = "STYLE", Foreground = Brushes.Gray, FontSize = 10 }; Grid.SetColumn(h2, 1); gh.Children.Add(h2);
            Label h3 = new Label() { Content = "SIZE", Foreground = Brushes.Gray, FontSize = 10 }; Grid.SetColumn(h3, 2); gh.Children.Add(h3);
            spT.Children.Add(gh);

            _textUiRows.Clear();
            spT.Children.Add(BuildTextRow("Bearing", _config.TextBrg, CadConstants.LAY_TXT_BRG));
            spT.Children.Add(BuildTextRow("Distance", _config.TextDist, CadConstants.LAY_TXT_DIST));
            spT.Children.Add(BuildTextRow("Point #", _config.TextPt, CadConstants.LAY_TXT_PTNUM));
            spT.Children.Add(BuildTextRow("Comment", _config.TextComm, CadConstants.LAY_TXT_SYMB));
            Button btnResetT = UITheme.CreateActionBtn("RESET TEXT DEFAULT", Brushes.DimGray); btnResetT.Click += (s, e) => { _config.ResetText(); UpdateUIFromConfig(); }; spT.Children.Add(btnResetT);
            cardT.Child = spT; pnl.Children.Add(cardT);

            Border cardO = UITheme.CreateCard(); StackPanel spO = new StackPanel(); spO.Children.Add(UITheme.CreateLabel("AUDIO"));
            setChkAudio = UITheme.CreateToggle("Enable Audio"); cmbSound = new ComboBox() { Height = 25, Margin = new Thickness(5), ItemsSource = new List<string> { "Beep", "Asterisk" } };
            spO.Children.Add(setChkAudio); spO.Children.Add(cmbSound); cardO.Child = spO; pnl.Children.Add(cardO);
            StackPanel acts = new StackPanel() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            Button btnSave = UITheme.CreateActionBtn("SAVE", UITheme.HighlightBrush); btnSave.Width = 120; btnSave.Margin = new Thickness(5);
            btnSave.Click += (s, e) => { SaveSettings(); ApplySettingsToExistingText(); HideOverlay(); };
            Button btnCancel = UITheme.CreateActionBtn("CANCEL", Brushes.DimGray); btnCancel.Width = 120; btnCancel.Margin = new Thickness(5);
            btnCancel.Click += (s, e) => HideOverlay();
            acts.Children.Add(btnCancel); acts.Children.Add(btnSave); pnl.Children.Add(acts);
            scroll.Content = pnl; PopulateComboBoxes(); UpdateUIFromConfig(); ShowOverlay(scroll);
        }

        private Grid BuildTextRow(string label, TextSettings ts, string layerName)
        {
            Grid g = new Grid(); g.Margin = new Thickness(0, 4, 0, 4);
            for (int i = 0; i < 7; i++) g.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            Label l = new Label() { Content = label, Foreground = Brushes.Cyan, Width = 70, VerticalAlignment = VerticalAlignment.Center };
            ComboBox cb = UITheme.CreateLayerCombo(); cb.Width = 100; TextBox tb = UITheme.CreateInputBox(); tb.Width = 50; tb.Height = 28; tb.FontSize = 12;
            Button bc = UITheme.CreateColorBtn(ts.ColorIndex);
            bc.Click += (s, e) => { Autodesk.AutoCAD.Windows.ColorDialog cd = new Autodesk.AutoCAD.Windows.ColorDialog(); if (cd.ShowDialog() == System.Windows.Forms.DialogResult.OK) { ts.ColorIndex = cd.Color.ColorIndex; bc.Background = new SolidColorBrush(UITheme.GetWpfColor(ts.ColorIndex)); } };
            CheckBox cM = new CheckBox() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "MText" };
            CheckBox cMs = new CheckBox() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Mask" };
            CheckBox cV = new CheckBox() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Visible" };
            Grid.SetColumn(l, 0); Grid.SetColumn(cb, 1); Grid.SetColumn(tb, 2); Grid.SetColumn(bc, 3); Grid.SetColumn(cM, 4); Grid.SetColumn(cMs, 5); Grid.SetColumn(cV, 6);
            g.Children.Add(l); g.Children.Add(cb); g.Children.Add(tb); g.Children.Add(bc); g.Children.Add(cM); g.Children.Add(cMs); g.Children.Add(cV);
            _textUiRows.Add(new TextUiRow() { CmbStyle = cb, TxtSize = tb, BtnColor = bc, ChkMText = cM, ChkMask = cMs, ChkVisible = cV, SettingsRef = ts, AssociatedLayer = layerName });
            return g;
        }

        private void StartNewTraverse(bool promptUser)
        {
            if (promptUser)
            {
                StackPanel p = new StackPanel() { Margin = new Thickness(40), Background = UITheme.BackgroundBrush };
                p.Children.Add(new TextBlock() { Text = "PICK START POINT", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });
                p.Children.Add(UITheme.CreateLabel("Easting (X)")); TextBox tE = UITheme.CreateInputBox(); p.Children.Add(tE);
                p.Children.Add(UITheme.CreateLabel("Northing (Y)")); TextBox tN = UITheme.CreateInputBox(); p.Children.Add(tN);
                tE.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; tN.Focus(); tN.SelectAll(); } };
                Button bPick = UITheme.CreateActionBtn("PICK ON SCREEN", Brushes.Orange);
                bPick.Click += (s, e) => { HideOverlay(); PromptPointResult ppr = AcApp.DocumentManager.MdiActiveDocument.Editor.GetPoint("\nPick Start: "); if (ppr.Status == PromptStatus.OK) InitTraverse(ppr.Value); };
                Button bOk = UITheme.CreateActionBtn("OK", UITheme.HighlightBrush);
                bOk.Click += (s, e) => { if (double.TryParse(tE.Text, out double x) && double.TryParse(tN.Text, out double y)) { HideOverlay(); InitTraverse(new Point3d(x, y, 0)); } };
                Button bCn = UITheme.CreateActionBtn("CANCEL", Brushes.DimGray); bCn.Click += (s, e) => HideOverlay();
                p.Children.Add(bPick); p.Children.Add(bOk); p.Children.Add(bCn);

                tN.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) bOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };

                ShowOverlay(p); tE.Focus();
            }
            else { InitTraverse(Point3d.Origin); }
        }
        private void InitTraverse(Point3d start)
        {
            _currentPoint = start;
            int traverseNum = _allTraverses.Count + 1;
            int baseNum = traverseNum * 1000;

            _traversePath.Clear(); _traversePath.Add(start); _undoStack.Clear();
            _currentTraverse = new TraverseChain() { ChainIndex = traverseNum, OriginPoint = start };
            _allTraverses.Add(_currentTraverse);
            RefreshTraverseList();

            // Create First Point
            int firstPt = baseNum + 1;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                Entity ptTxt = CreateText(firstPt.ToString(), CadConstants.LAY_TXT_PTNUM, start, AttachmentPoint.BottomLeft, tr, doc.Database, _config.TextPt);
                AddToDb(ptTxt, btr, tr);
                tr.Commit();
            }
            UpdateRunningMisclosure(); CalculateArea(); UpdateGuideText("ENTER AZIMUTH/DIST"); lblStatus.Content = $"Traverse {traverseNum} Started.";
            PanToPoint(start);
            _lastPtNum = firstPt;
            SaveState();
        }

        private void ShowRadiationOverlay(Point3d? overrideOrigin = null)
        {
            StackPanel p = new StackPanel() { Margin = new Thickness(40), Background = UITheme.BackgroundBrush };
            p.Children.Add(new TextBlock() { Text = "RADIATION (SIDE SHOT)", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });

            // PRE-FILL
            p.Children.Add(UITheme.CreateLabel("Azimuth (or Math)")); TextBox tA = UITheme.CreateInputBox(); tA.Text = string.IsNullOrEmpty(_savedRadAz) ? txtAzimuth.Text : _savedRadAz; p.Children.Add(tA);
            p.Children.Add(UITheme.CreateLabel("Distance (or Math)")); TextBox tD = UITheme.CreateInputBox(); tD.Text = string.IsNullOrEmpty(_savedRadDist) ? txtDistance.Text : _savedRadDist; p.Children.Add(tD);
            p.Children.Add(UITheme.CreateLabel("Comment")); TextBox tC = UITheme.CreateInputBox(); tC.Text = _savedRadComm; p.Children.Add(tC);

            // Tab flow
            tA.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; tD.Focus(); tD.SelectAll(); } };
            tD.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; tC.Focus(); tC.SelectAll(); } };

            Button bOk = UITheme.CreateActionBtn("SHOOT", UITheme.HighlightBrush);
            bOk.Click += (s, e) => {
                double az, dist;
                string azVal = CadMath.EvaluateMath(tA.Text);
                string distVal = CadMath.EvaluateMath(tD.Text);
                if (CadMath.TryParseAzimuth(azVal, out az) && double.TryParse(distVal, out dist))
                {
                    _savedRadAz = tA.Text; _savedRadDist = tD.Text; _savedRadComm = tC.Text; // Save for next time
                    ExecuteRadiation(az, dist, tC.Text, overrideOrigin ?? _currentPoint);
                    tA.Text = ""; tD.Text = ""; tC.Text = ""; tA.Focus(); // Clear for next shot
                }
                else { tA.BorderBrush = Brushes.Red; tD.BorderBrush = Brushes.Red; }
            };
            tC.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) bOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };

            Button bCn = UITheme.CreateActionBtn("CLOSE", Brushes.DimGray); bCn.Click += (s, e) => HideOverlay();
            p.Children.Add(bOk); p.Children.Add(bCn);
            ShowOverlay(p); tA.Focus(); tA.SelectAll();
        }

        private void ShowCommentOverlay()
        {
            StackPanel p = new StackPanel() { Margin = new Thickness(40), Background = UITheme.BackgroundBrush };
            p.Children.Add(new TextBlock() { Text = "ADD NOTE/COMMENT", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });
            TextBox t = UITheme.CreateInputBox(); p.Children.Add(t);
            Button bOk = UITheme.CreateActionBtn("ADD", UITheme.HighlightBrush);
            bOk.Click += (s, e) => {
                if (!string.IsNullOrWhiteSpace(t.Text))
                {
                    var doc = AcApp.DocumentManager.MdiActiveDocument;
                    using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        Entity txt = CreateText(t.Text, CadConstants.LAY_TXT_SYMB, _currentPoint, AttachmentPoint.MiddleLeft, tr, doc.Database, _config.TextComm);
                        ObjectId txtId = AddToDb(txt, btr, tr);

                        // Capture logic: Link to last segment so it moves
                        if (_currentTraverse != null && _currentTraverse.Segments.Count > 0)
                        {
                            var lastSeg = _currentTraverse.Segments.Last();
                            lastSeg.TextCommId = txtId; // Store ID to move later
                        }

                        List<ObjectId> ids = new List<ObjectId>(); ids.Add(txtId); _undoStack.Push(ids);
                        tr.Commit(); doc.Editor.UpdateScreen();
                        SaveState();
                    }
                    HideOverlay();
                }
            };
            t.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) bOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            Button bCn = UITheme.CreateActionBtn("CANCEL", Brushes.DimGray); bCn.Click += (s, e) => HideOverlay();
            p.Children.Add(bOk); p.Children.Add(bCn);
            ShowOverlay(p); t.Focus();
        }

        private void ExecuteManualDraw()
        {
            // AUTO START if empty
            if (_traversePath.Count == 0 || (string.IsNullOrWhiteSpace(txtAzimuth.Text) && string.IsNullOrWhiteSpace(txtDistance.Text)))
            {
                StartNewTraverse(true); return;
            }

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                try
                {
                    string finalAz = CadMath.EvaluateMath(txtAzimuth.Text);
                    string finalDist = CadMath.EvaluateMath(txtDistance.Text);

                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    double rawAz, dist;
                    if (!CadMath.TryParseAzimuth(finalAz, out rawAz) || !double.TryParse(finalDist, out dist))
                    {
                        txtAzimuth.BorderBrush = Brushes.Red; return;
                    }
                    else { txtAzimuth.BorderBrush = Brushes.DimGray; }

                    double rad = (90.0 - CadMath.ParseDmsToDegrees(rawAz)) * (Math.PI / 180.0);
                    Point3d endPoint = new Point3d(_currentPoint.X + (dist * Math.Cos(rad)), _currentPoint.Y + (dist * Math.Sin(rad)), _currentPoint.Z);
                    endPoint = CheckSnapping(endPoint, tr, btr);

                    EnsureLayerExists(_currentLayer, tr, doc.Database);
                    List<ObjectId> createdIds = new List<ObjectId>();

                    Autodesk.AutoCAD.DatabaseServices.Line ln = new Autodesk.AutoCAD.DatabaseServices.Line(_currentPoint, endPoint); ln.Layer = _currentLayer;
                    ObjectId lnId = AddToDb(ln, btr, tr); createdIds.Add(lnId);
                    var txtIds = CreateAnnotatedText(btr, tr, ln, rawAz, dist, rad); createdIds.AddRange(txtIds);

                    int baseNum = _currentTraverse.ChainIndex * 1000;
                    int fromPt = _lastPtNum;
                    int toPt = -1;

                    if (fromPt < baseNum) fromPt = baseNum + _currentTraverse.Segments.Count + 1;
                    toPt = fromPt + 1;

                    ObjectId ptId = ObjectId.Null;
                    if (!DwgDataManager.IsPointNumberAtLocation(endPoint, tr, doc.Database))
                    {
                        Entity ptTxt = CreateText(toPt.ToString(), CadConstants.LAY_TXT_PTNUM, endPoint, AttachmentPoint.BottomLeft, tr, doc.Database, _config.TextPt);
                        ptId = AddToDb(ptTxt, btr, tr); createdIds.Add(ptId);
                    }

                    _undoStack.Push(createdIds); tr.Commit();

                    var seg = new TraverseSegment() { FromPoint = fromPt, ToPoint = toPt, PointNumber = toPt, RawAzimuth = rawAz, Distance = dist, LineId = lnId, StartPoint = _currentPoint, EndPoint = endPoint, TraverseId = _currentTraverse.Id, TextPtId = ptId, TextBrgId = txtIds[0], TextDistId = txtIds[1] };
                    _currentTraverse.Segments.Add(seg);
                    _logItems.Insert(0, seg);
                    _lastPtNum = toPt;

                    _currentPoint = endPoint; _traversePath.Add(endPoint);
                    UpdateRunningMisclosure(); CalculateArea(); PlayAudio(); PanToPoint(endPoint); doc.Editor.UpdateScreen();
                    SaveState(); // PERSIST

                    // PERSISTENCE: Don't clear inputs. Focus back.
                    txtAzimuth.Focus(); txtAzimuth.SelectAll();
                }
                catch { txtAzimuth.BorderBrush = Brushes.Red; }
            }
        }

        private void ExecuteRadiation(double az, double dist, string comm, Point3d fromPt)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                try
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    double rad = (90.0 - CadMath.ParseDmsToDegrees(az)) * (Math.PI / 180.0);
                    Point3d end = new Point3d(fromPt.X + (dist * Math.Cos(rad)), fromPt.Y + (dist * Math.Sin(rad)), fromPt.Z);

                    EnsureLayerExists(_currentLayer, tr, doc.Database);
                    List<ObjectId> createdIds = new List<ObjectId>();

                    Autodesk.AutoCAD.DatabaseServices.Line ln = new Autodesk.AutoCAD.DatabaseServices.Line(fromPt, end); ln.Layer = _currentLayer;
                    ObjectId lnId = AddToDb(ln, btr, tr); createdIds.Add(lnId);

                    var txtIds = CreateAnnotatedText(btr, tr, ln, az, dist, rad); createdIds.AddRange(txtIds);

                    // Radiation Numbering (e.g. 1901, 1902...)
                    int baseNum = _currentTraverse.ChainIndex * 1000;
                    int radNum = baseNum + 900 + _currentTraverse.Radiations.Count + 1;

                    int parentStation = -1;
                    if (fromPt.DistanceTo(_currentTraverse.OriginPoint) < 0.001) parentStation = baseNum + 1;
                    else
                    {
                        var parentSeg = _currentTraverse.Segments.FirstOrDefault(s => s.EndPoint.DistanceTo(fromPt) < 0.001);
                        if (parentSeg != null) parentStation = parentSeg.PointNumber;
                    }

                    Entity ptTxt = CreateText(radNum.ToString(), CadConstants.LAY_TXT_PTNUM, end, AttachmentPoint.BottomLeft, tr, doc.Database, _config.TextPt);
                    ObjectId ptId = AddToDb(ptTxt, btr, tr); createdIds.Add(ptId);

                    ObjectId commId = ObjectId.Null;
                    if (!string.IsNullOrEmpty(comm))
                    {
                        Entity txt = CreateText(comm, CadConstants.LAY_TXT_SYMB, end, AttachmentPoint.MiddleLeft, tr, doc.Database, _config.TextComm);
                        commId = AddToDb(txt, btr, tr);
                        createdIds.Add(commId);
                    }
                    _undoStack.Push(createdIds);

                    var seg = new TraverseSegment() { IsRadiation = true, ParentStationId = parentStation, PointNumber = radNum, RawAzimuth = az, Distance = dist, LineId = lnId, StartPoint = fromPt, EndPoint = end, TextPtId = ptId, TextBrgId = txtIds[0], TextDistId = txtIds[1], TextCommId = commId, TraverseId = _currentTraverse.Id, Comment = comm };

                    _currentTraverse.Radiations.Add(seg);
                    _logItems.Insert(0, seg);

                    tr.Commit();
                    doc.Editor.UpdateScreen();
                    SaveState();
                }
                catch { }
            }
        }

        private void PanToPoint(Point3d target)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                Matrix3d matWCS2DCS = Matrix3d.PlaneToWorld(view.ViewDirection) * Matrix3d.Displacement(view.Target - Point3d.Origin) * Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target);
                matWCS2DCS = matWCS2DCS.Inverse();
                Point3d centerPt = target.TransformBy(matWCS2DCS);
                view.CenterPoint = new Point2d(centerPt.X, centerPt.Y);
                ed.SetCurrentView(view);
            }
        }

        private void UndoLastStep()
        {
            if (_undoStack.Count == 0) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids = _undoStack.Pop();
                foreach (var id in ids)
                {
                    if (!id.IsErased)
                    {
                        Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        if (ent is Autodesk.AutoCAD.DatabaseServices.Line ln)
                        {
                            if (_traversePath.Count > 1 && ln.EndPoint.DistanceTo(_currentPoint) < 0.001)
                                _currentPoint = ln.StartPoint;
                        }
                        ent.Erase();
                    }
                }
                if (_traversePath.Count > 1) _traversePath.RemoveAt(_traversePath.Count - 1);
                if (_logItems.Count > 0 && !(_logItems[0] is LogHeader))
                {
                    var itm = _logItems[0] as TraverseSegment;
                    if (itm != null)
                    {
                        if (itm.IsRadiation) _currentTraverse.Radiations.Remove(itm);
                        else
                        {
                            _currentTraverse.Segments.Remove(itm);
                        }
                    }
                    _logItems.RemoveAt(0);
                }
                tr.Commit(); doc.Editor.UpdateScreen();
            }
            SyncCurrentState();
            SaveState();
        }

        // --- EDITING & INSERTING ---
        private void LstHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstHistory.SelectedItem is TraverseSegment seg)
            {
                StackPanel sp = new StackPanel() { Margin = new Thickness(40), Background = UITheme.BackgroundBrush };
                string title = seg.IsRadiation ? $"EDIT RAD #{seg.PointNumber}" : $"EDIT LINE #{seg.FromPoint}->#{seg.ToPoint}";
                sp.Children.Add(new TextBlock() { Text = title, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });
                sp.Children.Add(UITheme.CreateLabel("Azimuth")); TextBox tAz = UITheme.CreateInputBox(); tAz.Text = CadMath.DmsToString(seg.RawAzimuth); sp.Children.Add(tAz);
                sp.Children.Add(UITheme.CreateLabel("Distance")); TextBox tDist = UITheme.CreateInputBox(); tDist.Text = seg.Distance.ToString(); sp.Children.Add(tDist);

                Button btnUpd = UITheme.CreateActionBtn("UPDATE & ADJUST", UITheme.HighlightBrush);
                btnUpd.Click += (s, ev) => { double a, d; if (CadMath.TryParseAzimuth(tAz.Text, out a) && double.TryParse(tDist.Text, out d)) { UpdateTraverseFromSegment(seg, a, d); HideOverlay(); } }; sp.Children.Add(btnUpd);

                if (!seg.IsRadiation)
                {
                    Button btnInsB = UITheme.CreateActionBtn("INSERT BEFORE", Brushes.Orange);
                    btnInsB.Click += (s, ev) => { double a, d; if (CadMath.TryParseAzimuth(tAz.Text, out a) && double.TryParse(tDist.Text, out d)) { InsertSegment(seg, a, d, true); HideOverlay(); } }; sp.Children.Add(btnInsB);
                    Button btnInsA = UITheme.CreateActionBtn("INSERT AFTER", Brushes.Orange);
                    btnInsA.Click += (s, ev) => { double a, d; if (CadMath.TryParseAzimuth(tAz.Text, out a) && double.TryParse(tDist.Text, out d)) { InsertSegment(seg, a, d, false); HideOverlay(); } }; sp.Children.Add(btnInsA);

                    Button btnRad = UITheme.CreateActionBtn("RADIATE FROM THIS POINT", Brushes.Purple);
                    btnRad.Click += (s, ev) => {
                        HideOverlay();
                        ShowRadiationOverlay(seg.EndPoint);
                    };
                    sp.Children.Add(btnRad);
                }

                Button btnDel = UITheme.CreateActionBtn("DELETE SEGMENT", Brushes.Red);
                btnDel.Click += (s, ev) => { DeleteSegmentAndStitch(seg); HideOverlay(); }; sp.Children.Add(btnDel);

                Button btnCancel = UITheme.CreateActionBtn("CANCEL", Brushes.DimGray); btnCancel.Click += (s, ev) => HideOverlay(); sp.Children.Add(btnCancel);
                ShowOverlay(sp);
            }
        }

        private void InsertSegment(TraverseSegment refSeg, double az, double dist, bool before)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                try
                {
                    int idx = _currentTraverse.Segments.IndexOf(refSeg);
                    if (!before) idx++;

                    Point3d start = (idx == 0) ? _currentTraverse.OriginPoint : _currentTraverse.Segments[idx - 1].EndPoint;
                    double rad = (90.0 - CadMath.ParseDmsToDegrees(az)) * (Math.PI / 180.0);
                    Point3d end = new Point3d(start.X + (dist * Math.Cos(rad)), start.Y + (dist * Math.Sin(rad)), start.Z);
                    Vector3d shift = end - start;

                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(((BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    EnsureLayerExists(_currentLayer, tr, doc.Database);
                    Autodesk.AutoCAD.DatabaseServices.Line ln = new Autodesk.AutoCAD.DatabaseServices.Line(start, end); ln.Layer = _currentLayer;
                    ObjectId lId = AddToDb(ln, btr, tr);
                    var txts = CreateAnnotatedText(btr, tr, ln, az, dist, rad);
                    Entity ptTxt = CreateText("0", CadConstants.LAY_TXT_PTNUM, end, AttachmentPoint.BottomLeft, tr, doc.Database, _config.TextPt);
                    ObjectId pId = AddToDb(ptTxt, btr, tr);

                    TraverseSegment newSeg = new TraverseSegment() { TraverseId = _currentTraverse.Id, RawAzimuth = az, Distance = dist, StartPoint = start, EndPoint = end, LineId = lId, TextBrgId = txts[0], TextDistId = txts[1], TextPtId = pId };
                    _currentTraverse.Segments.Insert(idx, newSeg);
                    _logItems.Insert(_logItems.IndexOf(refSeg) + (before ? 0 : 1), newSeg);

                    PropagateShift(newSeg, Matrix3d.Displacement(shift), tr);
                    RenumberTraversePoints(_currentTraverse, tr);
                    tr.Commit(); doc.Editor.Regen();
                }
                catch { }
            }
            SyncCurrentState();
            SaveState();
        }

        private void UpdateTraverseFromSegment(TraverseSegment seg, double newAz, double newDist)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                try
                {
                    double oldRad = (90 - CadMath.ParseDmsToDegrees(seg.RawAzimuth)) * Math.PI / 180;
                    double newRad = (90 - CadMath.ParseDmsToDegrees(newAz)) * Math.PI / 180;
                    Point3d newEnd = new Point3d(seg.StartPoint.X + (newDist * Math.Cos(newRad)), seg.StartPoint.Y + (newDist * Math.Sin(newRad)), seg.StartPoint.Z);
                    Vector3d delta = newEnd - new Point3d(seg.StartPoint.X + (seg.Distance * Math.Cos(oldRad)), seg.StartPoint.Y + (seg.Distance * Math.Sin(oldRad)), seg.StartPoint.Z);

                    if (!seg.LineId.IsErased) { ((Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(seg.LineId, OpenMode.ForWrite)).EndPoint = newEnd; }
                    EraseId(seg.TextBrgId, tr); EraseId(seg.TextDistId, tr);
                    var newTxts = CreateAnnotatedText((BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite), tr, (Autodesk.AutoCAD.DatabaseServices.Line)tr.GetObject(seg.LineId, OpenMode.ForRead), newAz, newDist, newRad);
                    seg.TextBrgId = newTxts[0]; seg.TextDistId = newTxts[1];
                    if (!seg.TextPtId.IsErased) ((Entity)tr.GetObject(seg.TextPtId, OpenMode.ForWrite)).TransformBy(Matrix3d.Displacement(delta));

                    seg.RawAzimuth = newAz; seg.Distance = newDist; seg.EndPoint = newEnd; seg.NotifyUpdate();

                    if (seg.IsRadiation)
                    {
                        if (!seg.TextCommId.IsNull && !seg.TextCommId.IsErased)
                        {
                            Entity comm = (Entity)tr.GetObject(seg.TextCommId, OpenMode.ForWrite);
                            comm.TransformBy(Matrix3d.Displacement(delta));
                        }
                    }
                    else
                    {
                        PropagateShift(seg, Matrix3d.Displacement(delta), tr);
                    }
                    tr.Commit(); doc.Editor.Regen(); UpdateRunningMisclosure(); CalculateArea();
                }
                catch { }
            }
            SyncCurrentState();
            SaveState();
        }

        private void DeleteSegmentAndStitch(TraverseSegment seg)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                try
                {
                    EraseId(seg.LineId, tr); EraseId(seg.TextBrgId, tr); EraseId(seg.TextDistId, tr); EraseId(seg.TextPtId, tr); EraseId(seg.TextCommId, tr);
                    _logItems.Remove(seg);

                    if (seg.IsRadiation)
                    {
                        _currentTraverse.Radiations.Remove(seg);
                    }
                    else
                    {
                        Vector3d delta = seg.StartPoint - seg.EndPoint;
                        PropagateShift(seg, Matrix3d.Displacement(delta), tr);
                        _currentTraverse.Segments.Remove(seg);
                        RenumberTraversePoints(_currentTraverse, tr);
                    }
                    tr.Commit(); doc.Editor.Regen();
                }
                catch { }
            }
            SyncCurrentState();
            SaveState();
        }

        private void PropagateShift(TraverseSegment seg, Matrix3d matMove, Transaction tr)
        {
            if (_currentTraverse != null)
            {
                int idx = _currentTraverse.Segments.IndexOf(seg);
                if (idx == -1) return;
                for (int i = idx + 1; i < _currentTraverse.Segments.Count; i++)
                {
                    var sub = _currentTraverse.Segments[i];
                    sub.StartPoint = sub.StartPoint.TransformBy(matMove); sub.EndPoint = sub.EndPoint.TransformBy(matMove);
                    TransformEntity(sub.LineId, matMove, tr); TransformEntity(sub.TextBrgId, matMove, tr); TransformEntity(sub.TextDistId, matMove, tr); TransformEntity(sub.TextPtId, matMove, tr); TransformEntity(sub.TextCommId, matMove, tr);

                    foreach (var rad in _currentTraverse.Radiations)
                    {
                        if (rad.ParentStationId == sub.FromPoint)
                        {
                            rad.StartPoint = rad.StartPoint.TransformBy(matMove);
                            rad.EndPoint = rad.EndPoint.TransformBy(matMove);
                            TransformEntity(rad.LineId, matMove, tr); TransformEntity(rad.TextBrgId, matMove, tr); TransformEntity(rad.TextDistId, matMove, tr); TransformEntity(rad.TextPtId, matMove, tr); TransformEntity(rad.TextCommId, matMove, tr);
                        }
                    }
                }
            }
        }

        private void RenumberTraversePoints(TraverseChain chain, Transaction tr)
        {
            int baseNum = chain.ChainIndex * 1000;
            for (int i = 0; i < chain.Segments.Count; i++)
            {
                var seg = chain.Segments[i];
                seg.FromPoint = baseNum + i + 1;
                seg.ToPoint = baseNum + i + 2;
                seg.PointNumber = seg.ToPoint;
                seg.NotifyUpdate();

                if (!seg.TextPtId.IsErased)
                {
                    Entity ent = (Entity)tr.GetObject(seg.TextPtId, OpenMode.ForWrite);
                    if (ent is DBText dt) dt.TextString = seg.PointNumber.ToString(); else if (ent is MText mt) mt.Contents = seg.PointNumber.ToString();
                }
            }
        }

        // --- HELPER METHODS ---
        private void EraseId(ObjectId id, Transaction tr) { if (id.IsValid && !id.IsErased) ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase(); }
        private void TransformEntity(ObjectId id, Matrix3d mat, Transaction tr) { if (!id.IsValid || id.IsErased) return; ((Entity)tr.GetObject(id, OpenMode.ForWrite)).TransformBy(mat); }
        private void ApplySettingsToExistingText()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            using (DocumentLock loc = doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (var chain in _allTraverses)
                {
                    foreach (var seg in chain.Segments) { UpdateEntityStyle(seg.TextBrgId, _config.TextBrg, tr); UpdateEntityStyle(seg.TextDistId, _config.TextDist, tr); UpdateEntityStyle(seg.TextPtId, _config.TextPt, tr); UpdateEntityStyle(seg.TextCommId, _config.TextComm, tr); }
                    foreach (var rad in chain.Radiations) { UpdateEntityStyle(rad.TextBrgId, _config.TextBrg, tr); UpdateEntityStyle(rad.TextDistId, _config.TextDist, tr); UpdateEntityStyle(rad.TextPtId, _config.TextPt, tr); UpdateEntityStyle(rad.TextCommId, _config.TextComm, tr); }
                }
                tr.Commit(); doc.Editor.Regen();
            }
        }
        private void UpdateEntityStyle(ObjectId id, TextSettings ts, Transaction tr)
        {
            if (id.IsValid && !id.IsErased)
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                ent.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, ts.ColorIndex);
                if (ent is DBText dt) { dt.Height = ts.Size; dt.Visible = ts.Visible; } else if (ent is MText mt) { mt.TextHeight = ts.Size; mt.BackgroundFill = ts.Masking; mt.Visible = ts.Visible; }
            }
        }
        private Point3d CheckSnapping(Point3d target, Transaction tr, BlockTableRecord btr)
        {
            foreach (ObjectId id in btr)
            {
                Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                if (ent is DBPoint pt && pt.Position.DistanceTo(target) < _config.SnapTolerance) return pt.Position;
            }
            return target;
        }
        // CORRECT VERSION (With Database Logic)
        private ObjectId AddToDb(Entity ent, BlockTableRecord btr, Transaction tr)
        {
            ObjectId id = btr.AppendEntity(ent);
            tr.AddNewlyCreatedDBObject(ent, true);
            return id;
        }

        private List<ObjectId> CreateAnnotatedText(BlockTableRecord btr, Transaction tr, Autodesk.AutoCAD.DatabaseServices.Line ln, double rawAz, double dist, double rad)
        {
            List<ObjectId> ids = new List<ObjectId>();
            double textRot = rad; double norm = rad % (Math.PI * 2); if (norm < 0) norm += Math.PI * 2;
            if (norm > Math.PI / 2 && norm <= 3 * Math.PI / 2) textRot += Math.PI;
            Point3d mid = ln.StartPoint + (ln.EndPoint - ln.StartPoint) / 2.0;
            Vector3d up = new Vector3d(-Math.Sin(rad), Math.Cos(rad), 0); if (norm > Math.PI / 2 && norm <= 3 * Math.PI / 2) up = -up;
            ids.Add(AddToDb(CreateText(CadMath.DegreesToDmsFormatted(CadMath.ParseDmsToDegrees(rawAz)), CadConstants.LAY_TXT_BRG, mid + up * _config.TextBrg.Size * 0.7, AttachmentPoint.BottomCenter, tr, btr.Database, _config.TextBrg, textRot), btr, tr));
            ids.Add(AddToDb(CreateText(dist.ToString("0.000"), CadConstants.LAY_TXT_DIST, mid - up * _config.TextDist.Size * 0.7, AttachmentPoint.TopCenter, tr, btr.Database, _config.TextDist, textRot), btr, tr));
            return ids;
        }
        private Entity CreateText(string txt, string lay, Point3d pt, AttachmentPoint align, Transaction tr, Database db, TextSettings ts, double rot = 0)
        {
            EnsureLayerExists(lay, tr, db);
            if (ts.IsMText)
            {
                MText mt = new MText() { Contents = txt, Layer = lay, TextHeight = ts.Size, Rotation = rot, Location = pt, Attachment = align };
                if (ts.Masking) { mt.BackgroundFill = true; mt.UseBackgroundColor = true; mt.BackgroundScaleFactor = 1.1; }
                mt.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, ts.ColorIndex); return mt;
            }
            else
            {
                DBText dt = new DBText() { TextString = txt, Layer = lay, Height = ts.Size, Rotation = rot, Position = pt };
                dt.Justify = align; dt.AlignmentPoint = pt; dt.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, ts.ColorIndex); return dt;
            }
        }
        private void EnsureLayerExists(string layer, Transaction tr, Database db)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layer))
            {
                lt.UpgradeOpen(); LayerTableRecord ltr = new LayerTableRecord() { Name = layer, Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7) };
                lt.Add(ltr); tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }
        private void UpdateRunningMisclosure()
        {
            if (_traversePath.Count < 2) { txtRunningClosure.Text = "Misclosure: N/A"; return; }
            Point3d s = _traversePath[0], e = _traversePath.Last();
            txtRunningClosure.Text = $"Err: {s.DistanceTo(e):0.000}m @ {CadMath.DegreesToDmsFormatted((Math.Atan2(s.Y - e.Y, s.X - e.X) * 180 / Math.PI + 270) % 360)}";
        }
        private void CalculateArea()
        {
            if (_traversePath.Count < 2) return;
            List<Point3d> p = new List<Point3d>(_traversePath); if (p[0].DistanceTo(p.Last()) > 0.001) p.Add(p[0]);
            double area = 0; for (int i = 0; i < p.Count - 1; i++) area += (p[i].X * p[i + 1].Y) - (p[i + 1].X * p[i].Y);
            txtAreaInfo.Text = $"Area: {Math.Abs(area) / 2.0:0.00} m²";
        }
        private void SetCurrentLayer(string l, Button b) { if (l != "NOT SET") { _currentLayer = l; HighlightActiveLayer(b); } }
        private void HighlightActiveLayer(Button b)
        {
            if (btnQ == null) return;
            btnQ.BorderThickness = btnW.BorderThickness = btnE.BorderThickness = btnA.BorderThickness = btnS.BorderThickness = btnD.BorderThickness = new Thickness(1);
            btnQ.BorderBrush = btnW.BorderBrush = Brushes.Gray;
            b.BorderThickness = new Thickness(3); b.BorderBrush = Brushes.White;
        }
        private void UpdateGuideText(string t) => lblGuide.Text = t;
        private void PlayAudio()
        {
            if (!_config.AudioFeedback) return;
            try
            {
                string f = ""; string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                switch (_config.AudioSound) { case "Beep": f = "Windows Default.wav"; break; case "Asterisk": f = "Windows Background.wav"; break; default: f = "Windows Background.wav"; break; }
                string full = System.IO.Path.Combine(path, f);
                if (File.Exists(full)) new SoundPlayer(full).Play(); else SystemSounds.Beep.Play();
            }
            catch { SystemSounds.Beep.Play(); }
        }

        private void OpenCalculator(TextBox tb, bool dms)
        {
            StackPanel sp = new StackPanel() { Margin = new Thickness(40), Background = UITheme.BackgroundBrush };
            sp.Children.Add(new TextBlock() { Text = "CALCULATOR", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 20) });
            TextBox t = UITheme.CreateInputBox(); t.Text = tb.Text; sp.Children.Add(t);
            Button b = UITheme.CreateActionBtn("=", UITheme.HighlightBrush);
            b.Click += (s, e) => {
                try
                {
                    if (dms)
                    {
                        string[] p;
                        if (t.Text.Contains("+")) { p = t.Text.Split('+'); tb.Text = CadMath.DmsToString(CadMath.AddSubDms(double.Parse(p[0]), double.Parse(p[1]), true)); }
                        else if (t.Text.Contains("-")) { p = t.Text.Split('-'); tb.Text = CadMath.DmsToString(CadMath.AddSubDms(double.Parse(p[0]), double.Parse(p[1]), false)); }
                    }
                    else { tb.Text = new System.Data.DataTable().Compute(t.Text, "").ToString(); }
                }
                catch { }
                HideOverlay();
            };
            t.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            Button bc = UITheme.CreateActionBtn("CANCEL", Brushes.DimGray); bc.Click += (s, e) => HideOverlay();
            sp.Children.Add(b); sp.Children.Add(bc);
            ShowOverlay(sp); t.Focus();
        }

        private void PopulateComboBoxes()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; if (doc == null) return;
            List<string> l = new List<string>(), s = new List<string>();
            using (doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead)) l.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                foreach (ObjectId id in (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead)) s.Add(((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                tr.Commit();
            }
            l.Sort(); s.Sort(); var c = new[] { cmbLayQ, cmbLayW, cmbLayE, cmbLayA, cmbLayS, cmbLayD };
            foreach (var cb in c) cb.ItemsSource = l;
            foreach (var r in _textUiRows) r.CmbStyle.ItemsSource = s;
            // Update Button Colors from Layers
            using (doc.LockDocument()) using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                void Upd(Button b, string lname, string key)
                {
                    b.Content = new TextBlock() { Text = key, FontWeight = FontWeights.Bold, FontSize = 16 };
                    if (lt.Has(lname))
                    {
                        var col = ((LayerTableRecord)tr.GetObject(lt[lname], OpenMode.ForRead)).Color;
                        b.Background = new SolidColorBrush(Color.FromRgb(col.ColorValue.R, col.ColorValue.G, col.ColorValue.B));
                        b.Content = new TextBlock() { Text = $"{key}\n{lname}", FontSize = 10, TextWrapping = TextWrapping.Wrap, TextAlignment = System.Windows.TextAlignment.Center };
                    }
                    else
                    {
                        b.Content = new TextBlock() { Text = $"{key}\nSET LAYER", Foreground = Brushes.Red, FontSize = 10, FontWeight = FontWeights.Bold, TextAlignment = System.Windows.TextAlignment.Center };
                    }
                }
                Upd(btnQ, _config.LayQ, "Q"); Upd(btnW, _config.LayW, "W"); Upd(btnE, _config.LayE, "E");
                Upd(btnA, _config.LayA, "A"); Upd(btnS, _config.LayS, "S"); Upd(btnD, _config.LayD, "D");
            }
        }
        private void UpdateUIFromConfig()
        {
            cmbLayQ.Text = _config.LayQ; cmbLayW.Text = _config.LayW; cmbLayE.Text = _config.LayE;
            cmbLayA.Text = _config.LayA; cmbLayS.Text = _config.LayS; cmbLayD.Text = _config.LayD;
            foreach (var r in _textUiRows)
            {
                r.CmbStyle.Text = r.SettingsRef.Style; r.TxtSize.Text = r.SettingsRef.Size.ToString();
                r.ChkMText.IsChecked = r.SettingsRef.IsMText; r.ChkMask.IsChecked = r.SettingsRef.Masking; r.ChkVisible.IsChecked = r.SettingsRef.Visible;
                r.BtnColor.Background = new SolidColorBrush(UITheme.GetWpfColor(r.SettingsRef.ColorIndex));
            }
            if (setChkAudio != null) setChkAudio.IsChecked = _config.AudioFeedback;
            if (cmbSound != null) cmbSound.SelectedItem = _config.AudioSound;
        }
        private void SaveSettings()
        {
            _config.LayQ = cmbLayQ.Text; _config.LayW = cmbLayW.Text; _config.LayE = cmbLayE.Text;
            _config.LayA = cmbLayA.Text; _config.LayS = cmbLayS.Text; _config.LayD = cmbLayD.Text;
            foreach (var r in _textUiRows)
            {
                r.SettingsRef.Style = r.CmbStyle.Text; if (double.TryParse(r.TxtSize.Text, out double d)) r.SettingsRef.Size = d;
                r.SettingsRef.IsMText = r.ChkMText.IsChecked == true; r.SettingsRef.Masking = r.ChkMask.IsChecked == true; r.SettingsRef.Visible = r.ChkVisible.IsChecked == true;
            }
            if (setChkAudio != null) _config.AudioFeedback = setChkAudio.IsChecked == true;
            if (cmbSound != null) _config.AudioSound = cmbSound.Text;
            AppSettings.Save(_config);
        }

        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (lstHistory.SelectedItem is TraverseSegment seg) PanToPoint(seg.EndPoint); }
        private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_overlayContainer.Visibility == System.Windows.Visibility.Visible)
            {
                if (e.Key == Key.Escape) { HideOverlay(); e.Handled = true; }
                return;
            }
            if (e.Key == Key.PageUp) { e.Handled = true; StartNewTraverse(true); }
            if (e.Key == Key.PageDown) { e.Handled = true; ShowRadiationOverlay(); }
            if (e.Key == Key.Insert) { e.Handled = true; ShowCommentOverlay(); }
            if (e.Key == Key.Q) { SetCurrentLayer(_config.LayQ, btnQ); e.Handled = true; }
            if (e.Key == Key.W) { SetCurrentLayer(_config.LayW, btnW); e.Handled = true; }
            if (e.Key == Key.E) { SetCurrentLayer(_config.LayE, btnE); e.Handled = true; }
            if (e.Key == Key.A) { SetCurrentLayer(_config.LayA, btnA); e.Handled = true; }
            if (e.Key == Key.S) { SetCurrentLayer(_config.LayS, btnS); e.Handled = true; }
            if (e.Key == Key.D) { SetCurrentLayer(_config.LayD, btnD); e.Handled = true; }
            if (e.Key == Key.Delete) { /* Undo */ e.Handled = true; UndoLastStep(); }
        }
        private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; if (sender == txtAzimuth) txtDistance.Focus(); else ExecuteManualDraw(); }
            if (sender == txtAzimuth && double.TryParse(txtAzimuth.Text, out double c))
            {
                double dec = CadMath.ParseDmsToDegrees(c);
                if (e.Key == Key.Up) dec += 90; if (e.Key == Key.Down) dec -= 90; if (e.Key == Key.Right) dec += 180; if (e.Key == Key.Left) dec -= 180;
                dec = dec % 360; if (dec < 0) dec += 360;
                if (e.Key >= Key.Left && e.Key <= Key.Down) { txtAzimuth.Text = CadMath.DegreesToDmsString(dec); e.Handled = true; }
            }
        }
    }

    public class LogHeader { public string DisplayLog { get; set; } }
}