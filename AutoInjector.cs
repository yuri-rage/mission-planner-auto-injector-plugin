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
using MissionPlanner.GCSViews;
using MissionPlanner.GCSViews.ConfigurationView;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;

namespace AutoInjector
{
    public class AutoInjector : Plugin
    {
        public override string Name => "Auto Injector (RTCM3)";
        public override string Version => "v0.2.0";
        public override string Author => "Yuri Rage";
        public override float loopratehz { get; set; } = 1.0f;

        private static readonly ILog _log = LogManager.GetLogger("AutoInjector");
        private static void Log(string msg) => _log.Info(msg);

        // --- Cached Reflection ---
        private static readonly Type TInject = typeof(ConfigSerialInjectGPS);
        private static readonly Type TFlight = typeof(FlightData);
        private static FieldInfo F(Type t, string name, bool isStatic) =>
            t.GetField(name, (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo F_comPort = F(TInject, "comPort", true);
        private static readonly FieldInfo F_threadrun = F(TInject, "threadrun", true);
        private static readonly FieldInfo F_bytes = F(TInject, "bytes", true);
        private static readonly FieldInfo F_msgseen = F(TInject, "msgseen", true);
        private static readonly FieldInfo F_statusLine3 = F(TInject, "status_line3", true);
        private static readonly FieldInfo F_injectInstance = F(TInject, "Instance", true);
        private static readonly FieldInfo F_panel1 = F(TFlight, "panel1", false);
        private static readonly FieldInfo F_autopan = F(TFlight, "CHK_autopan", false);

        private static readonly Func<CommsNTRIP.GgaInfo> LiveGgaDelegate = InitLiveGgaDelegate();
        private static Func<CommsNTRIP.GgaInfo> InitLiveGgaDelegate()
        {
            var m = TInject.GetMethod("GetLiveGga", BindingFlags.Static | BindingFlags.NonPublic);
            return m != null ? (Func<CommsNTRIP.GgaInfo>)Delegate.CreateDelegate(typeof(Func<CommsNTRIP.GgaInfo>), m) : null;
        }

        // --- State ---
        private static ConfigSerialInjectGPS _lastHookedInstance;
        private static StatusLabel _statusLabel;
        private static ToolTip _statusToolTip;
        private static bool _panelDoubleBuffered;
        private static int _lastTotalBytes;
        private static int _zeroRateSeconds;
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
                UpdateFlightDataStatusBar();
            });
            return true;
        }

        public override bool Loop()
        {
            RunOnUi(() =>
            {
                SyncUiInstance();
                UpdateFlightDataStatusBar();
            });
            return true;
        }

        public override bool Exit()
        {
            RunOnUi(() =>
            {
                if (_statusLabel != null && !_statusLabel.IsDisposed)
                {
                    _statusLabel.Parent?.Controls.Remove(_statusLabel);
                    _statusLabel.Dispose();
                    _statusLabel = null;
                }
                _statusToolTip?.Dispose();
                _statusToolTip = null;
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
        private void UpdateFlightDataStatusBar()
        {
            try
            {
                var flightData = FlightData.instance ?? Host.MainForm?.FlightData;
                if (flightData == null || flightData.IsDisposed || !flightData.Visible) return;

                var panel1 = F_panel1?.GetValue(flightData) as Panel;
                if (panel1 == null || panel1.IsDisposed) return;

                if (!_panelDoubleBuffered)
                {
                    typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(panel1, true, null);
                    _panelDoubleBuffered = true;
                }

                var autopan = F_autopan?.GetValue(flightData) as CheckBox;
                var targetLocation = new Point(autopan != null ? autopan.Right + 12 : 395, autopan != null ? autopan.Top : 3);
                int targetHeight = autopan != null ? autopan.Height : 17;

                if (_statusLabel == null || _statusLabel.IsDisposed || _statusLabel.Parent != panel1)
                {
                    _statusLabel = panel1.Controls.OfType<StatusLabel>().FirstOrDefault();
                    if (_statusLabel == null)
                    {
                        _statusLabel = new StatusLabel
                        {
                            Name = "lbl_rtcm_status",
                            AutoSize = false,
                            Size = new Size(350, targetHeight),
                            Location = targetLocation,
                            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                            Text = "RTCM3: Initializing...",
                            BackColor = panel1.BackColor,
                            ForeColor = Color.Silver,
                            Cursor = Cursors.Hand,
                            Font = autopan?.Font ?? new Font(panel1.Font.FontFamily, 8.25f, FontStyle.Regular)
                        };

                        _statusLabel.Click += (s, e) => { try { new ConfigSerialInjectGPS().Show(); } catch (Exception ex) { Log(ex.ToString()); } };
                        _statusToolTip = new ToolTip();
                        panel1.Controls.Add(_statusLabel);
                        _statusLabel.BringToFront();
                    }
                }

                if (autopan != null)
                {
                    _statusLabel.Height = autopan.Height;
                    _statusLabel.Font = autopan.Font;
                }
                _statusLabel.BackColor = panel1.BackColor;
                _statusLabel.Location = targetLocation;

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

                string newText;
                Color newColor;
                string newTooltip;

                if (!isPortOpen || !isThreadRunning)
                {
                    newText = "RTCM3: Disconnected";
                    newColor = Color.Silver;
                    newTooltip = $"Correction stream is not active.\nLast configured source: {portType}";
                }
                else if (totalBytes == 0 || (rate == 0 && (msgseen == null || msgseen.IsEmpty)))
                {
                    newText = $"RTCM3: {sourceDisplay} • Connecting...";
                    newColor = Color.Gold;
                    newTooltip = $"Source: {sourceDisplay}\nWaiting for incoming RTCM3 packets...";
                }
                else if (_zeroRateSeconds >= 3)
                {
                    string baseTag = hasBase ? " (Base OK)" : "";
                    newText = $"RTCM3: {sourceDisplay} • Stalled (0 B/s){baseTag}";
                    newColor = Color.DarkOrange;
                    newTooltip = $"Source: {sourceDisplay}\nWarning: No correction data received in {_zeroRateSeconds}s.";
                }
                else
                {
                    string baseTag = hasBase ? " (Base OK)" : "";
                    string constellTag = constellations.Count > 0 ? $" [{string.Join("+", constellations)}]" : "";
                    newText = $"RTCM3: {sourceDisplay} • {FormatRate(rate)}{baseTag}{constellTag}";
                    newColor = hasBase ? Color.Lime : Color.SpringGreen;

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
                _statusLabel.Text = newText;
                _statusLabel.ForeColor = newColor;

                if (_lastToolTipText != newTooltip)
                {
                    _lastToolTipText = newTooltip;
                    _statusToolTip?.SetToolTip(_statusLabel, newTooltip);
                }
            }
            catch (Exception ex)
            {
                Log($"Error updating status bar: {ex}");
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

        private class StatusLabel : Label
        {
            public StatusLabel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                UpdateStyles();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var brush = new SolidBrush(BackColor))
                    e.Graphics.FillRectangle(brush, ClientRectangle);

                var textRect = new Rectangle(0, -2, Width, Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, textRect, ForeColor, BackColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
