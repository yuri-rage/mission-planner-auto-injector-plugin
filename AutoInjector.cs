using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using log4net;
using MissionPlanner.Comms;
using MissionPlanner.GCSViews.ConfigurationView;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;

namespace AutoInjector
{
    public class AutoInjector : Plugin
    {
        public override string Name => "Auto Injector (RTCM3)";
        public override string Version => "v0.3.0";
        public override string Author => "Yuri Rage";
        public override float loopratehz { get; set; } = 1.0f;

        private static readonly ILog _log = LogManager.GetLogger("AutoInjector");
        private static void Log(string msg) => _log.Info(msg);

        // --- Cached Reflection ---
        private static readonly Type TInject = typeof(ConfigSerialInjectGPS);
        private static FieldInfo F(Type t, string name, bool isStatic) =>
            t.GetField(name, (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo F_comPort = F(TInject, "comPort", true);
        private static readonly FieldInfo F_threadrun = F(TInject, "threadrun", true);
        private static readonly FieldInfo F_bytes = F(TInject, "bytes", true);
        private static readonly FieldInfo F_msgseen = F(TInject, "msgseen", true);
        private static readonly FieldInfo F_statusLine3 = F(TInject, "status_line3", true);
        private static readonly FieldInfo F_injectInstance = F(TInject, "Instance", true);

        private static readonly Func<CommsNTRIP.GgaInfo> LiveGgaDelegate = InitLiveGgaDelegate();
        private static Func<CommsNTRIP.GgaInfo> InitLiveGgaDelegate()
        {
            var m = TInject.GetMethod("GetLiveGga", BindingFlags.Static | BindingFlags.NonPublic);
            return m != null ? (Func<CommsNTRIP.GgaInfo>)Delegate.CreateDelegate(typeof(Func<CommsNTRIP.GgaInfo>), m) : null;
        }

        // --- State ---
        private static ConfigSerialInjectGPS _lastHookedInstance;
        private static ToolStripControlHost _statusHost;
        private static StatusWidgetPanel _statusPanel;
        private static Font _statusFont;
        private static ToolTip _statusToolTip;
        private static int _lastTotalBytes;
        private static int _zeroRateSeconds;
        private static int _lastWidth;
        private static string _lastToolTipText;

        private static readonly (string Tag, Func<string, bool> Match)[] Constellations =
        {
            ("GPS", k => k.StartsWith("Rtcm107") || k == "Rtcm1004" || k == "Rtcm1002"),
            ("GLO", k => k.StartsWith("Rtcm108") || k == "Rtcm1012"),
            ("GAL", k => k.StartsWith("Rtcm109")),
            ("BDS", k => k.StartsWith("Rtcm112")),
        };

        private static bool GetBool(string key, bool def) =>
            bool.TryParse(Settings.Instance[key], out var v) ? v : def;

        public override bool Init() => true;

        public override bool Loaded()
        {
            RunOnUi(async () =>
            {
                await ConnectToLastConfiguredSource();
                UpdateStatusBar();
            });
            return true;
        }

        public override bool Loop()
        {
            RunOnUi(() =>
            {
                SyncUiInstance();
                UpdateStatusBar();
            });
            return true;
        }

        public override bool Exit()
        {
            RunOnUi(() =>
            {
                if (_statusHost != null && !_statusHost.IsDisposed)
                {
                    _statusHost.Owner?.Items.Remove(_statusHost);
                    _statusHost.Dispose();
                    _statusHost = null;
                }
                _statusPanel = null;
                _statusToolTip?.Dispose();
                _statusToolTip = null;
                _statusFont?.Dispose();
                _statusFont = null;
                _lastWidth = 0;
            });
            return true;
        }

        private void RunOnUi(Action action)
        {
            var form = Host?.MainForm;
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
            try
            {
                if (form.InvokeRequired) form.BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // --- UI Synchronization ---
        private static void SyncUiInstance()
        {
            var active = F_injectInstance?.GetValue(null) as ConfigSerialInjectGPS;
            if (active == null || active == _lastHookedInstance || active.IsDisposed) return;
            _lastHookedInstance = active;

            Bind(active, "chk_sendgga", "SerialInjectGPS_sendgga");
            Bind(active, "check_sendntripv1", "SerialInjectGPS_ntripv1");
        }

        private static void Bind(ConfigSerialInjectGPS inst, string fieldName, string settingKey)
        {
            if (!(F(TInject, fieldName, false)?.GetValue(inst) is CheckBox chk) || chk.IsDisposed) return;
            if (bool.TryParse(Settings.Instance[settingKey], out var saved)) chk.Checked = saved;
            chk.CheckedChanged += (s, e) => Settings.Instance[settingKey] = chk.Checked.ToString();
        }

        // --- Auto-Connect ---
        private async Task<bool> ConnectToLastConfiguredSource()
        {
            try
            {
                if (!Host.config.ContainsKey("SerialInjectGPS_port"))
                    return false;

                if (F_comPort?.GetValue(null) is ICommsSerial cur && cur.IsOpen)
                    return true;

                // UI thread: safe to build the control
                var inject = (F_injectInstance?.GetValue(null) as ConfigSerialInjectGPS) ?? new ConfigSerialInjectGPS();
                SyncUiInstance();

                string portType = Host.config["SerialInjectGPS_port"];

                if (portType == "NTRIP" && Host.config.ContainsKey("NTRIP_url"))
                {
                    string url = Host.config["NTRIP_url"];
                    if (string.IsNullOrWhiteSpace(url)) return false;

                    var ntrip = new CommsNTRIP { ntrip_v1 = GetBool("SerialInjectGPS_ntripv1", false) };
                    (ntrip.lat, ntrip.lng, ntrip.alt) = ParseBasePos();
                    ntrip.GgaSource = () => GetBool("SerialInjectGPS_sendgga", true)
                        ? LiveGgaDelegate?.Invoke()
                        : new CommsNTRIP.GgaInfo { Lat = 0, Lng = 0 };

                    await Task.Run(() => ntrip.Open(url)); // only the blocking socket work leaves the UI thread
                    F_comPort?.SetValue(null, ntrip);
                    await inject.DoConnect();              // back on the UI thread
                    return true;
                }

                if (portType.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                {
                    if (!SerialPort.GetPortNames().Contains(portType, StringComparer.OrdinalIgnoreCase))
                    {
                        Log($"COM port {portType} is not present; skipping auto-connect.");
                        return false;
                    }
                    await inject.DoConnect();
                    return true;
                }

                if (portType == "TCP Client" || portType.StartsWith("UDP"))
                {
                    await inject.DoConnect();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Auto-connect failed: {ex}");
            }
            return false;
        }

        private static (double Lat, double Lng, double Alt) ParseBasePos()
        {
            if (Settings.Instance.ContainsKey("base_pos"))
            {
                var p = Settings.Instance["base_pos"].Split(',');
                if (p.Length >= 3 &&
                    double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lng) &&
                    double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double alt))
                    return (lat, lng, alt);
            }
            return (0, 0, 0);
        }

        // --- Status Bar Widget ---
        private void UpdateStatusBar()
        {
            try
            {
                var mainMenu = Host?.MainForm?.MainMenu;
                if (mainMenu == null || mainMenu.IsDisposed) return;

                EnsureStatusWidget(mainMenu);
                if (_statusHost == null || _statusHost.IsDisposed || _statusPanel == null || _statusPanel.IsDisposed) return;

                // Query stream metrics
                bool isPortOpen = F_comPort?.GetValue(null) is ICommsSerial p && p.IsOpen;
                bool isThreadRunning = (bool)(F_threadrun?.GetValue(null) ?? false);
                int totalBytes = (int)(F_bytes?.GetValue(null) ?? 0);
                int rate = Math.Max(0, totalBytes - _lastTotalBytes);
                _lastTotalBytes = totalBytes;

                if (rate == 0) _zeroRateSeconds++;
                else _zeroRateSeconds = 0;

                var msgseen = F_msgseen?.GetValue(null) as ConcurrentDictionary<string, int>;
                string basePosInfo = F_statusLine3?.GetValue(null) as string;

                bool hasBase = false;
                var constellations = new List<string>();
                var activeMessages = new List<string>();

                if (msgseen != null && !msgseen.IsEmpty)
                {
                    var keys = msgseen.Keys.ToList();
                    hasBase = keys.Any(k => k == "Rtcm1005" || k == "Rtcm1006" || k == "Rtcm4072");
                    constellations = Constellations.Where(c => keys.Any(c.Match)).Select(c => c.Tag).ToList();
                    activeMessages = keys.OrderBy(k => k).Select(k => $"{k.Replace("Rtcm", "")}({msgseen[k]})").ToList();
                }

                string portType = Settings.Instance["SerialInjectGPS_port"] ?? "None";
                string sourceDisplay = portType == "NTRIP" && Settings.Instance.ContainsKey("NTRIP_url")
                    ? GetNtripMountpoint(Settings.Instance["NTRIP_url"])
                    : (portType.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? $"{portType} ({Settings.Instance["SerialInjectGPS_baud"] ?? "115200"})" : portType);

                string line1Text;
                string line2Text;
                Color primaryColor;
                Color secondaryColor;
                string newTooltip;

                if (!isPortOpen || !isThreadRunning)
                {
                    line1Text = "RTCM3: Disconnected";
                    line2Text = $"Source: {portType}";
                    primaryColor = Color.Silver;
                    secondaryColor = Color.Gray;
                    newTooltip = $"Correction stream is not active.\nLast configured source: {portType}";
                }
                else if (totalBytes == 0 || (rate == 0 && (msgseen == null || msgseen.IsEmpty)))
                {
                    line1Text = $"RTCM3: {sourceDisplay}";
                    line2Text = "Connecting...";
                    primaryColor = secondaryColor = Color.Gold;
                    newTooltip = $"Source: {sourceDisplay}\nWaiting for incoming RTCM3 packets...";
                }
                else if (_zeroRateSeconds >= 3)
                {
                    string baseTag = hasBase ? "Base OK" : "No Base";
                    line1Text = $"RTCM3: {sourceDisplay} • Stalled";
                    line2Text = $"No data for {_zeroRateSeconds}s • {baseTag}";
                    primaryColor = secondaryColor = Color.DarkOrange;
                    newTooltip = $"Source: {sourceDisplay}\nWarning: No correction data received in {_zeroRateSeconds}s.";
                }
                else
                {
                    string baseInfo = hasBase ? "Base OK" : "No Base";
                    string constellInfo = constellations.Count > 0 ? string.Join("+", constellations) : "---";

                    line1Text = $"RTCM3: {sourceDisplay} • {FormatRate(rate)}";
                    line2Text = $"{baseInfo} • [{constellInfo}]";
                    primaryColor = secondaryColor = hasBase ? Color.Lime : Color.SpringGreen;

                    var sb = new StringBuilder();
                    sb.AppendLine("RTCM3 Correction Stream:");
                    sb.AppendLine($"  Source: {portType} ({sourceDisplay})");
                    if (portType == "NTRIP")
                    {
                        sb.AppendLine($"  URL: {SanitizeUrl(Settings.Instance["NTRIP_url"])}");
                        sb.AppendLine($"  Protocol: {(GetBool("SerialInjectGPS_ntripv1", false) ? "NTRIP v1.0" : "NTRIP v2.0")} | GGA: {(GetBool("SerialInjectGPS_sendgga", true) ? "Enabled" : "Disabled")}");
                    }
                    sb.AppendLine($"  Throughput: {FormatRate(rate)} (Total: {FormatBytes(totalBytes)})");
                    if (constellations.Count > 0) sb.AppendLine($"  Constellations: {string.Join(", ", constellations)}");
                    if (activeMessages.Count > 0) sb.AppendLine($"  Messages: {string.Join(" ", activeMessages.Take(8))}");
                    if (!string.IsNullOrWhiteSpace(basePosInfo)) sb.AppendLine($"  Base Position: {basePosInfo}");
                    newTooltip = sb.ToString().TrimEnd();
                }

                newTooltip = $"{newTooltip}\nClick to open RTK/GPS Inject settings.";
                _statusPanel.UpdateContent(line1Text, primaryColor, line2Text, secondaryColor);

                var prefSize = _statusPanel.MeasurePreferredSize();
                int targetWidth = (int)(Math.Ceiling(prefSize.Width / 8.0) * 8);
                int targetHeight = prefSize.Height;

                if (targetWidth > _lastWidth || targetWidth < _lastWidth - 16 || _statusPanel.Height != targetHeight)
                {
                    _lastWidth = targetWidth;
                    _statusPanel.Size = new Size(targetWidth, targetHeight);
                    _statusHost.Size = new Size(targetWidth, targetHeight);
                }

                if (_lastToolTipText != newTooltip)
                {
                    _lastToolTipText = newTooltip;
                    _statusToolTip?.SetToolTip(_statusPanel, newTooltip);
                }
            }
            catch (Exception ex)
            {
                Log($"Error updating status bar: {ex}");
            }
        }

        private void EnsureStatusWidget(MenuStrip mainMenu)
        {
            if (mainMenu == null || mainMenu.IsDisposed) return;

            // Remove any stale host from a previous plugin instance
            var oldItem = mainMenu.Items["lbl_rtcm_status"];
            if (oldItem != null && oldItem != _statusHost)
            {
                mainMenu.Items.Remove(oldItem);
                oldItem.Dispose();
            }

            if (_statusFont == null)
            {
                _statusFont = new Font(mainMenu.Font?.FontFamily ?? FontFamily.GenericSansSerif, 8F, FontStyle.Regular);
            }

            if (_statusHost == null || _statusHost.IsDisposed || _statusPanel == null || _statusPanel.IsDisposed)
            {
                _statusPanel = new StatusWidgetPanel
                {
                    Name = "pnl_rtcm_status",
                    Font = _statusFont
                };
                _statusPanel.UpdateContent("RTCM3: Initializing...", Color.Silver, "Connecting...", Color.Gray);

                var initSize = _statusPanel.MeasurePreferredSize();
                int initWidth = (int)(Math.Ceiling(initSize.Width / 8.0) * 8);
                _statusPanel.Size = new Size(initWidth, initSize.Height);
                _lastWidth = initWidth;

                _statusHost = new ToolStripControlHost(_statusPanel)
                {
                    Name = "lbl_rtcm_status",
                    AutoSize = false,
                    Size = new Size(initWidth, initSize.Height),
                    Margin = new Padding(8, 0, 0, 0),
                    BackColor = Color.Transparent
                };

                _statusPanel.Click += (s, e) =>
                {
                    try { new ConfigSerialInjectGPS().Show(); }
                    catch (Exception ex) { Log(ex.ToString()); }
                };

                _statusToolTip = new ToolTip();
            }

            int targetIndex = GetTargetInsertionIndex(mainMenu);

            if (_statusHost.Owner != mainMenu)
            {
                _statusHost.Owner?.Items.Remove(_statusHost);
                mainMenu.Items.Insert(Math.Min(targetIndex, mainMenu.Items.Count), _statusHost);
            }
            else
            {
                int currentIndex = mainMenu.Items.IndexOf(_statusHost);
                int expectedIndex = targetIndex > currentIndex ? targetIndex - 1 : targetIndex;
                if (currentIndex != expectedIndex)
                {
                    mainMenu.Items.Remove(_statusHost);
                    int newTarget = GetTargetInsertionIndex(mainMenu);
                    mainMenu.Items.Insert(Math.Min(newTarget, mainMenu.Items.Count), _statusHost);
                }
            }
        }

        private static int GetTargetInsertionIndex(MenuStrip mainMenu)
        {
            // 1. TitanPlanner: place to the right of the vehicle state control (GPS stats are rightmost inside it)
            var vehicleState = mainMenu.Items.Cast<ToolStripItem>()
                .FirstOrDefault(i => i != _statusHost &&
                                     (i.Name == "toolStripVehicleState" ||
                                      i.GetType().Name == "ToolStripVehicleState"));

            if (vehicleState != null)
            {
                return mainMenu.Items.IndexOf(vehicleState) + 1;
            }

            // 2. Vanilla MissionPlanner: place right of the top navigation buttons
            // (after MenuHelp if visible, otherwise after SITL/SIMULATION)
            var help = mainMenu.Items.Cast<ToolStripItem>()
                .FirstOrDefault(i => i != _statusHost && i.Name == "MenuHelp") ??
                mainMenu.Items.Cast<ToolStripItem>()
                .FirstOrDefault(i => i != _statusHost &&
                                     (i.Text ?? "").IndexOf("HELP", StringComparison.OrdinalIgnoreCase) >= 0);

            if (help != null && help.Visible)
            {
                return mainMenu.Items.IndexOf(help) + 1;
            }

            var sitl = mainMenu.Items.Cast<ToolStripItem>()
                .FirstOrDefault(i => i != _statusHost && (i.Name == "MenuSimulation" || i.Name == "MenuSITL")) ??
                mainMenu.Items.Cast<ToolStripItem>()
                .FirstOrDefault(i => i != _statusHost &&
                                     ((i.Text ?? "").IndexOf("SITL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      (i.Text ?? "").IndexOf("SIMULATION", StringComparison.OrdinalIgnoreCase) >= 0));

            if (sitl != null)
            {
                return mainMenu.Items.IndexOf(sitl) + 1;
            }

            // 3. Fallback: place after the last visible left-aligned item
            int lastLeftIdx = -1;
            for (int i = 0; i < mainMenu.Items.Count; i++)
            {
                var item = mainMenu.Items[i];
                if (item != _statusHost && item.Alignment == ToolStripItemAlignment.Left && item.Visible)
                {
                    lastLeftIdx = i;
                }
            }

            return lastLeftIdx >= 0 ? lastLeftIdx + 1 : mainMenu.Items.Count;
        }

        private class StatusWidgetPanel : Panel
        {
            private string _line1 = "";
            private string _line2 = "";
            private Color _line1Color = Color.Silver;
            private Color _line2Color = Color.Gray;

            public StatusWidgetPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.SupportsTransparentBackColor |
                         ControlStyles.ResizeRedraw, true);
                UpdateStyles();
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
            }

            public override Color BackColor
            {
                get => Color.Transparent;
                set => base.BackColor = Color.Transparent;
            }

            public void UpdateContent(string line1, Color color1, string line2, Color color2)
            {
                if (_line1 != line1 || _line1Color != color1 || _line2 != line2 || _line2Color != color2)
                {
                    _line1 = line1;
                    _line1Color = color1;
                    _line2 = line2;
                    _line2Color = color2;
                    Invalidate();
                }
            }

            public Size MeasurePreferredSize()
            {
                if (Font == null) return new Size(140, 32);
                int w1 = TextRenderer.MeasureText(_line1, Font).Width;
                int w2 = TextRenderer.MeasureText(_line2, Font).Width;
                int w = Math.Max(w1, w2);
                int h = Math.Max(32, 1 + 2 * Font.Height + 1);
                return new Size(w, h);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                if (Font == null) return;

                int lineHeight = Font.Height;
                var rect1 = new Rectangle(0, 1, Width, lineHeight);
                var rect2 = new Rectangle(0, 1 + lineHeight, Width, lineHeight);

                TextRenderer.DrawText(e.Graphics, _line1 ?? "", Font, rect1, _line1Color,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, _line2 ?? "", Font, rect2, _line2Color,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            }
        }

        // --- Helpers ---
        private static string FormatBytes(long b) => b >= 1048576 ? $"{b / 1048576.0:F1} MB" : (b >= 1024 ? $"{b / 1024.0:F1} KB" : $"{b} B");
        private static string FormatRate(int b) => b >= 1024 ? $"{b / 1024.0:F1} KB/s" : $"{b} B/s";

        private static Uri ParseNtrip(string url) =>
            Uri.TryCreate(url?.Replace("ntrip://", "http://"), UriKind.Absolute, out var u) ? u : null;

        private static string GetNtripMountpoint(string url)
        {
            var u = ParseNtrip(url);
            if (u == null) return "NTRIP";
            var mount = u.AbsolutePath.TrimStart('/');
            return mount.Length > 0 ? mount : u.Host;
        }

        private static string SanitizeUrl(string url)
        {
            var u = ParseNtrip(url);
            if (u == null) return Regex.Replace(url ?? "", @":[^@/]+@", ":***@");
            var user = u.UserInfo.Length > 0 ? u.UserInfo.Split(':')[0] + ":***@" : "";
            return $"{u.Scheme}://{user}{u.Authority}{u.PathAndQuery}";
        }
    }
}
