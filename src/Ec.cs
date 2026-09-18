// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the embedded controller, through the driver.
//
// The EC is the chip that actually runs the fans; the WMI mailbox is the firmware relaying requests to it. On
// most boards the relay works and this file is only ever read from. On a few it refuses fan levels outright
// (878A answers rc 46 forever), and there the only way to the fans is the EC's own registers, reached through
// ports 0x62 and 0x66 with the handshake the ACPI specification describes (section 12, "Embedded Controller").
//
// Two things this file is careful about, both learned from other people's bug reports:
//  * The register map is per generation, not per vendor. The 2025 OMEN MAX boards lay the EC out differently,
//    and writing the classic addresses to them corrupts EC state until the Caps Lock LED blinks in panic
//    (OmenCore issue #60). So there is no default map: a profile either carries one, with its evidence, or
//    this class is never constructed for that board. Writes are further limited to the map's own short list.
//  * The EC also answers the battery, the lid and the keyboard. Flood it and its ACPI transactions time out
//    (Event 13), Windows loses the battery reading and runs the critical-battery action - a shutdown, on a
//    plugged-in laptop (OmenCore 2.8.6). So every wait here is bounded, identical writes are not repeated, and
//    a controller that times out repeatedly is left alone for a while.
//
// The map itself is OmenMon's (Hardware/EcData.cs), which agrees with omen-fan's probes and OmenCore's code:
// 0x34/0x35 set each fan in rpm/100, 0x62 hands manual control to the caller with 0x06 and back with 0x00,
// 0x63 is the countdown in seconds after which the EC takes the fans back - the 120 s expiry the mailbox path
// keeps alive with the 0x10 query is this byte counting down.
using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Ohman {

    /// <summary>The two EC ports, however they are reached. Return 0 on success, else a Win32 error.</summary>
    public interface IEcPorts {
        int In(byte port, out byte value);
        int Out(byte port, byte value);
    }

    /// <summary>The ports through PawnIO's LpcACPIEC module, which allows exactly 0x62 and 0x66.</summary>
    public sealed class PawnIoEcPorts : IEcPorts, IDisposable {
        readonly PawnIoModule m;
        public PawnIoEcPorts(PawnIoModule module) { m = module; }
        public int In(byte port, out byte value) {
            var o = new ulong[1]; int n;
            int rc = m.Execute("ioctl_pio_read", new ulong[] { port }, o, out n);
            value = rc == 0 ? (byte)o[0] : (byte)0;
            return rc;
        }
        public int Out(byte port, byte value) { int n; return m.Execute("ioctl_pio_write", new ulong[] { port, value }, null, out n); }
        public void Dispose() { m.Dispose(); }
    }

    /// <summary>Where things are in this generation's EC. Every address is a claim with a source behind it.</summary>
    public sealed class EcMap {
        public string Name;
        public byte CpuTemp = 0x57, GpuTemp = 0xB7;                 // CPUT, GPTM: degrees C
        public byte Rpm1 = 0xB0, Rpm2 = 0xB2;                       // RPM1/2, RPM3/4: 16-bit little-endian rpm per fan
        public byte FanSet1 = 0x34, FanSet2 = 0x35;                 // SRP1, SRP2: rpm/100, the mailbox's own unit
        public byte Manual = 0x62, ManualOn = 0x06, ManualOff = 0x00;   // OMCC
        public byte Countdown = 0x63, CountdownDefault = 0x78, CountdownHold = 0xFF;   // XFCD, seconds
        public byte Mode = 0x95, Charge = 0x96;                     // HPCM, XBCH: read for the report only
        /// <summary>The registers a write may touch at all. Everything else is refused in code, whatever the caller.</summary>
        public byte[] Writable { get { return new byte[] { FanSet1, FanSet2, Manual, Countdown }; } }
        public bool MayWrite(byte reg) { foreach (byte w in Writable) if (w == reg) return true; return false; }

        /// <summary>The 2018–2022 OMEN layout: OmenMon (developed on 8A14), omen-fan (16-c0140AX), OmenCore
        /// (8574 fan control confirmed in the field). Not the 2025 OMEN MAX, whose layout differs.</summary>
        public static EcMap Legacy() { return new EcMap { Name = "OMEN 2018-2022 (OmenMon / omen-fan map)" }; }
    }

    /// <summary>One read of everything the map names. -1 where the read failed.</summary>
    public sealed class EcReading {
        public int Cpu = -1, Gpu = -1, Rpm1 = -1, Rpm2 = -1, Manual = -1, Countdown = -1, Mode = -1, Charge = -1;
        public bool Any { get { return Cpu >= 0 || Rpm1 >= 0 || Manual >= 0; } }
    }

    public sealed class EmbeddedController : IDisposable {
        const byte DataPort = 0x62, CommandPort = 0x66;
        const byte CmdRead = 0x80, CmdWrite = 0x81;
        const byte Obf = 0x01, Ibf = 0x02;              // status bits: output buffer full, input buffer full
        const int WaitPolls = 400;                     // bounded: ~20 ms of polling, then 1 ms sleeps, never more than ~400 ms
        const int MutexWaitMs = 250;
        const int TimeoutsBeforeRest = 5;
        static readonly TimeSpan Rest = TimeSpan.FromMinutes(10);

        public readonly EcMap Map;
        readonly IEcPorts ports;
        readonly Mutex mutex;                          // Global\Access_EC: OmenMon, LibreHardwareMonitor, OmenCore and HWiNFO all take it
        readonly object sync = new object();
        int timeouts;
        DateTime restUntil = DateTime.MinValue;
        public int Timeouts { get { return timeouts; } }
        public string LastError = "";
        readonly Dictionary<byte, byte> lastWritten = new Dictionary<byte, byte>();
        readonly Dictionary<byte, DateTime> lastWrittenAt = new Dictionary<byte, DateTime>();

        public EmbeddedController(IEcPorts ports, EcMap map) {
            this.ports = ports;
            Map = map;
            mutex = OpenMutex(@"Global\Access_EC");
        }

        /// <summary>The shared lock, created so that anyone can open it afterwards. OmenMon does the same; an
        /// admin-only mutex would make the next tool fail to open it and go ahead without it, which is worse.</summary>
        static Mutex OpenMutex(string name) {
            try {
                var sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                return new Mutex(false, name, out created, sec);
            } catch (UnauthorizedAccessException) {
                try { return Mutex.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify); } catch { return null; }
            } catch { return null; }
        }

        /// <summary>Resting after repeated timeouts. Callers on the fan path fall back to the mailbox meanwhile.</summary>
        public bool Resting { get { return DateTime.Now < restUntil; } }

        // ---------- the handshake ----------
        bool Status(out byte s) { return ports.In(CommandPort, out s) == 0; }
        bool WaitFor(byte bit, bool set) {
            byte s;
            for (int i = 0; i < WaitPolls; i++) {
                if (!Status(out s)) return false;
                if (((s & bit) != 0) == set) return true;
                if (i >= 20) Thread.Sleep(1);
            }
            return false;
        }
        /// <summary>A byte left in the output buffer by an earlier, interrupted transaction would be handed to us
        /// as the answer to this one. Take it out first.</summary>
        void Drain() {
            byte s, junk;
            if (Status(out s) && (s & Obf) != 0) ports.In(DataPort, out junk);
        }
        bool ReadRaw(byte reg, out byte value) {
            value = 0;
            Drain();
            if (!WaitFor(Ibf, false) || ports.Out(CommandPort, CmdRead) != 0) return false;
            if (!WaitFor(Ibf, false) || ports.Out(DataPort, reg) != 0) return false;
            if (!WaitFor(Obf, true)) return false;
            return ports.In(DataPort, out value) == 0;
        }
        bool WriteRaw(byte reg, byte value) {
            if (!WaitFor(Ibf, false) || ports.Out(CommandPort, CmdWrite) != 0) return false;
            if (!WaitFor(Ibf, false) || ports.Out(DataPort, reg) != 0) return false;
            if (!WaitFor(Ibf, false) || ports.Out(DataPort, value) != 0) return false;
            return WaitFor(Ibf, false);
        }

        /// <summary>Run one or more transactions under the lock. False when the lock is busy, the controller is
        /// resting, or the body reported a failure; a timeout counts towards the rest.</summary>
        bool Locked(Func<bool> body, string what) {
            if (Resting) { LastError = "EC resting after " + timeouts + " timeouts"; return false; }
            bool held = false;
            lock (sync) {
                try {
                    if (mutex != null) {
                        try { held = mutex.WaitOne(MutexWaitMs); } catch (AbandonedMutexException) { held = true; }
                        if (!held) { LastError = "EC busy (another program holds it)"; return false; }
                    }
                    bool ok = body();
                    if (ok) { timeouts = 0; LastError = ""; }
                    else {
                        timeouts++;
                        if (LastError.Length == 0) LastError = "EC did not answer (" + what + ")";
                        if (timeouts >= TimeoutsBeforeRest) { restUntil = DateTime.Now + Rest; Log.Write("EC: " + timeouts + " timeouts in a row; leaving it alone for " + Rest.TotalMinutes + " minutes"); }
                    }
                    return ok;
                } catch (Exception ex) { LastError = ex.Message; return false; }
                finally { if (held) { try { mutex.ReleaseMutex(); } catch { } } }
            }
        }

        // ---------- what callers use ----------
        public bool ReadByte(byte reg, out byte value) { byte v = 0; bool ok = Locked(delegate { return ReadRaw(reg, out v); }, "read 0x" + reg.ToString("X2")); value = v; return ok; }

        /// <summary>A write, to a register on the map's own list only. Repeating a value the register already
        /// holds is skipped for five seconds: the fan path calls every tick and the EC does not need telling twice.</summary>
        public bool WriteByte(byte reg, byte value) {
            if (!Map.MayWrite(reg)) { LastError = "0x" + reg.ToString("X2") + " is not a register this map allows writing"; Log.Write("EC: refused write to " + LastError); return false; }
            byte had; DateTime at;
            if (lastWritten.TryGetValue(reg, out had) && had == value && lastWrittenAt.TryGetValue(reg, out at) && (DateTime.Now - at).TotalSeconds < 5) return true;
            bool ok = Locked(delegate { return WriteRaw(reg, value); }, "write 0x" + reg.ToString("X2"));
            if (ok) { lastWritten[reg] = value; lastWrittenAt[reg] = DateTime.Now; }
            return ok;
        }

        /// <summary>Everything on the map in one hold of the lock, for the panel and the report.</summary>
        public EcReading Read() {
            var r = new EcReading();
            Locked(delegate {
                byte lo, hi, v;
                if (ReadRaw(Map.CpuTemp, out v)) r.Cpu = v;
                if (ReadRaw(Map.GpuTemp, out v)) r.Gpu = v;
                if (ReadRaw(Map.Rpm1, out lo) && ReadRaw((byte)(Map.Rpm1 + 1), out hi)) r.Rpm1 = lo | (hi << 8);
                if (ReadRaw(Map.Rpm2, out lo) && ReadRaw((byte)(Map.Rpm2 + 1), out hi)) r.Rpm2 = lo | (hi << 8);
                if (ReadRaw(Map.Manual, out v)) r.Manual = v;
                if (ReadRaw(Map.Countdown, out v)) r.Countdown = v;
                if (ReadRaw(Map.Mode, out v)) r.Mode = v;
                if (ReadRaw(Map.Charge, out v)) r.Charge = v;
                return r.Any;
            }, "snapshot");
            return r;
        }

        /// <summary>Take the fans and set both levels, in the mailbox's unit (rpm/100). Manual mode is asserted
        /// first and read back after: a controller that does not keep 0x06 in the manual register is not one this
        /// map fits, and the caller must stop using it. The countdown is set to its maximum so the hold outlives
        /// the caller's own 5 s tick many times over; the caller still calls every tick, and a repeat is free.</summary>
        public bool HoldFans(int level1, int level2) {
            byte l1 = (byte)Math.Max(0, Math.Min(255, level1)), l2 = (byte)Math.Max(0, Math.Min(255, level2));
            if (!WriteByte(Map.Manual, Map.ManualOn)) return false;
            if (!WriteByte(Map.FanSet1, l1) || !WriteByte(Map.FanSet2, l2)) return false;
            if (!WriteByte(Map.Countdown, Map.CountdownHold)) return false;
            byte check;
            if (!ReadByte(Map.Manual, out check)) return false;
            if (check != Map.ManualOn) { LastError = "manual register reads 0x" + check.ToString("X2") + " after writing 0x" + Map.ManualOn.ToString("X2") + "; this EC does not follow the map"; return false; }
            return true;
        }

        /// <summary>Hand the fans back: levels cleared, manual off, countdown at the firmware's own default so
        /// it resumes its curve on the next tick of its clock. omen-fan's restore sequence.</summary>
        public bool ReleaseFans() {
            lastWritten.Clear();                        // the point is to write these whatever we wrote last
            bool ok = WriteByte(Map.FanSet1, 0);
            ok &= WriteByte(Map.FanSet2, 0);
            ok &= WriteByte(Map.Manual, Map.ManualOff);
            ok &= WriteByte(Map.Countdown, Map.CountdownDefault);
            return ok;
        }

        public void Dispose() {
            try { var d = ports as IDisposable; if (d != null) d.Dispose(); } catch { }
            try { if (mutex != null) mutex.Close(); } catch { }
        }
    }

    /// <summary>A pretend EC for the preview build: registers in an array, the handshake honoured.</summary>
    public sealed class DemoEcPorts : IEcPorts {
        readonly byte[] ram = new byte[256];
        readonly Random rnd = new Random();
        byte status;                  // OBF/IBF as a real controller would show them
        int phase;                    // 0 idle, 1 got read cmd, 2 got write cmd, 3 write cmd + address
        byte address, output;
        DateTime lastTick = DateTime.Now;
        public DemoEcPorts() {
            ram[0x57] = 48; ram[0xB7] = 41;
            SetRpm(0xB0, 2650); SetRpm(0xB2, 2480);
            ram[0x63] = 0x78; ram[0x95] = 0x30; ram[0x96] = 100;
        }
        void SetRpm(int at, int rpm) { ram[at] = (byte)(rpm & 0xFF); ram[at + 1] = (byte)(rpm >> 8); }
        void Tick() {
            if ((DateTime.Now - lastTick).TotalSeconds < 1) return;
            lastTick = DateTime.Now;
            ram[0x57] = (byte)Math.Max(36, Math.Min(90, ram[0x57] + rnd.Next(-1, 2)));
            if (ram[0x63] > 0) ram[0x63]--;
            if (ram[0x62] == 0x06) { SetRpm(0xB0, ram[0x34] * 100); SetRpm(0xB2, ram[0x35] * 100); }
        }
        public int In(byte port, out byte value) {
            Tick();
            if (port == 0x66) { value = status; return 0; }
            value = output; status &= unchecked((byte)~0x01);
            return 0;
        }
        public int Out(byte port, byte value) {
            if (port == 0x66) { phase = value == 0x80 ? 1 : value == 0x81 ? 2 : 0; return 0; }
            switch (phase) {
                case 1: output = ram[value]; status |= 0x01; phase = 0; break;
                case 2: address = value; phase = 3; break;
                case 3: ram[address] = value; phase = 0; break;
            }
            return 0;
        }
    }
}
