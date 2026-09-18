// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the CPU's own registers, through the driver.
//
// The ACPI thermal zone Windows exposes is whatever the firmware chose to put there: on some boards the
// package sensor, on some a skin sensor, on a few a constant 28. The CPU itself knows exactly how hot it is
// and says so in a model-specific register, which only ring 0 can read. The same registers say what power
// limit the package is actually holding and why it is slowing down, neither of which the mailbox can tell.
//
// Intel: IA32_TEMPERATURE_TARGET (0x1A2) carries TjMax, IA32_PACKAGE_THERM_STATUS (0x1B1) the distance below
// it plus the throttle flags, MSR_PKG_POWER_LIMIT (0x610) PL1/PL2 in the units MSR_RAPL_POWER_UNIT (0x606)
// declares, and MSR_PKG_ENERGY_STATUS (0x611) a running energy count. Intel SDM vol. 4, cross-checked against
// LibreHardwareMonitor's IntelCpu.cs. AMD: the die temperature is not an MSR but a system-management-network
// register, THM_TCON_CUR_TMP at 0x59800, read through the AMDFamily17 module the same way LibreHardwareMonitor
// reads it (Amd17Cpu.cs); package energy comes from MSR_PKG_ENERGY_STAT (0xC001029B).
//
// Reads only. The one write the Intel module allows that would interest us, PL1/PL2, waits for a tester.
using System;
using System.Threading;
using Microsoft.Win32;

namespace Ohman {

    /// <summary>What one poll of the registers said. NaN where this CPU or this driver cannot say.</summary>
    public sealed class CpuTelemetry {
        public double DieTemp = double.NaN;         // the package sensor, degrees C
        public int TjMax;                           // where the CPU starts to throttle; 0 = unknown
        public double Pl1 = double.NaN, Pl2 = double.NaN;   // watts the package is allowed, long and short term
        public bool Pl1On, Pl2On, PlLocked;
        public double Watts = double.NaN;           // package power over the last poll interval
        public string Throttle = "";                // "" or why the CPU is being held back right now
    }

    /// <summary>The CPU's registers behind an interface, so the preview build can pretend to have them.</summary>
    public abstract class CpuRegisters : IDisposable {
        readonly object sync = new object();
        public abstract string Describe { get; }
        /// <summary>One reading. Serialised, because two threads do this: the sensor loop every couple of
        /// seconds, and the driver check eighty times in two when somebody presses Troubleshoot. The energy
        /// counter is a difference against the last reading and the last time, so two callers sharing one
        /// instance without this take each other's measurement window and both get a number the package
        /// cannot draw.</summary>
        public CpuTelemetry Poll() { lock (sync) return PollCore(); }
        protected abstract CpuTelemetry PollCore();
        public abstract void Dispose();

        /// <summary>The vendor string the kernel recorded at boot; no CPUID instruction needed from here.</summary>
        public static string Vendor() {
            try {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) return (k.GetValue("VendorIdentifier") as string ?? "").Trim();
            } catch { }
            return "";
        }

        /// <summary>Open the register set for this machine's CPU, or null with the reason.</summary>
        public static CpuRegisters Open(out string why) {
            string vendor = Vendor();
            if (vendor.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) {
                PawnIoModule m = PawnIo.Open("IntelMSR", out why);
                return m == null ? null : new IntelCpu(m);
            }
            if (vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0) {
                PawnIoModule m = PawnIo.Open("AMDFamily17", out why);
                return m == null ? null : new AmdCpu(m);
            }
            why = "unknown CPU vendor '" + vendor + "'";
            return null;
        }

        /// <summary>Energy counters are 32-bit and wrap; the difference over the interval is the power.</summary>
        protected static double Power(ulong now, ref ulong last, ref DateTime lastAt, double joulesPerCount) {
            DateTime t = DateTime.UtcNow;
            double w = double.NaN;
            if (lastAt != DateTime.MinValue) {
                double secs = (t - lastAt).TotalSeconds;
                ulong delta = (now - last) & 0xFFFFFFFFu;
                if (secs > 0.2 && secs < 120) w = delta * joulesPerCount / secs;
            }
            last = now; lastAt = t;
            return w;
        }
    }

    /// <summary>Intel Core, through the IntelMSR module.</summary>
    sealed class IntelCpu : CpuRegisters {
        const uint IA32_THERM_STATUS = 0x19C, IA32_TEMPERATURE_TARGET = 0x1A2, IA32_PACKAGE_THERM_STATUS = 0x1B1;
        const uint MSR_RAPL_POWER_UNIT = 0x606, MSR_PKG_POWER_LIMIT = 0x610, MSR_PKG_ENERGY_STATUS = 0x611;
        readonly PawnIoModule m;
        int tjMax;
        double powerUnit, energyUnit;       // watts per count, joules per count
        ulong lastEnergy; DateTime lastEnergyAt = DateTime.MinValue;
        public IntelCpu(PawnIoModule module) { m = module; }
        public override string Describe { get { return "Intel MSR" + (tjMax > 0 ? " · TjMax " + tjMax : ""); } }

        bool Read(uint msr, out ulong v) {
            var o = new ulong[1];
            bool ok = m.Call("ioctl_read_msr", new ulong[] { msr }, o);
            v = ok ? o[0] : 0;
            return ok;
        }

        protected override CpuTelemetry PollCore() {
            var t = new CpuTelemetry();
            ulong v;
            if (tjMax == 0 && Read(IA32_TEMPERATURE_TARGET, out v)) { tjMax = (int)((v >> 16) & 0xFF); if (tjMax == 0) tjMax = 100; }
            t.TjMax = tjMax;
            if (tjMax > 0 && Read(IA32_PACKAGE_THERM_STATUS, out v) && (v & 0x80000000u) != 0) {
                t.DieTemp = tjMax - (int)((v >> 16) & 0x7F);
                // The status bits are what is happening now; the bit above each is its sticky log, ignored here.
                if ((v & (1u << 0)) != 0) t.Throttle = "thermal";
                else if ((v & (1u << 2)) != 0) t.Throttle = "PROCHOT";
                else if ((v & (1u << 10)) != 0) t.Throttle = "power limit";
            }
            // Current-limit status is per core rather than per package; whichever core runs this thread is a
            // fair sample, since the limit is a package-level regulator decision.
            if (t.Throttle.Length == 0 && Read(IA32_THERM_STATUS, out v) && (v & (1u << 12)) != 0) t.Throttle = "current limit";
            if (powerUnit == 0 && Read(MSR_RAPL_POWER_UNIT, out v)) {
                powerUnit = 1.0 / (1 << (int)(v & 0xF));
                energyUnit = 1.0 / (1 << (int)((v >> 8) & 0x1F));
            }
            if (powerUnit > 0 && Read(MSR_PKG_POWER_LIMIT, out v)) {
                t.Pl1 = (v & 0x7FFF) * powerUnit; t.Pl1On = (v & (1ul << 15)) != 0;
                t.Pl2 = ((v >> 32) & 0x7FFF) * powerUnit; t.Pl2On = (v & (1ul << 47)) != 0;
                t.PlLocked = (v & (1ul << 63)) != 0;
            }
            if (energyUnit > 0 && Read(MSR_PKG_ENERGY_STATUS, out v)) t.Watts = Power(v & 0xFFFFFFFFu, ref lastEnergy, ref lastEnergyAt, energyUnit);
            return t;
        }
        public override void Dispose() { m.Dispose(); }
    }

    /// <summary>AMD Ryzen (family 17h to 1Ah), through the AMDFamily17 module.</summary>
    sealed class AmdCpu : CpuRegisters {
        const uint THM_TCON_CUR_TMP = 0x00059800;
        const uint MSR_PWR_UNIT = 0xC0010299, MSR_PKG_ENERGY_STAT = 0xC001029B;
        readonly PawnIoModule m;
        readonly Mutex pci;                 // the SMN goes through PCI config space, which every tool serialises on this name
        double energyUnit;
        ulong lastEnergy; DateTime lastEnergyAt = DateTime.MinValue;
        public AmdCpu(PawnIoModule module) { m = module; try { pci = new Mutex(false, @"Global\Access_PCI"); } catch { } }
        public override string Describe { get { return "AMD SMN + MSR"; } }

        bool Msr(uint msr, out ulong v) { var o = new ulong[1]; bool ok = m.Call("ioctl_read_msr", new ulong[] { msr }, o); v = ok ? o[0] : 0; return ok; }
        bool Smn(uint addr, out uint v) {
            v = 0;
            bool held = false;
            try {
                if (pci != null) { try { held = pci.WaitOne(250); } catch (AbandonedMutexException) { held = true; } }
                var o = new ulong[1];
                if (!m.Call("ioctl_read_smn", new ulong[] { addr }, o)) return false;
                v = (uint)o[0];
                return true;
            } finally { if (held) { try { pci.ReleaseMutex(); } catch { } } }
        }

        protected override CpuTelemetry PollCore() {
            var t = new CpuTelemetry();
            uint r;
            if (Smn(THM_TCON_CUR_TMP, out r)) {
                // bits 31:21 are the temperature in 1/8 degree; bit 19 says the range starts at -49 (Zen 2+ ranges).
                double tctl = ((r >> 21) & 0x7FF) * 0.125;
                if ((r & (1u << 19)) != 0) tctl -= 49;
                if (tctl > 0 && tctl < 130) t.DieTemp = tctl;
            }
            ulong v;
            if (energyUnit == 0 && Msr(MSR_PWR_UNIT, out v)) energyUnit = 1.0 / (1 << (int)((v >> 8) & 0x1F));
            if (energyUnit > 0 && Msr(MSR_PKG_ENERGY_STAT, out v)) t.Watts = Power(v & 0xFFFFFFFFu, ref lastEnergy, ref lastEnergyAt, energyUnit);
            return t;
        }
        public override void Dispose() { m.Dispose(); try { if (pci != null) pci.Close(); } catch { } }
    }

    /// <summary>Simulated registers for the preview build: the rows render, nothing is read.</summary>
    public sealed class DemoCpu : CpuRegisters {
        readonly Random rnd = new Random();
        double temp = 52;
        public override string Describe { get { return "simulated"; } }
        protected override CpuTelemetry PollCore() {
            temp = Math.Max(38, Math.Min(88, temp + rnd.Next(-2, 3)));
            return new CpuTelemetry { DieTemp = temp, TjMax = 100, Pl1 = 45, Pl2 = 115, Pl1On = true, Pl2On = true, Watts = 12 + rnd.Next(0, 9), Throttle = temp > 84 ? "power limit" : "" };
        }
        public override void Dispose() { }
    }
}
