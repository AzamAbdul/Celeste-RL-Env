// RLBridge/Source/SpeedrunToolInterop.cs
using System;
using System.Reflection;

namespace RLBridge
{
    /// <summary>
    /// Reflection-based bridge to Speedrun Tool's savestate API.
    /// We look for a static LoadState(int slot) method.
    /// </summary>
    public class SpeedrunToolInterop
    {
        public bool IsAvailable { get; private set; }
        private MethodInfo miLoadState;
        private MethodInfo miSaveState;

        public SpeedrunToolInterop Bind()
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                // Try a few namespaces users report; adjust if needed
                Type t = FindType(asms, "SpeedrunTool.SaveLoad") ??
                         FindType(asms, "SpeedrunToolInterop.SaveLoad") ??
                         FindType(asms, "SpeedrunTool.SaveStateManager");
                if (t != null)
                {
                    miLoadState = t.GetMethod("LoadState", BindingFlags.Public | BindingFlags.Static);
                    miSaveState = t.GetMethod("SaveState", BindingFlags.Public | BindingFlags.Static);
                    IsAvailable = miLoadState != null;
                }
            }
            catch { IsAvailable = false; }
            return this;
        }

        private static Type FindType(Assembly[] asms, string fullName)
        {
            foreach (var a in asms)
            {
                var t = a.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        public bool LoadSlot(int slot)
        {
            if (!IsAvailable || miLoadState == null) return false;
            try { miLoadState.Invoke(null, new object[] { slot }); return true; }
            catch { return false; }
        }

        public bool SaveSlot(int slot)
        {
            if (miSaveState == null) return false;
            try { miSaveState.Invoke(null, new object[] { slot }); return true; }
            catch { return false; }
        }
    }
}
