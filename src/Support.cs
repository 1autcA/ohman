// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the support report: everything needed to add or fix a laptop, gathered in one go.
//
// This exists because the first day of reports was mostly people being asked for one more thing. Every question
// that came up — which commands does this firmware answer, which thermal-policy version, does the keyboard
// declare a backlight, which ACPI zone is being read, what did OMEN Gaming Hub itself decide — is answered here
// without a second round trip. Ohman is already running elevated, so it can ask the firmware directly rather
// than sending somebody to find a script and elevate it again.
//
// Everything read here is read-only, and every line is scrubbed of the user name, profile path and machine name
// before it goes out: this text exists to be pasted into a public issue.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;

namespace Ohman {

    public static class Support {

        /// <summary>Takes out anything that identifies the machine or its owner. The report is meant to be pasted
        /// into a public issue, and paths under C:\Users carry a real name.</summary>
        public static string Scrub(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            try {
                string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(prof)) s = Replace(s, prof, "<userprofile>");
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length > 2) s = Replace(s, user, "<user>");
                string host = Environment.MachineName;
                if (!string.IsNullOrEmpty(host) && host.Length > 2) s = Replace(s, host, "<host>");
            } catch { }
            return s;
        }
        static string Replace(string hay, string needle, string with) {
            int i; int from = 0;
            while ((i = hay.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase)) >= 0) {
                hay = hay.Substring(0, i) + with + hay.Substring(i + needle.Length);
                from = i + with.Length;
            }
            return hay;
        }

        /// <summary>An OGH log line is "timestamp [INF] [PID: n] [TID: n] the interesting part". Drop the
        /// timestamp, then every bracketed field that follows, and keep what is left.</summary>
        static string Strip(string line) {
            int at = line.IndexOf("[INF]", StringComparison.Ordinal);
            string v = at >= 0 ? line.Substring(at + 5).TrimStart() : line.TrimStart();
            while (v.Length > 0 && v[0] == '[') {
                int close = v.IndexOf(']');
                if (close < 0) break;
                v = v.Substring(close + 1).TrimStart();
            }
            return v.Trim();
        }

        static string OghLogDir() {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Packages";
            if (!System.IO.Directory.Exists(root)) return null;
            foreach (string d in System.IO.Directory.GetDirectories(root, "AD2F1837.OMENCommandCenter*")) {
                string c = d + "\\LocalCache\\Local\\HPOMEN";
                if (System.IO.Directory.Exists(c)) return c;
            }
            return null;
        }
        static List<System.IO.FileInfo> OghLogs(string dir) {
            var files = new List<System.IO.FileInfo>();
            foreach (string f in System.IO.Directory.GetFiles(dir, "HPOMEN*.log")) files.Add(new System.IO.FileInfo(f));
            files.Sort(delegate(System.IO.FileInfo a, System.IO.FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
            return files;
        }

        static string Hex(byte[] d, int max) {
            if (d == null) return "(null)";
            var sb = new StringBuilder();
            for (int i = 0; i < d.Length && i < max; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2")); }
            return sb.ToString();
        }

        /// <summary>One read-only BIOS query, reported whether it answers or not. A refusal is data: it is how we
        /// learned that board 8574 implements 0x20009 and none of 0x20008.</summary>
        static byte[] Probe(StringBuilder sb, string label, uint cmd, uint op, byte[] data, int outSize) {
            try {
                var d = Bios.Call(cmd, op, data, outSize);
                sb.AppendLine("  " + label.PadRight(22) + "ok   " + Hex(d, 24));
                return d;
            } catch (Exception ex) {
                sb.AppendLine("  " + label.PadRight(22) + "NO   " + Scrub(ex.Message));
                return null;
            }
        }

        /// <summary>A prefilled "New laptop support" form. GitHub fills an issue form from query parameters named
        /// after the field ids, and caps the whole URL at roughly 8 KB, so the report itself only goes in when it
        /// fits. It is on the clipboard either way, and the form says so when it is not already in the box.</summary>
        public static string IssueUrl(Engine e, string report) {
            string model = "";
            try {
                using (var s = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) model = "" + o["Model"];
            } catch { }
            const string Base = "https://github.com/P4R1H/ohman/issues/new?template=new-laptop-support.yml";
            string url = Base
                + "&title=" + Uri.EscapeDataString("Support: " + (model.Length > 0 ? model : "board " + e.Board) + " (board " + e.Board + ")")
                + "&model=" + Uri.EscapeDataString(model)
                + "&board=" + Uri.EscapeDataString(e.Board);
            string with = url + "&supportinfo=" + Uri.EscapeDataString(report);
            if (with.Length <= 7000) return with;
            return url + "&supportinfo=" + Uri.EscapeDataString("(the report is on your clipboard - paste it here with Ctrl+V)");
        }

        public static string Report(Engine e) {
            var sb = new StringBuilder();
            sb.AppendLine("Ohman support report  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Ohman " + Program.Version + (e.Hw.IsDemo ? "  (SIMULATED HARDWARE - not a real reading)" : ""));
            sb.AppendLine("----------------------------------------");

            // ---- the machine
            try {
                string model = "", sku = "", family = "";
                using (var s = new ManagementObjectSearcher("SELECT Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem"))
                    foreach (ManagementObject o in s.Get()) { model = "" + o["Model"]; sku = "" + o["SystemSKUNumber"]; family = "" + o["SystemFamily"]; }
                sb.AppendLine("Model:     " + model + "   (family " + family + ", SKU " + sku + ")");
            } catch (Exception ex) { sb.AppendLine("Model:     unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine("Board:     " + e.Board);
            try {
                using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("BIOS:      " + o["SMBIOSBIOSVersion"] + "  " + ("" + o["ReleaseDate"]).Substring(0, 8));
            } catch { }
            try {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("CPU:       " + o["Name"]);
                var gpus = new List<string>();
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get()) gpus.Add("" + o["Name"]);
                sb.AppendLine("GPU:       " + string.Join(" / ", gpus.ToArray()));
                using (var s = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get()) sb.AppendLine("Windows:   " + o["Caption"] + " " + o["Version"]);
            } catch { }
            sb.AppendLine("Elevated:  " + (!e.Hw.IsDemo));
            sb.AppendLine();

            // ---- what Ohman decided, and why
            sb.AppendLine("Ohman's decision");
            sb.AppendLine("  profile:  " + (e.Supported ? e.P.Name + (e.Generic ? "  [generic, built from the firmware]" : "  [verified entry]") : "UNSUPPORTED - read-only, nothing is written"));
            sb.AppendLine("  notes:    " + (e.P != null && e.P.Notes != null ? e.P.Notes : ""));
            sb.AppendLine("  bios ok:  " + e.BiosOk + (e.LastError.Length > 0 ? "   last error: " + Scrub(e.LastError) : ""));
            if (e.P != null)
                sb.AppendLine("  modes:    eco 0x" + e.P.ModeEco.ToString("X2") + " balanced 0x" + e.P.ModeBalanced.ToString("X2")
                    + " performance 0x" + e.P.ModePerformance.ToString("X2") + " cool 0x" + e.P.ModeCool.ToString("X2")
                    + "   (v" + e.P.ThermalPolicy + ")"
                    + (e.P.ModeEco == e.P.ModeBalanced ? "   <- eco and balanced are the same byte on this firmware" : ""));
            sb.AppendLine("  lighting: " + (e.Light == null ? "none detected" : e.Light.Describe + (e.Light.Inert ? "  (firmware answers but drives nothing; per-key board)" : "")));
            sb.AppendLine("  fans:     " + e.FanCount + "   graphics: " + e.GpuMode);
            sb.AppendLine();

            // ---- the firmware, asked directly
            sb.AppendLine("BIOS queries (read-only; NO means the firmware refused, which is itself useful)");
            byte[] sys = null, fanTable = null;
            if (!e.Hw.IsDemo) {
                var z4 = new byte[4];
                sys = Probe(sb, "0x28 system data", Bios.CMD_DEFAULT, 0x28, z4, 128);
                Probe(sb, "0x2D fan levels", Bios.CMD_DEFAULT, 0x2D, z4, 128);
                fanTable = Probe(sb, "0x2F fan table", Bios.CMD_DEFAULT, 0x2F, z4, 128);
                Probe(sb, "0x2C fan types", Bios.CMD_DEFAULT, 0x2C, z4, 128);
                Probe(sb, "0x23 chassis temp", Bios.CMD_DEFAULT, 0x23, new byte[] { 1, 0, 0, 0 }, 4);
                Probe(sb, "0x21 gpu power", Bios.CMD_DEFAULT, 0x21, z4, 4);
                Probe(sb, "0x26 max fan", Bios.CMD_DEFAULT, 0x26, z4, 4);
                Probe(sb, "0x2B keyboard type", Bios.CMD_DEFAULT, 0x2B, z4, 4);
                Probe(sb, "0x52 graphics mode", Bios.CMD_DEFAULT, 0x52, z4, 4);
                // 0x10 is deliberately not here: that query is the firmware's user-defined-fan trigger, not a read.
                Probe(sb, "0x20009/01 support", BiosLighting.CMD, 0x01, z4, 128);
                Probe(sb, "0x20009/02 colours", BiosLighting.CMD, 0x02, new byte[] { 0 }, 128);
                Probe(sb, "0x20009/04 backlight", BiosLighting.CMD, 0x04, new byte[] { 0 }, 128);
            } else sb.AppendLine("  (simulated hardware: nothing was asked)");
            sb.AppendLine();

            // ---- the same bytes, read out loud
            sb.AppendLine("Decoded");
            if (sys != null && sys.Length >= 9) {
                sb.AppendLine("  thermal policy:   v" + sys[3] + "   <- picks the mode bytes (v0 = 00/01/02, v1 = 30/31/50)");
                sb.AppendLine("  software fan:     " + (((sys[4] & 1) != 0) ? "yes" : "no"));
                sb.AppendLine("  PL4 default:      " + sys[5] + " W");
                sb.AppendLine("  base concurrent:  " + sys[8] + " W   <- where the power gain slider starts");
                var gm = new List<string>();
                if ((sys[7] & 1) != 0) gm.Add("iGPU only");
                if ((sys[7] & 2) != 0) gm.Add("Hybrid");
                if ((sys[7] & 4) != 0) gm.Add("Discrete");
                if ((sys[7] & 8) != 0) gm.Add("Advanced Optimus");
                sb.AppendLine("  graphics modes:   0x" + sys[7].ToString("X2") + "  " + (gm.Count > 0 ? string.Join(", ", gm.ToArray()) : "none offered"));
                if (fanTable != null && fanTable.Length >= 2) {
                    int rows = Math.Min((int)fanTable[1], 40), top = 0;
                    for (int i = 0; i < rows; i++) { int o = 2 + 3 * i; if (o + 1 < fanTable.Length) top = Math.Max(top, Math.Max(fanTable[o], fanTable[o + 1])); }
                    sb.AppendLine("  fan table:        " + fanTable[0] + " fans, " + fanTable[1] + " curve rows, top level " + top + " (about " + (top * 100) + " rpm)");
                }
            } else if (!e.Hw.IsDemo) {
                sb.AppendLine("  system data was refused. The thermal-policy version is what decides the mode bytes, so");
                sb.AppendLine("  without it this board cannot be driven from the firmware alone and needs a contributed");
                sb.AppendLine("  readback. An OMEN Gaming Hub log is the best source; see docs/laptops.md.");
            }
            sb.AppendLine();

            // ---- which sensor the temperature comes from
            sb.AppendLine("ACPI thermal zones (Ohman uses the hottest valid one)");
            try {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                string[] inst = cat.GetInstanceNames();
                Array.Sort(inst);
                if (inst.Length == 0) sb.AppendLine("  (none exposed by this machine)");
                foreach (string n in inst) {
                    PerformanceCounter pc = null;
                    try {
                        pc = new PerformanceCounter("Thermal Zone Information", "Temperature", n, true);
                        pc.NextValue(); System.Threading.Thread.Sleep(60);
                        double k = pc.NextValue();
                        bool usable = k >= 283 && k <= 398;
                        sb.AppendLine("  " + Scrub(n).PadRight(30) + Math.Round(k - 273.15, 1).ToString(CultureInfo.InvariantCulture).PadLeft(7)
                            + " C" + (usable ? "" : "   (outside 10-125 C, ignored)"));
                    } catch { sb.AppendLine("  " + Scrub(n).PadRight(30) + " unreadable"); }
                    finally { if (pc != null) try { pc.Dispose(); } catch { } }
                }
                sb.AppendLine("  If this never moves while the machine is busy, it is not the CPU. Say so in the report.");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- what HP's own app concluded about this machine
            sb.AppendLine("OMEN Gaming Hub's own answers (from its log, if it has ever run)");
            try {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Packages";
                string dir = null;
                if (System.IO.Directory.Exists(root))
                    foreach (string d in System.IO.Directory.GetDirectories(root, "AD2F1837.OMENCommandCenter*")) {
                        string c = d + "\\LocalCache\\Local\\HPOMEN";
                        if (System.IO.Directory.Exists(c)) { dir = c; break; }
                    }
                if (dir == null) sb.AppendLine("  (OMEN Gaming Hub has never run here)");
                else {
                    string[] keys = { "IsCtgpModeSupport", "IsIccMaxSupport", "IsSurfaceTempSupport", "ChangeTppToDynamicBoost",
                                      "GetUnleashedModePowerLimit4", "GetUnleashedModeTppOffset", "IsEnableTgpPpab", "SetPL1DefaultValue" };
                    var found = new Dictionary<string, string>();
                    var files = new List<System.IO.FileInfo>();
                    foreach (string f in System.IO.Directory.GetFiles(dir, "HPOMEN*.log")) files.Add(new System.IO.FileInfo(f));
                    files.Sort(delegate(System.IO.FileInfo a, System.IO.FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                    int n = 0;
                    foreach (var f in files) {
                        if (++n > 4) break;
                        try {
                            foreach (string line in System.IO.File.ReadLines(f.FullName))
                                foreach (string k in keys)
                                    if (!found.ContainsKey(k) && line.IndexOf(k, StringComparison.Ordinal) >= 0) {
                                        found[k] = Scrub(Strip(line));
                                    }
                        } catch { }
                    }
                    if (found.Count == 0) sb.AppendLine("  (no capability lines in the most recent logs)");
                    foreach (string k in keys) if (found.ContainsKey(k)) sb.AppendLine("  " + found[k]);
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- OGH's cached copy of system data. This is the one that matters when the firmware refuses
            //      0x28: HP's own app stores the same bytes, and byte 3 is the thermal-policy version, which is
            //      the single thing standing between an undrivable board and a working one.
            sb.AppendLine("OMEN Gaming Hub's cached system data (works even when 0x28 is refused)");
            try {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HP\OMEN Ally\Settings")) {
                    if (k == null) sb.AppendLine("  (no OGH settings key)");
                    else {
                        var raw = k.GetValue("SystemDesignData") as byte[];
                        if (raw == null) sb.AppendLine("  (key present, no SystemDesignData)");
                        else {
                            sb.AppendLine("  SystemDesignData = " + Hex(raw, 12));
                            if (raw.Length > 8) sb.AppendLine("  -> thermal policy v" + raw[3] + ", PL4 " + raw[5] + " W, base concurrent " + raw[8] + " W");
                        }
                        foreach (string n in new[] { "LoadedJsonSku", "LastLoadedJsonSku" }) {
                            object v = k.GetValue(n);
                            if (v != null) sb.AppendLine("  " + n + " = " + Scrub("" + v));
                        }
                    }
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the payloads OGH itself sends, which is ground truth for any byte we are unsure of
            sb.AppendLine("Payloads OMEN Gaming Hub sent (its own writes; ground truth for a byte we guessed)");
            try {
                string dir2 = OghLogDir();
                if (dir2 == null) sb.AppendLine("  (OMEN Gaming Hub has never run here)");
                else {
                    var counts = new Dictionary<string, int>();
                    var files2 = OghLogs(dir2);
                    int n2 = 0;
                    foreach (var f in files2) {
                        if (++n2 > 4) break;
                        try {
                            foreach (string line in System.IO.File.ReadLines(f.FullName)) {
                                int at = line.IndexOf("inputData=", StringComparison.Ordinal);
                                if (at < 0) continue;
                                string v = line.Substring(at + 10).Trim().TrimEnd(',');
                                if (v.Length == 0 || v.Length > 40 || v.IndexOf(',') < 0 || v == "0,0,0,0" || v == "is null") continue;   // single values are not payloads
                                if (counts.ContainsKey(v)) counts[v]++; else counts[v] = 1;
                            }
                        } catch { }
                    }
                    if (counts.Count == 0) sb.AppendLine("  (none in the most recent logs)");
                    var keys2 = new List<string>(counts.Keys);
                    keys2.Sort(delegate(string a, string b) { return counts[b].CompareTo(counts[a]); });
                    int shown = 0;
                    foreach (string key in keys2) { if (++shown > 12) break; sb.AppendLine("  " + key.PadRight(24) + " x" + counts[key]); }
                }
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the GPU's own limits, for any report about wattage
            sb.AppendLine("NVIDIA power limits");
            try {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,power.limit,power.default_limit,power.min_limit,power.max_limit --format=csv,noheader") {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                using (var pr = Process.Start(psi)) {
                    string o = pr.StandardOutput.ReadToEnd();
                    pr.WaitForExit(8000);
                    if (o.Trim().Length == 0) sb.AppendLine("  (no output; AMD or iGPU only)");
                    foreach (string line in o.Split('\n')) if (line.Trim().Length > 0) sb.AppendLine("  " + Scrub(line.Trim()));
                }
            } catch { sb.AppendLine("  (nvidia-smi not present; AMD or iGPU only)"); }
            sb.AppendLine();

            // ---- and what Ohman itself logged getting here
            sb.AppendLine("Ohman log (the decisions, not the whole file)");
            try {
                var keep = new List<string>();
                foreach (string line in System.IO.File.ReadLines(Log.Path)) {
                    if (line.IndexOf("platform:", StringComparison.Ordinal) >= 0 || line.IndexOf("generic profile", StringComparison.Ordinal) >= 0
                        || line.IndexOf("BIOS ok", StringComparison.Ordinal) >= 0 || line.IndexOf("self-test", StringComparison.Ordinal) >= 0
                        || line.IndexOf("read-only", StringComparison.Ordinal) >= 0 || line.IndexOf("no fan table", StringComparison.Ordinal) >= 0
                        || line.IndexOf("system data", StringComparison.Ordinal) >= 0 || line.IndexOf("thermal zone", StringComparison.Ordinal) >= 0
                        || line.IndexOf("keyboard lighting", StringComparison.Ordinal) >= 0 || line.IndexOf("THERMAL GUARD", StringComparison.Ordinal) >= 0
                        || line.IndexOf("FAIL ", StringComparison.Ordinal) >= 0)
                        keep.Add(line);
                }
                int from = Math.Max(0, keep.Count - 25);
                for (int i = from; i < keep.Count; i++) sb.AppendLine("  " + Scrub(keep[i]));
                if (keep.Count == 0) sb.AppendLine("  (nothing notable yet)");
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- the keyboard's own HID interface, which is what matters on a per-key board
            sb.AppendLine("HID lighting interface");
            try {
                var arrays = LampArray.All();
                if (arrays.Count == 0) sb.AppendLine("  no HID Lighting And Illumination collection (usage page 0x59) on this machine");
                foreach (var a in arrays) sb.AppendLine("  " + Scrub(a.Describe));
            } catch (Exception ex) { sb.AppendLine("  unavailable (" + Scrub(ex.Message) + ")"); }
            sb.AppendLine();

            // ---- and the running state, which is what Diagnostics already said well
            sb.AppendLine("Current state");
            foreach (string line in e.Diagnostics().Split('\n')) {
                string l = line.TrimEnd();
                if (l.Length > 0) sb.AppendLine("  " + Scrub(l));
            }
            return sb.ToString();
        }
    }
}
