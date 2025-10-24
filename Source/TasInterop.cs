// RLBridge/Source/TasInterop.cs
using System;
using System.Numerics;
using System.Reflection;
using Microsoft.Xna.Framework;

namespace RLBridge
{
    /// <summary>
    /// Reflection-based bridge to CelesteTAS-EverestInterop.
    /// We try a few known type/method names; if not found, IsAvailable=false.
    /// </summary>
    public class TasInterop
    {
        public bool IsAvailable { get; private set; }

        private MethodInfo miAdvanceOneFrame;
        private MethodInfo miSetInputs;     // expects something like (float moveX, bool jump, bool dash, bool grab, Vector2 aim)
        private MethodInfo miSetSeed;

        public TasInterop Bind()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies();
                Type tInterop =
                    FindType(asm, "CelesteTAS.EverestInterop") ??
                    FindType(asm, "CelesteTAS.EverestInterop.TASInterop") ??
                    FindType(asm, "TAS.EverestInterop");

                if (tInterop != null)
                {
                    miAdvanceOneFrame = tInterop.GetMethod("AdvanceOneFrame", BindingFlags.Public | BindingFlags.Static);
                    miSetInputs       = tInterop.GetMethod("SetInputs",       BindingFlags.Public | BindingFlags.Static);
                    miSetSeed         = tInterop.GetMethod("SetUniversalSeed",BindingFlags.Public | BindingFlags.Static);
                    IsAvailable = miAdvanceOneFrame != null && miSetInputs != null;
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

        public bool AdvanceOneFrame()
        {
            if (!IsAvailable || miAdvanceOneFrame == null) return false;
            miAdvanceOneFrame.Invoke(null, null);
            return true;
        }

        public bool SetInputs(InputState a)
        {
            if (!IsAvailable || miSetInputs == null) return false;
            // Try calling SetInputs(moveX, jumpTap, dash, grab, aimX, aimY)
            try
            {
                object[] args = new object[] { (float)a.MoveX, a.JumpTap, a.Dash, a.Grab, a.Aim.X, a.Aim.Y };
                miSetInputs.Invoke(null, args);
                return true;
            }
            catch { return false; }
        }

        public bool SetUniversalSeed(int seed)
        {
            if (miSetSeed == null) return false;
            try { miSetSeed.Invoke(null, new object[] { seed }); return true; }
            catch { return false; }
        }
    }
}
