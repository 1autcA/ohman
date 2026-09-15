// SPDX-License-Identifier: GPL-3.0-or-later
// Ohman — the Start menu entry. One .lnk in the per-user Programs folder, which is all it takes to be
// searchable: Start indexes that folder, so typing the name finds the app without an installer, a registry
// key or anything that needs administrator.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ohman {

    public static class Shortcut {
        /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Ohman.lnk — per user, no elevation needed even
        /// though we have it. The all-users folder would need cleaning up by an administrator later, and Ohman is
        /// a file somebody downloaded, not something installed for the machine.</summary>
        public static string Path {
            get {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, Program.DisplayName + ".lnk");
            }
        }

        /// <summary>Put it there, or correct it if it points somewhere else. Called on every launch: the owner can
        /// move the exe, and a shortcut that opens nothing is worse than none at all. Writing it only when it is
        /// wrong matters — rewriting the file on each start would keep re-adding it to Start's "recently added"
        /// list and disturb whatever ranking it has earned.</summary>
        public static void Ensure() {
            try {
                string path = Path, exe = Update.ExePath;
                if (path == null || string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                if (File.Exists(path) && string.Equals(TargetOf(path), exe, StringComparison.OrdinalIgnoreCase)) return;
                Write(path, exe);
                Log.Write("start menu: " + (File.Exists(path) ? "wrote " : "could not write ") + path);
            } catch (Exception ex) { Log.Write("start menu: " + ex.Message); }
        }

        /// <summary>Take it away again. Part of Uninstall: the dialog promises nothing is left behind.</summary>
        public static void Remove() {
            try {
                string path = Path;
                if (path != null && File.Exists(path)) { File.Delete(path); Log.Write("start menu: removed " + path); }
            } catch (Exception ex) { Log.Write("start menu remove: " + ex.Message); }
        }

        // WScript.Shell late-bound rather than IShellLink: one COM object, no interface declarations, and nothing
        // to reference. It is an in-process server present on every Windows since 98, so there is no install to
        // check for — but it is still COM, so every object is released rather than left to a finaliser.
        static void Write(string path, string exe) {
            object shell = null, link = null;
            try {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                shell = Activator.CreateInstance(t);
                link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Set(link, "TargetPath", exe);
                Set(link, "WorkingDirectory", System.IO.Path.GetDirectoryName(exe));
                Set(link, "IconLocation", exe + ",0");
                Set(link, "Description", Program.DisplayName + " — fan, thermal and keyboard lighting control");
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            } finally {
                Release(link);
                Release(shell);
            }
        }
        static void Set(object o, string name, object v) {
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { v });
        }
        static string TargetOf(string path) {
            object shell = null, link = null;
            try {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                return link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            } catch { return null; } finally {
                Release(link);
                Release(shell);
            }
        }
        static void Release(object o) {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }
    }
}
