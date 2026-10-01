// SimpleMacFan - minimal fan control for Intel Macs running Windows through Boot Camp.
// Copyright (C) 2026 ozaretskyi <ozaretskyi@proton.me>
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details. You should have received a copy of
// the GNU General Public License along with this program. If not, see
// <https://www.gnu.org/licenses/>.
//
// It talks to the Apple SMC through Apple's own Boot Camp driver (MacHALDriver.sys),
// so no extra kernel driver is installed.
//
// Safety model: the normal modes only RAISE each fan's minimum speed (FxMn). They never go
// below Apple's default minimum or above the fan's maximum, and the SMC's own thermal control
// stays active, so it can always spin the fans faster on its own.
// The "Forced" modes take a fan away from the SMC (FS! bit + FxTg target) so it can run
// slower than the SMC wants. Because the SMC no longer protects such a fan, every forced fan
// is handed back to the SMC whenever a critical sensor gets hot or cannot be read.
// Apple's defaults are written back when the app exits or Windows shuts down.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SimpleMacFan
{
    // IOCTL interface of Apple's MacHALDriver (Boot Camp 6.x), all METHOD_BUFFERED:
    //   READ  : in = 4-byte key                        out = N data bytes
    //   WRITE : in = 4-byte key + length byte + data   out = none
    //   INDEX : in = uint32 index                      out = 4-byte key + status
    //   INFO  : in = 4-byte key + 1 byte               out = [0] size, [4..7] type, [8] attributes
    sealed class Smc : IDisposable
    {
        const uint IoctlRead = 0x9C402454, IoctlWrite = 0x9C402458, IoctlInfo = 0x9C402460;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
                                           byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

        readonly SafeFileHandle handle;
        readonly object sync = new object();

        public Smc()
        {
            handle = CreateFile(@"\\.\MacHALDriver", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Cannot open Apple's Boot Camp driver (MacHALDriver). Are the Boot Camp drivers installed?");
        }

        byte[] Call(uint code, byte[] input, int outSize)
        {
            var output = new byte[Math.Max(outSize, 1)];
            int returned;
            lock (sync)
            {
                if (!DeviceIoControl(handle, code, input, input.Length, output, outSize, out returned, IntPtr.Zero))
                    return null;
            }
            return output;
        }

        static byte[] KeyBuffer(string key, int extra)
        {
            var buffer = new byte[4 + extra];
            Encoding.ASCII.GetBytes(key, 0, 4, buffer, 0);
            return buffer;
        }

        public bool GetInfo(string key, out int size, out string type)
        {
            var r = Call(IoctlInfo, KeyBuffer(key, 1), 12);
            size = r == null ? 0 : r[0];
            type = r == null ? null : Encoding.ASCII.GetString(r, 4, 4);
            return r != null && size > 0;
        }

        public byte[] Read(string key)
        {
            int size;
            string type;
            return GetInfo(key, out size, out type) ? Read(key, size) : null;
        }

        public byte[] Read(string key, int size)
        {
            return Call(IoctlRead, KeyBuffer(key, 0), size);
        }

        public bool Write(string key, byte[] data)
        {
            var buffer = KeyBuffer(key, 1 + data.Length);
            buffer[4] = (byte)data.Length;
            Array.Copy(data, 0, buffer, 5, data.Length);
            return Call(IoctlWrite, buffer, 0) != null;
        }

        // fpe2: unsigned 14.2 fixed point, used for fan speeds (rpm).
        public double ReadRpm(string key)
        {
            var r = Read(key, 2);
            return r == null ? double.NaN : ((r[0] << 8) | r[1]) / 4.0;
        }

        public bool WriteRpm(string key, int rpm)
        {
            int raw = rpm * 4;
            return Write(key, new[] { (byte)(raw >> 8), (byte)raw });
        }

        // sp78: signed 8.8 fixed point, used for temperatures (degrees C).
        public double ReadTemp(string key)
        {
            var r = Read(key, 2);
            return r == null ? double.NaN : (short)((r[0] << 8) | r[1]) / 256.0;
        }

        public void Dispose()
        {
            handle.Dispose();
        }
    }

    enum FanMode { Auto, Fixed, Sensor, Forced, ForcedSensor }

    sealed class Fan
    {
        public int Index;
        public string Name;
        public int AppleMin;
        public int Max;
        public FanMode Mode;
        public int FixedRpm;
        public string SensorKey;
        public int FromC;
        public int ToC;
        public int LastTarget = -1;

        public bool IsForced
        {
            get { return Mode == FanMode.Forced || Mode == FanMode.ForcedSensor; }
        }

        public string Key(string suffix)
        {
            return "F" + Index + suffix;
        }
    }

    sealed class SensorItem
    {
        public string Key;
        public string Name;

        public override string ToString()
        {
            return Name;
        }
    }

    sealed class Settings
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Settings(string path)
        {
            this.path = path;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0)
                        values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public bool Has(string key)
        {
            return values.ContainsKey(key);
        }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public void Set(string key, object value)
        {
            values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                foreach (var kv in values)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(path, sb.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class FanRow
    {
        public Fan Fan;
        public Label Speed;
        public ComboBox Mode;
        public NumericUpDown FixedRpm;
        public ComboBox Sensor;
        public NumericUpDown From;
        public NumericUpDown To;
    }

    sealed class MainForm : Form
    {
        public const string AppName = "SimpleMacFan";
        const string HotterCpuGpu = "CPU+GPU";
        const int MaxStepDown = 150; // rpm per tick, so fans slow down smoothly
        const string ForceKey = "FS! ";
        const double SafetyTripC = 85, GpuTripC = 90, SafetyHysteresisC = 10;

        static readonly string[][] KnownSensors =
        {
            new[] { "TCXc", "CPU (hottest core)" },
            new[] { "TC0D", "CPU die" },
            new[] { "TC0P", "CPU proximity" },
            new[] { "TC0H", "CPU heatsink" },
            new[] { "TG0D", "GPU die" },
            new[] { "TG0P", "GPU proximity" },
            new[] { "TG0H", "GPU heatsink" },
            new[] { "TPCD", "Platform controller hub" },
            new[] { "Tm0P", "Memory / mainboard" },
            new[] { "TO0P", "Optical drive" },
            new[] { "TH0P", "Hard drive" },
            new[] { "Tp2H", "Power supply heatsink" },
            new[] { "Tp1P", "Power supply" },
            new[] { "TL0P", "LCD panel" },
            new[] { "TA0P", "Ambient air" },
        };

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        readonly Smc smc;
        readonly Settings settings;
        readonly List<Fan> fans = new List<Fan>();
        readonly List<FanRow> rows = new List<FanRow>();
        readonly List<SensorItem> sensors = new List<SensorItem>();
        readonly Dictionary<string, double> temps = new Dictionary<string, double>();
        readonly Dictionary<string, ListViewItem> tempItems = new Dictionary<string, ListViewItem>();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly CheckBox autostart = new CheckBox();
        readonly Label status = new Label();
        readonly List<string> safetyKeys = new List<string>();
        string cpuKey, gpuKey, trayIconText;
        Icon trayIcon;
        bool allowVisible, exiting, restored, loading, trayHintShown, canForce, safetyTripped;

        public MainForm(bool startHidden)
        {
            allowVisible = !startHidden;
            smc = new Smc();
            settings = new Settings(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "settings.ini"));

            DiscoverSensors();
            DiscoverFans();
            BuildUi();

            AppDomain.CurrentDomain.ProcessExit += delegate { RestoreDefaults(); };
            SystemEvents.SessionEnding += delegate { RestoreDefaults(); };

            timer.Interval = 2000;
            timer.Tick += delegate { UpdateFans(); };
            timer.Start();
            UpdateFans();
        }

        void DiscoverSensors()
        {
            foreach (var known in KnownSensors)
            {
                double t = smc.ReadTemp(known[0]);
                if (t > 0 && t < 130)
                    sensors.Add(new SensorItem { Key = known[0], Name = known[1] });
            }
            cpuKey = FirstPresent("TCXc", "TC0D", "TC0P", "TC0H");
            gpuKey = FirstPresent("TG0D", "TG0P", "TG0H");
            if (cpuKey != null && gpuKey != null)
                sensors.Insert(0, new SensorItem { Key = HotterCpuGpu, Name = "CPU or GPU (whichever is hotter)" });

            // Sensors watched by the safety cut-out for forced fans.
            foreach (var key in new[] { cpuKey, gpuKey, FirstPresent("TPCD"), FirstPresent("Tm0P"), FirstPresent("Tp2H") })
                if (key != null)
                    safetyKeys.Add(key);
        }

        string FirstPresent(params string[] keys)
        {
            foreach (var key in keys)
                foreach (var s in sensors)
                    if (s.Key == key)
                        return key;
            return null;
        }

        void DiscoverFans()
        {
            var count = smc.Read("FNum", 1);
            if (count == null || count[0] == 0)
                throw new InvalidOperationException("The SMC reports no fans.");
            canForce = smc.Read(ForceKey, 2) != null && cpuKey != null;

            for (int i = 0; i < count[0]; i++)
            {
                var fan = new Fan { Index = i, Name = "Fan " + (i + 1) };
                var id = smc.Read(fan.Key("ID"));
                if (id != null && id.Length > 4)
                {
                    string name = Encoding.ASCII.GetString(id, 4, id.Length - 4).Trim('\0', ' ');
                    if (name.Length > 0)
                        fan.Name = name;
                }
                fan.Max = (int)smc.ReadRpm(fan.Key("Mx"));

                // Remember Apple's default minimum the first time we see this fan, so that a
                // value raised by this app is never mistaken for the default later.
                string p = "fan" + i + ".";
                fan.AppleMin = settings.GetInt(p + "applemin", (int)smc.ReadRpm(fan.Key("Mn")));
                settings.Set(p + "applemin", fan.AppleMin);

                bool isCpu = fan.Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isGpu = fan.Name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             fan.Name.Equals("ODD", StringComparison.OrdinalIgnoreCase);
                FanMode defaultMode = (isCpu || isGpu) && cpuKey != null ? FanMode.Sensor : FanMode.Auto;
                string defaultSensor = isGpu && gpuKey != null ? HotterCpuGpu : cpuKey;

                FanMode mode;
                fan.Mode = Enum.TryParse(settings.Get(p + "mode", ""), out mode) ? mode : defaultMode;
                if (fan.IsForced && !canForce)
                    fan.Mode = FanMode.Auto;
                fan.FixedRpm = settings.GetInt(p + "fixed", fan.AppleMin);
                fan.SensorKey = settings.Get(p + "sensor", defaultSensor);
                fan.FromC = settings.GetInt(p + "from", isGpu ? 55 : 50);
                fan.ToC = settings.GetInt(p + "to", isGpu ? 85 : 80);
                fans.Add(fan);
            }
            settings.Save();
        }

        void BuildUi()
        {
            Text = AppName;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 470);
            loading = true;

            var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6), ColumnCount = 8 };
            string[] headers = { "Fan", "Speed", "Range (rpm)", "Mode", "Fixed rpm", "Sensor", "From \u00B0C", "To \u00B0C" };
            var bold = new Font(Font, FontStyle.Bold);
            for (int c = 0; c < headers.Length; c++)
                table.Controls.Add(new Label { Text = headers[c], AutoSize = true, Font = bold, Margin = new Padding(3, 3, 9, 6) }, c, 0);

            for (int i = 0; i < fans.Count; i++)
            {
                var fan = fans[i];
                var row = new FanRow { Fan = fan };
                row.Speed = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(75, 0) };
                row.Mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
                row.Mode.Items.AddRange(new object[] { "Auto (Apple default)", "Fixed minimum", "Follow sensor" });
                if (canForce)
                    row.Mode.Items.AddRange(new object[] { "Forced speed", "Forced, follow sensor" });
                row.Mode.SelectedIndex = (int)fan.Mode;
                row.FixedRpm = new NumericUpDown { Minimum = fan.AppleMin, Maximum = Math.Max(fan.Max, fan.AppleMin), Increment = 100, Width = 75 };
                SetNumber(row.FixedRpm, fan.FixedRpm);
                row.Sensor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
                foreach (var s in sensors)
                {
                    row.Sensor.Items.Add(s);
                    if (s.Key == fan.SensorKey)
                        row.Sensor.SelectedItem = s;
                }
                if (row.Sensor.SelectedIndex < 0 && row.Sensor.Items.Count > 0)
                    row.Sensor.SelectedIndex = 0;
                row.From = new NumericUpDown { Minimum = 20, Maximum = 99, Width = 55 };
                row.To = new NumericUpDown { Minimum = 21, Maximum = 100, Width = 55 };
                SetNumber(row.From, fan.FromC);
                SetNumber(row.To, fan.ToC);

                int r = i + 1;
                table.Controls.Add(new Label { Text = fan.Name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
                table.Controls.Add(row.Speed, 1, r);
                table.Controls.Add(new Label { Text = fan.AppleMin + " \u2013 " + fan.Max, AutoSize = true, Anchor = AnchorStyles.Left }, 2, r);
                table.Controls.Add(row.Mode, 3, r);
                table.Controls.Add(row.FixedRpm, 4, r);
                table.Controls.Add(row.Sensor, 5, r);
                table.Controls.Add(row.From, 6, r);
                table.Controls.Add(row.To, 7, r);

                FanRow captured = row;
                EventHandler changed = delegate { OnRowChanged(captured); };
                row.Mode.SelectedIndexChanged += changed;
                row.FixedRpm.ValueChanged += changed;
                row.Sensor.SelectedIndexChanged += changed;
                row.From.ValueChanged += changed;
                row.To.ValueChanged += changed;
                rows.Add(row);
                UpdateEnabled(row);
            }

            var fanGroup = new GroupBox { Text = "Fans", Dock = DockStyle.Top, Padding = new Padding(8) };
            fanGroup.Controls.Add(table);
            fanGroup.Height = table.PreferredSize.Height + 30;

            var tempList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            tempList.Columns.Add("Temperature sensor", 300);
            tempList.Columns.Add("\u00B0C", 80, HorizontalAlignment.Right);
            foreach (var s in sensors)
            {
                if (s.Key == HotterCpuGpu)
                    continue;
                var item = new ListViewItem(new[] { s.Name + "  (" + s.Key + ")", "" });
                tempList.Items.Add(item);
                tempItems[s.Key] = item;
            }
            var tempGroup = new GroupBox { Text = "Temperatures", Dock = DockStyle.Fill, Padding = new Padding(8) };
            tempGroup.Controls.Add(tempList);

            autostart.Text = "Start with Windows (in the tray)";
            autostart.AutoSize = true;
            autostart.Dock = DockStyle.Left;
            autostart.Checked = IsAutostartEnabled();
            autostart.CheckedChanged += delegate { if (!loading) SetAutostart(autostart.Checked); };
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleRight;
            status.ForeColor = SystemColors.GrayText;
            status.Text = "Fans only ever run faster than Apple's default unless forced. Defaults are restored on exit.";
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(10, 6, 10, 6) };
            bottom.Controls.Add(status);
            bottom.Controls.Add(autostart);

            // Docking is laid out in reverse order of addition: Fill must be added first.
            Controls.Add(tempGroup);
            Controls.Add(fanGroup);
            Controls.Add(bottom);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open " + AppName, null, delegate { ShowFromTray(); });
            menu.Items.Add("Exit (restore Apple defaults)", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.Text = AppName;
            tray.Visible = true;
            Icon = SystemIcons.Application;
            loading = false;
        }

        static void SetNumber(NumericUpDown box, int value)
        {
            box.Value = Math.Max(box.Minimum, Math.Min(box.Maximum, value));
        }

        void UpdateEnabled(FanRow row)
        {
            var mode = row.Fan.Mode;
            row.FixedRpm.Enabled = mode == FanMode.Fixed || mode == FanMode.Forced;
            row.Sensor.Enabled = row.From.Enabled = row.To.Enabled = mode == FanMode.Sensor || mode == FanMode.ForcedSensor;
        }

        void OnRowChanged(FanRow row)
        {
            if (loading)
                return;
            if (row.To.Value <= row.From.Value)
            {
                loading = true;
                SetNumber(row.To, (int)row.From.Value + 1);
                SetNumber(row.From, (int)row.To.Value - 1);
                loading = false;
            }

            var fan = row.Fan;
            fan.Mode = (FanMode)Math.Max(0, row.Mode.SelectedIndex);
            fan.FixedRpm = (int)row.FixedRpm.Value;
            var sensor = row.Sensor.SelectedItem as SensorItem;
            if (sensor != null)
                fan.SensorKey = sensor.Key;
            fan.FromC = (int)row.From.Value;
            fan.ToC = (int)row.To.Value;
            UpdateEnabled(row);

            string p = "fan" + fan.Index + ".";
            settings.Set(p + "mode", fan.Mode);
            settings.Set(p + "fixed", fan.FixedRpm);
            settings.Set(p + "sensor", fan.SensorKey);
            settings.Set(p + "from", fan.FromC);
            settings.Set(p + "to", fan.ToC);
            settings.Save();
            UpdateFans();
        }

        double Temp(string key)
        {
            double v;
            if (key == null || !temps.TryGetValue(key, out v) || v <= 0 || v >= 130)
                return double.NaN;
            return v;
        }

        double SensorTemp(string key)
        {
            return key == HotterCpuGpu ? Math.Max(Temp(cpuKey), Temp(gpuKey)) : Temp(key);
        }

        // Minimum speed (normal modes) or exact speed (forced modes) the fan should run at.
        int Target(Fan fan)
        {
            switch (fan.Mode)
            {
                case FanMode.Fixed:
                case FanMode.Forced:
                    return Math.Max(fan.AppleMin, Math.Min(fan.Max, fan.FixedRpm));
                case FanMode.Sensor:
                case FanMode.ForcedSensor:
                    double t = SensorTemp(fan.SensorKey);
                    if (double.IsNaN(t))
                        return fan.Max; // unreadable sensor: fail safe
                    if (t <= fan.FromC)
                        return fan.AppleMin;
                    if (t >= fan.ToC)
                        return fan.Max;
                    return (int)(fan.AppleMin + (fan.Max - fan.AppleMin) * (t - fan.FromC) / (fan.ToC - fan.FromC));
                default:
                    return fan.AppleMin;
            }
        }

        void UpdateFans()
        {
            if (restored)
                return;
            try
            {
                foreach (var s in sensors)
                    if (s.Key != HotterCpuGpu)
                        temps[s.Key] = smc.ReadTemp(s.Key);

                UpdateSafety();

                var tip = new StringBuilder();
                int forceMask = 0;
                foreach (var row in rows)
                {
                    var fan = row.Fan;
                    bool forced = fan.IsForced && !safetyTripped;
                    int target = Target(fan);
                    if (fan.LastTarget >= 0 && target < fan.LastTarget - MaxStepDown)
                        target = fan.LastTarget - MaxStepDown;
                    fan.LastTarget = target;

                    // Re-check every tick: the SMC resets these values after sleep or an SMC reset.
                    int wantedMin = forced ? fan.AppleMin : target;
                    double currentMin = smc.ReadRpm(fan.Key("Mn"));
                    if (double.IsNaN(currentMin) || Math.Abs(currentMin - wantedMin) >= 10)
                        smc.WriteRpm(fan.Key("Mn"), wantedMin);
                    if (forced)
                    {
                        forceMask |= 1 << fan.Index;
                        double currentTarget = smc.ReadRpm(fan.Key("Tg"));
                        if (double.IsNaN(currentTarget) || Math.Abs(currentTarget - target) >= 10)
                            smc.WriteRpm(fan.Key("Tg"), target);
                    }

                    double actual = smc.ReadRpm(fan.Key("Ac"));
                    row.Speed.Text = double.IsNaN(actual) ? "?" : Math.Round(actual) + " rpm";
                    tip.Append(fan.Name).Append(' ').Append(double.IsNaN(actual) ? "?" : Math.Round(actual).ToString()).Append(", ");
                }
                if (canForce)
                    SetForceMask(forceMask);

                foreach (var kv in tempItems)
                {
                    double t = Temp(kv.Key);
                    kv.Value.SubItems[1].Text = double.IsNaN(t) ? "\u2013" : t.ToString("0.0");
                }

                string tipText = string.Format("CPU {0} GPU {1}\n{2} rpm", FormatTemp(Temp(cpuKey)),
                    FormatTemp(Temp(gpuKey)), tip.ToString().TrimEnd(',', ' '));
                tray.Text = tipText.Length > 63 ? tipText.Substring(0, 63) : tipText;
                UpdateTrayIcon(Temp(cpuKey));
            }
            catch (Exception ex)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = "Error: " + ex.Message;
            }
        }

        // The mobile Radeon GPUs in these Macs are designed to run hotter than the CPU.
        double TripLimit(string key)
        {
            return key == gpuKey ? GpuTripC : SafetyTripC;
        }

        // Hands every forced fan back to the SMC while a critical sensor is hot or unreadable.
        // A sensor trips at its limit; forced control resumes once every sensor is
        // SafetyHysteresisC below its limit.
        void UpdateSafety()
        {
            bool unreadable = false, overLimit = false, cooledDown = true;
            double worstMargin = double.MinValue, worstTemp = 0;
            string worstKey = null;
            foreach (var key in safetyKeys)
            {
                double t = Temp(key);
                if (double.IsNaN(t))
                {
                    unreadable = true;
                    continue;
                }
                double margin = t - TripLimit(key);
                overLimit |= margin >= 0;
                cooledDown &= margin < -SafetyHysteresisC;
                if (margin > worstMargin)
                {
                    worstMargin = margin;
                    worstTemp = t;
                    worstKey = key;
                }
            }

            bool wasTripped = safetyTripped;
            if (unreadable || overLimit)
                safetyTripped = true;
            else if (cooledDown)
                safetyTripped = false;

            bool anyForced = false;
            foreach (var fan in fans)
                anyForced |= fan.IsForced;

            if (safetyTripped && anyForced)
            {
                string message = unreadable
                    ? "A temperature sensor is unreadable. Forced fans are handed back to the Mac."
                    : string.Format("{0} at {1}. Forced fans are handed back to the Mac until it is below {2}°C.",
                                    SensorName(worstKey), FormatTemp(worstTemp), TripLimit(worstKey) - SafetyHysteresisC);
                status.ForeColor = Color.Firebrick;
                status.Text = "Safety: " + message;
                if (!wasTripped)
                    tray.ShowBalloonTip(5000, AppName + " safety cut-out", message, ToolTipIcon.Warning);
            }
            else if (wasTripped && !safetyTripped || status.ForeColor == Color.Firebrick)
            {
                status.ForeColor = SystemColors.GrayText;
                status.Text = "Fans only ever run faster than Apple's default unless forced. Defaults are restored on exit.";
                if (wasTripped && !safetyTripped && anyForced)
                    tray.ShowBalloonTip(4000, AppName, "Temperatures are back to normal. Forced fan control resumed.", ToolTipIcon.Info);
            }
        }

        string SensorName(string key)
        {
            foreach (var s in sensors)
                if (s.Key == key)
                    return s.Name;
            return key;
        }

        // FS! is a bitmask: bit n set = fan n is in forced (manual) mode.
        void SetForceMask(int mask)
        {
            var r = smc.Read(ForceKey, 2);
            int current = r == null ? -1 : (r[0] << 8) | r[1];
            int allFans = (1 << fans.Count) - 1;
            int wanted = current < 0 ? mask : (current & ~allFans) | mask;
            if (wanted != current)
                smc.Write(ForceKey, new[] { (byte)(wanted >> 8), (byte)wanted });
        }

        static string FormatTemp(double t)
        {
            return double.IsNaN(t) ? "?" : Math.Round(t) + "\u00B0C";
        }

        // Tray icon shows the CPU temperature, coloured orange/red when it gets hot.
        void UpdateTrayIcon(double cpu)
        {
            string text = double.IsNaN(cpu) ? "?" : Math.Round(cpu).ToString();
            Color color = cpu >= 85 ? Color.FromArgb(255, 90, 70) : cpu >= 70 ? Color.Orange : Color.White;
            string key = text + color.ToArgb();
            if (key == trayIconText)
                return;
            trayIconText = key;

            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font("Segoe UI", text.Length > 2 ? 7f : 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(color))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.Clear(Color.FromArgb(45, 45, 48));
                    g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                    g.DrawString(text, font, brush, new RectangleF(-1, 0, 18, 17), format);
                }
                IntPtr handle = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                tray.Icon = icon;
                Icon = icon;
                if (trayIcon != null)
                    trayIcon.Dispose();
                trayIcon = icon;
            }
        }

        void RestoreDefaults()
        {
            if (restored)
                return;
            restored = true;
            timer.Stop();
            if (canForce)
                SetForceMask(0);
            foreach (var fan in fans)
                smc.WriteRpm(fan.Key("Mn"), fan.AppleMin);
        }

        // ---- Start with Windows: a logon task with highest privileges avoids a UAC prompt. ----

        static int RunHidden(string file, string args)
        {
            var info = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(info))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static bool IsAutostartEnabled()
        {
            return RunHidden("schtasks.exe", "/Query /TN \"" + AppName + "\"") == 0;
        }

        void SetAutostart(bool enable)
        {
            int code;
            if (enable)
            {
                string user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
                string xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                    "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType>" +
                    "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                    "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                    "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) +
                    "</Command><Arguments>/tray</Arguments></Exec></Actions>\n" +
                    "</Task>\n";
                string file = Path.Combine(Path.GetTempPath(), AppName + "-task.xml");
                File.WriteAllText(file, xml, Encoding.Unicode);
                code = RunHidden("schtasks.exe", "/Create /F /TN \"" + AppName + "\" /XML \"" + file + "\"");
                File.Delete(file);
            }
            else
            {
                code = RunHidden("schtasks.exe", "/Delete /F /TN \"" + AppName + "\"");
            }

            if (code != 0)
            {
                MessageBox.Show(this, "Could not update the Windows startup task (schtasks exit code " + code + ").",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                loading = true;
                autostart.Checked = IsAutostartEnabled();
                loading = false;
            }
        }

        // ---- Window / tray behaviour ----

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!IsHandleCreated)
                    CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        public void ShowFromTray()
        {
            allowVisible = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        public void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !exiting)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, AppName, "Still controlling the fans from the tray. Right-click the icon to exit.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            RestoreDefaults();
            tray.Visible = false;
            tray.Dispose();
            smc.Dispose();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool startHidden = Array.IndexOf(args, "/tray") >= 0;
            bool exitRequest = Array.IndexOf(args, "/exit") >= 0;
            string showName = "Local\\" + MainForm.AppName + ".Show";
            string exitName = "Local\\" + MainForm.AppName + ".Exit";

            bool firstInstance;
            using (var mutex = new Mutex(true, "Local\\" + MainForm.AppName + ".Instance", out firstInstance))
            {
                if (!firstInstance)
                {
                    // Already running: ask that instance to show itself (or to exit with /exit).
                    EventWaitHandle signal;
                    if (EventWaitHandle.TryOpenExisting(exitRequest ? exitName : showName, out signal))
                        using (signal)
                            signal.Set();
                    return;
                }
                if (exitRequest)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                MainForm form;
                try
                {
                    form = new MainForm(startHidden);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, showName))
                using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, exitName))
                {
                    var listener = new Thread(delegate()
                    {
                        var handles = new WaitHandle[] { show, exit };
                        while (true)
                        {
                            int which = WaitHandle.WaitAny(handles);
                            try
                            {
                                if (which == 0)
                                    form.BeginInvoke(new MethodInvoker(form.ShowFromTray));
                                else
                                    form.BeginInvoke(new MethodInvoker(form.ExitApp));
                            }
                            catch (InvalidOperationException)
                            {
                                return; // form already gone
                            }
                        }
                    });
                    listener.IsBackground = true;
                    listener.Start();
                    Application.Run(form);
                }
            }
        }
    }
}
