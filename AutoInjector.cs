using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MissionPlanner.Comms;
using MissionPlanner.Controls;
using MissionPlanner.GCSViews;
using MissionPlanner.GCSViews.ConfigurationView;
using MissionPlanner.Plugin;
using MissionPlanner.Utilities;

namespace AutoInjector
{
    public class AutoInjector : Plugin
    {
        public override string Name => "Auto Injector (RTCM3)";
        public override string Version => "v0.3.2";
        public override string Author => "Yuri Rage";

        public override float loopratehz { get; set; } = 1.0f;

        private static ConfigSerialInjectGPS _lastHookedInstance = null;
        private static (double Lat, double Lng, double Alt)? _fallbackBasePos = null;
        private static StatusLabel _statusLabel = null;
        private static ToolTip _statusToolTip = null;
        private static int _lastTotalBytes = 0;
        private static bool _panelDoubleBuffered = false;

        private static string _lastStatusText = null;
        private static Color _lastStatusColor = Color.Empty;
        private static string _lastToolTipText = null;

        /// <summary>
        /// Custom double-buffered label that completely eliminates WinForms redraw flicker
        /// by handling painting off-screen and suppressing WM_ERASEBKGND.
        /// </summary>
        private class StatusLabel : Label
        {
            public StatusLabel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Solid fill matching parent panel prevents parent invalidation
                using (var brush = new SolidBrush(this.BackColor))
                {
                    e.Graphics.FillRectangle(brush, this.ClientRectangle);
                }

                // Offset text rectangle to align baseline precisely with neighboring UxTheme CheckBoxes
                var textRect = new Rectangle(0, -2, this.Width, this.Height);

                TextRenderer.DrawText(
                    e.Graphics,
                    this.Text,
                    this.Font,
                    textRect,
                    this.ForeColor,
                    this.BackColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                );
            }
        }

        public override bool Init() => true;

        public override bool Exit()
        {
            Host?.MainForm?.BeginInvokeIfRequired(() =>
            {
                if (_statusLabel != null && !_statusLabel.IsDisposed)
                {
                    _statusLabel.Parent?.Controls.Remove(_statusLabel);
                    _statusLabel.Dispose();
                    _statusLabel = null;
                }
            });
            return true;
        }

        public override bool Loaded()
        {
            Host.MainForm.BeginInvokeIfRequired(() =>
            {
                _ = ConnectToLastConfiguredSource();
                UpdateFlightDataStatusBar();
            });
            return true;
        }

        public override bool Loop()
        {
            SyncUiInstance();

            Host.MainForm.BeginInvokeIfRequired(() =>
            {
                UpdateFlightDataStatusBar();
            });

            return true;
        }

        /// <summary>
        /// Inserts or updates an interactive RTCM3 stream status indicator in FlightData's bottom panel
        /// alongside coords, tuning, and autopan checkboxes.
        /// </summary>
        private void UpdateFlightDataStatusBar()
        {
            try
            {
                var flightData = FlightData.instance ?? Host.MainForm?.FlightData;
                if (flightData == null || flightData.IsDisposed || !flightData.Visible)
                    return;

                // 1. Resolve panel1 via direct reflection
                var panel1Field = typeof(FlightData).GetField("panel1", BindingFlags.Instance | BindingFlags.NonPublic);
                var panel1 = panel1Field?.GetValue(flightData) as Panel;
                if (panel1 == null || panel1.IsDisposed)
                {
                    panel1 = flightData.Controls.Find("panel1", true).FirstOrDefault() as Panel;
                    if (panel1 == null || panel1.IsDisposed)
                        return;
                }

                // Enable DoubleBuffering on panel1 to eliminate background erase-repaint flicker
                if (!_panelDoubleBuffered)
                {
                    typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(panel1, true, null);
                    _panelDoubleBuffered = true;
                }

                // 2. Resolve CHK_autopan to anchor next to it
                var autopanField = typeof(FlightData).GetField("CHK_autopan", BindingFlags.Instance | BindingFlags.NonPublic);
                var autopan = autopanField?.GetValue(flightData) as CheckBox;
                if (autopan == null)
                    autopan = panel1.Controls.Find("CHK_autopan", true).FirstOrDefault() as CheckBox;

                int targetX = autopan != null ? autopan.Right + 12 : 395;
                int targetY = autopan != null ? autopan.Top : 3;
                int targetHeight = autopan != null ? autopan.Height : 17;
                var targetLocation = new Point(targetX, targetY);

                // 3. Create or attach custom DoubleBuffered StatusLabel
                if (_statusLabel == null || _statusLabel.IsDisposed || _statusLabel.Parent != panel1)
                {
                    _statusLabel = panel1.Controls.OfType<StatusLabel>().FirstOrDefault();

                    if (_statusLabel == null)
                    {
                        // Fixed size prevents layout recalculation on panel1 / tableMap
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
                            Font = autopan != null ? autopan.Font : new Font(panel1.Font.FontFamily, 8.25f, FontStyle.Regular)
                        };

                        _statusLabel.Click += (s, e) =>
                        {
                            try
                            {
                                new ConfigSerialInjectGPS().Show();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[AutoInjector] Error displaying RTK window: {ex.Message}");
                            }
                        };

                        _statusToolTip = new ToolTip();
                        panel1.Controls.Add(_statusLabel);
                        _statusLabel.BringToFront();
                    }
                }

                // Match font and height dynamically with autopan
                if (autopan != null)
                {
                    if (_statusLabel.Height != autopan.Height)
                        _statusLabel.Height = autopan.Height;

                    if (!_statusLabel.Font.Equals(autopan.Font))
                        _statusLabel.Font = autopan.Font;
                }

                // Match background color seamlessly with panel1 (e.g. on theme change)
                if (_statusLabel.BackColor != panel1.BackColor)
                    _statusLabel.BackColor = panel1.BackColor;

                // Only move control if location actually changed
                if (_statusLabel.Location != targetLocation)
                {
                    _statusLabel.Location = targetLocation;
                }

                // 4. Query active injector stream statistics
                var comPortField = typeof(ConfigSerialInjectGPS).GetField("comPort", BindingFlags.Static | BindingFlags.NonPublic);
                var currentComPort = comPortField?.GetValue(null) as ICommsSerial;
                bool isPortOpen = currentComPort != null && currentComPort.IsOpen;

                var threadrunField = typeof(ConfigSerialInjectGPS).GetField("threadrun", BindingFlags.Static | BindingFlags.NonPublic);
                bool isThreadRunning = (bool)(threadrunField?.GetValue(null) ?? false);

                var bytesField = typeof(ConfigSerialInjectGPS).GetField("bytes", BindingFlags.Static | BindingFlags.NonPublic);
                int totalBytes = (int)(bytesField?.GetValue(null) ?? 0);

                int rate = Math.Max(0, totalBytes - _lastTotalBytes);
                _lastTotalBytes = totalBytes;

                var msgseenField = typeof(ConfigSerialInjectGPS).GetField("msgseen", BindingFlags.Static | BindingFlags.NonPublic);
                var msgseen = msgseenField?.GetValue(null) as ConcurrentDictionary<string, int>;

                var statusLine3Field = typeof(ConfigSerialInjectGPS).GetField("status_line3", BindingFlags.Static | BindingFlags.NonPublic);
                string basePosInfo = statusLine3Field?.GetValue(null) as string;

                // 5. Inspect stream message types & constellations
                bool hasBase = false;
                var constellations = new List<string>();
                var activeMessages = new List<string>();

                if (msgseen != null && !msgseen.IsEmpty)
                {
                    hasBase = msgseen.Keys.Any(k => k == "Rtcm1005" || k == "Rtcm1006" || k == "Rtcm4072");

                    if (msgseen.Keys.Any(k => k.StartsWith("Rtcm107") || k == "Rtcm1004" || k == "Rtcm1002"))
                        constellations.Add("GPS");
                    if (msgseen.Keys.Any(k => k.StartsWith("Rtcm108") || k == "Rtcm1012"))
                        constellations.Add("GLO");
                    if (msgseen.Keys.Any(k => k.StartsWith("Rtcm109")))
                        constellations.Add("GAL");
                    if (msgseen.Keys.Any(k => k.StartsWith("Rtcm112")))
                        constellations.Add("BDS");

                    foreach (var key in msgseen.Keys.OrderBy(k => k))
                    {
                        activeMessages.Add($"{key.Replace("Rtcm", "")}({msgseen[key]})");
                    }
                }

                // 6. Resolve friendly source name
                string portType = Settings.Instance["SerialInjectGPS_port"] ?? "None";
                string sourceDisplay = portType;

                if (portType == "NTRIP" && Settings.Instance.ContainsKey("NTRIP_url"))
                {
                    sourceDisplay = GetNtripMountpoint(Settings.Instance["NTRIP_url"]);
                }
                else if (portType.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                {
                    string baud = Settings.Instance["SerialInjectGPS_baud"] ?? "115200";
                    sourceDisplay = $"{portType} ({baud})";
                }

                // 7. Calculate new text, color, and tooltip
                string newText;
                Color newColor;
                string newTooltip;

                if (!isPortOpen || !isThreadRunning)
                {
                    newText = "RTCM3: Disconnected";
                    newColor = Color.Silver;
                    newTooltip = $"Correction stream is not active.\nLast configured source: {portType}\nClick to open RTK/GPS Inject settings.";
                }
                else if (totalBytes == 0 || (rate == 0 && msgseen != null && msgseen.IsEmpty))
                {
                    newText = $"RTCM3: {sourceDisplay} • Connecting...";
                    newColor = Color.Gold;
                    newTooltip = $"Source: {sourceDisplay}\nStatus: Socket open, waiting for incoming RTCM3 packets...\nClick to open RTK/GPS Inject settings.";
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
                        sb.AppendLine($"  URL: {Settings.Instance["NTRIP_url"]}");
                        sb.AppendLine($"  Protocol: {(IsNtripV1Enabled() ? "NTRIP v1.0" : "NTRIP v2.0")} | GGA Output: {(IsGgaEnabled() ? "Enabled" : "Disabled")}");
                    }
                    sb.AppendLine($"  Throughput: {FormatRate(rate)} (Total: {FormatBytes(totalBytes)})");
                    if (constellations.Count > 0)
                        sb.AppendLine($"  Constellations: {string.Join(", ", constellations)}");
                    if (activeMessages.Count > 0)
                        sb.AppendLine($"  Messages: {string.Join(" ", activeMessages.Take(8))}");
                    if (!string.IsNullOrWhiteSpace(basePosInfo))
                        sb.AppendLine($"  Base Position: {basePosInfo}");
                    sb.Append("Click to open RTK/GPS Inject settings.");

                    newTooltip = sb.ToString();
                }

                // 8. Change-detection guard: only write WinForms properties if values meaningfully changed
                if (_lastStatusText != newText)
                {
                    _lastStatusText = newText;
                    _statusLabel.Text = newText;
                }

                if (_lastStatusColor != newColor)
                {
                    _lastStatusColor = newColor;
                    _statusLabel.ForeColor = newColor;
                }

                if (_lastToolTipText != newTooltip)
                {
                    _lastToolTipText = newTooltip;
                    _statusToolTip?.SetToolTip(_statusLabel, newTooltip);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoInjector] Error updating status bar: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1048576)
                return $"{bytes / 1048576.0:F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        private static string FormatRate(int bytesPerSec)
        {
            if (bytesPerSec >= 1024)
                return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec} B/s";
        }

        private static string GetNtripMountpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "NTRIP";
            try
            {
                var uri = new Uri(url.Replace("ntrip://", "http://"));
                string mount = uri.AbsolutePath.TrimStart('/');
                return string.IsNullOrEmpty(mount) ? uri.Host : mount;
            }
            catch
            {
                int slash = url.LastIndexOf('/');
                if (slash >= 0 && slash < url.Length - 1)
                    return url.Substring(slash + 1);
                return "NTRIP";
            }
        }

        /// <summary>
        /// Synchronizes and persists user checkbox selections on the ConfigSerialInjectGPS UI form
        /// for both Send GGA and NTRIP v1.0.
        /// </summary>
        private static void SyncUiInstance()
        {
            try
            {
                var instanceField = typeof(ConfigSerialInjectGPS).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic);
                var activeInstance = instanceField?.GetValue(null) as ConfigSerialInjectGPS;

                if (activeInstance != null && activeInstance != _lastHookedInstance && !activeInstance.IsDisposed)
                {
                    _lastHookedInstance = activeInstance;

                    // 1. Sync & Hook Send GGA checkbox
                    var ggaField = typeof(ConfigSerialInjectGPS).GetField("chk_sendgga", BindingFlags.Instance | BindingFlags.NonPublic);
                    var chkGga = ggaField?.GetValue(activeInstance) as CheckBox;

                    if (chkGga != null && !chkGga.IsDisposed)
                    {
                        if (Settings.Instance.ContainsKey("SerialInjectGPS_sendgga") &&
                            bool.TryParse(Settings.Instance["SerialInjectGPS_sendgga"], out bool savedGga))
                        {
                            if (chkGga.InvokeRequired)
                                chkGga.BeginInvoke((MethodInvoker)(() => chkGga.Checked = savedGga));
                            else
                                chkGga.Checked = savedGga;
                        }

                        chkGga.CheckedChanged += (s, e) =>
                        {
                            Settings.Instance["SerialInjectGPS_sendgga"] = chkGga.Checked.ToString();
                        };
                    }

                    // 2. Sync & Hook NTRIP v1.0 checkbox
                    var v1Field = typeof(ConfigSerialInjectGPS).GetField("check_sendntripv1", BindingFlags.Instance | BindingFlags.NonPublic);
                    var chkV1 = v1Field?.GetValue(activeInstance) as CheckBox;

                    if (chkV1 != null && !chkV1.IsDisposed)
                    {
                        if (Settings.Instance.ContainsKey("SerialInjectGPS_ntripv1") &&
                            bool.TryParse(Settings.Instance["SerialInjectGPS_ntripv1"], out bool savedV1))
                        {
                            if (chkV1.InvokeRequired)
                                chkV1.BeginInvoke((MethodInvoker)(() => chkV1.Checked = savedV1));
                            else
                                chkV1.Checked = savedV1;
                        }

                        chkV1.CheckedChanged += (s, e) =>
                        {
                            Settings.Instance["SerialInjectGPS_ntripv1"] = chkV1.Checked.ToString();
                        };
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsGgaEnabled()
        {
            try
            {
                var instanceField = typeof(ConfigSerialInjectGPS).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic);
                var activeInstance = instanceField?.GetValue(null) as ConfigSerialInjectGPS;

                if (activeInstance != null && !activeInstance.IsDisposed)
                {
                    var ggaField = typeof(ConfigSerialInjectGPS).GetField("chk_sendgga", BindingFlags.Instance | BindingFlags.NonPublic);
                    var chk = ggaField?.GetValue(activeInstance) as CheckBox;

                    if (chk != null && !chk.IsDisposed)
                    {
                        if (chk.InvokeRequired)
                        {
                            try
                            {
                                return (bool)chk.Invoke(new Func<bool>(() => chk.Checked));
                            }
                            catch
                            {
                                return false;
                            }
                        }
                        return chk.Checked;
                    }
                }
            }
            catch
            {
            }

            if (Settings.Instance.ContainsKey("SerialInjectGPS_sendgga") &&
                bool.TryParse(Settings.Instance["SerialInjectGPS_sendgga"], out bool savedConfig))
            {
                return savedConfig;
            }

            return true;
        }

        private static bool IsNtripV1Enabled()
        {
            try
            {
                var instanceField = typeof(ConfigSerialInjectGPS).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic);
                var activeInstance = instanceField?.GetValue(null) as ConfigSerialInjectGPS;

                if (activeInstance != null && !activeInstance.IsDisposed)
                {
                    var v1Field = typeof(ConfigSerialInjectGPS).GetField("check_sendntripv1", BindingFlags.Instance | BindingFlags.NonPublic);
                    var chk = v1Field?.GetValue(activeInstance) as CheckBox;

                    if (chk != null && !chk.IsDisposed)
                    {
                        if (chk.InvokeRequired)
                        {
                            try
                            {
                                return (bool)chk.Invoke(new Func<bool>(() => chk.Checked));
                            }
                            catch
                            {
                                return false;
                            }
                        }
                        return chk.Checked;
                    }
                }
            }
            catch
            {
            }

            if (Settings.Instance.ContainsKey("SerialInjectGPS_ntripv1") &&
                bool.TryParse(Settings.Instance["SerialInjectGPS_ntripv1"], out bool savedConfig))
            {
                return savedConfig;
            }

            return false;
        }

        private async Task<bool> ConnectToLastConfiguredSource()
        {
            if (!Host.config.ContainsKey("SerialInjectGPS_port"))
                return false;

            // Guard against already active connection
            var comPortField = typeof(ConfigSerialInjectGPS).GetField("comPort", BindingFlags.Static | BindingFlags.NonPublic);
            var currentComPort = comPortField?.GetValue(null) as ICommsSerial;
            if (currentComPort != null && currentComPort.IsOpen)
            {
                Console.WriteLine("[AutoInjector] RTK/GPS injection is already connected and active.");
                return true;
            }

            // Resolve or instantiate UI instance
            var instanceField = typeof(ConfigSerialInjectGPS).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic);
            var activeInstance = instanceField?.GetValue(null) as ConfigSerialInjectGPS;
            var inject = activeInstance ?? new ConfigSerialInjectGPS();

            SyncUiInstance();

            string portType = Host.config["SerialInjectGPS_port"];

            try
            {
                if (portType == "NTRIP" && Host.config.ContainsKey("NTRIP_url"))
                {
                    string url = Host.config["NTRIP_url"];
                    if (string.IsNullOrWhiteSpace(url))
                        return false;

                    var ntrip = new CommsNTRIP();

                    // Apply persistent NTRIP v1.0 configuration
                    ntrip.ntrip_v1 = IsNtripV1Enabled();

                    // Parse base_pos for fallback when vehicle doesn't have 3D fix
                    if (Host.config.ContainsKey("base_pos"))
                    {
                        string[] parts = Host.config["base_pos"].Split(',');
                        if (parts.Length >= 3 &&
                            double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                            double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lng) &&
                            double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double alt))
                        {
                            _fallbackBasePos = (lat, lng, alt);
                        }
                    }

                    // Leave lat/lng on the CommsNTRIP instance as 0 so CommsNTRIP's internal
                    // fallback cannot send GGA unless GgaSource explicitly authorizes it.
                    ntrip.lat = 0;
                    ntrip.lng = 0;
                    ntrip.alt = 0;

                    // GgaSource is evaluated dynamically on every 30-second interval
                    ntrip.GgaSource = () =>
                    {
                        if (!IsGgaEnabled())
                        {
                            return new CommsNTRIP.GgaInfo { Lat = 0, Lng = 0 };
                        }

                        var cs = Host.cs;
                        if (cs != null && cs.gpsstatus >= 3 && (cs.lat != 0 || cs.lng != 0))
                        {
                            return new CommsNTRIP.GgaInfo
                            {
                                Lat = cs.lat,
                                Lng = cs.lng,
                                Alt = cs.alt,
                                Quality = cs.gpsstatus == 4 ? 2 : (cs.gpsstatus >= 5 ? 5 : 1),
                                Sats = (int)cs.satcount,
                                Hdop = cs.gpshdop
                            };
                        }

                        if (_fallbackBasePos.HasValue && (_fallbackBasePos.Value.Lat != 0 || _fallbackBasePos.Value.Lng != 0))
                        {
                            return new CommsNTRIP.GgaInfo
                            {
                                Lat = _fallbackBasePos.Value.Lat,
                                Lng = _fallbackBasePos.Value.Lng,
                                Alt = _fallbackBasePos.Value.Alt,
                                Quality = 1,
                                Sats = 10,
                                Hdop = 1.0
                            };
                        }

                        return new CommsNTRIP.GgaInfo { Lat = 0, Lng = 0 };
                    };

                    ntrip.Open(url);
                    comPortField?.SetValue(null, ntrip);

                    await inject.DoConnect();
                    return true;
                }
                else if (portType.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                         portType == "TCP Client" ||
                         portType.StartsWith("UDP"))
                {
                    await inject.DoConnect();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoInjector] Failed to auto-connect RTCM source: {ex.Message}");
                return false;
            }

            return false;
        }
    }
}
