// Live sound level of Apple Music / iTunes for the hover popup's level meter.
// Uses the Windows audio session meters (Core Audio, Windows Vista+): each app's own output,
// so other sounds on the PC don't move the meter. Only read while the popup is open.
// C# 5 (built-in Windows compiler).

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ITunesScrobbler
{
    static class AudioMeter
    {
        // ---- Core Audio COM interfaces (only the methods used; vtable order matters)
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
            [PreserveSig] int GetGroupingParam(out Guid groupingParam);
            [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out int count);
            [PreserveSig] int GetChannelsPeakValues(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] values);
        }

        const int ERender = 0, EMultimedia = 1, ClsctxAll = 23, SessionActive = 1;
        static readonly string[] Players = { "AppleMusic", "iTunes" };

        static IAudioMeterInformation meter;      // the player's own session, or the whole output as a fallback
        static bool wholeOutput;
        static DateTime nextLookup = DateTime.MinValue;
        static readonly float[] peaks = new float[8];

        // Peak levels (0..1) of the left and right channel since the last call. False when nothing can be read.
        // Call from the UI thread only.
        public static bool Read(out float left, out float right)
        {
            left = right = 0;
            // look for the player's session now and then: it appears when playback starts,
            // and a fallback to the whole output switches over once it's there
            if ((meter == null || wholeOutput) && DateTime.UtcNow >= nextLookup)
            {
                nextLookup = DateTime.UtcNow.AddSeconds(2);
                Find();
            }
            if (meter == null) return false;
            try
            {
                int n;
                if (meter.GetMeteringChannelCount(out n) != 0) throw new COMException("channel count");
                if (n >= 2)
                {
                    n = Math.Min(n, peaks.Length);
                    if (meter.GetChannelsPeakValues(n, peaks) != 0) throw new COMException("peaks");
                    left = peaks[0]; right = peaks[1];
                }
                else
                {
                    float p;
                    if (meter.GetPeakValue(out p) != 0) throw new COMException("peak");
                    left = right = p;
                }
                return true;
            }
            catch
            {
                Release();   // device changed, player closed...: look again on the next call
                return false;
            }
        }

        // lets go of the audio objects (the popup closed)
        public static void Release()
        {
            if (meter != null) try { Marshal.ReleaseComObject(meter); } catch { }
            meter = null; wholeOutput = false;
            nextLookup = DateTime.MinValue;
        }

        static void Find()
        {
            object enumObj = null, mgrObj = null, endpointMeter = null;
            IMMDevice device = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                enumObj = new MMDeviceEnumeratorCom();
                var en = (IMMDeviceEnumerator)enumObj;
                if (en.GetDefaultAudioEndpoint(ERender, EMultimedia, out device) != 0 || device == null) return;

                var iidMgr = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iidMgr, ClsctxAll, IntPtr.Zero, out mgrObj) == 0 && mgrObj != null
                    && ((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) == 0 && sessions != null)
                {
                    object best = null; bool bestActive = false;
                    int count;
                    sessions.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        object s;
                        if (sessions.GetSession(i, out s) != 0 || s == null) continue;
                        bool keep = false;
                        try
                        {
                            var c2 = (IAudioSessionControl2)s;
                            uint pid; int state;
                            if (c2.GetProcessId(out pid) == 0 && pid != 0 && IsPlayer(pid))
                            {
                                c2.GetState(out state);
                                bool active = state == SessionActive;
                                if (best == null || (active && !bestActive)) { if (best != null) Marshal.ReleaseComObject(best); best = s; bestActive = active; keep = true; }
                            }
                        }
                        catch { }
                        if (!keep) try { Marshal.ReleaseComObject(s); } catch { }
                    }
                    if (best != null)
                    {
                        if (meter != null) try { Marshal.ReleaseComObject(meter); } catch { }
                        meter = (IAudioMeterInformation)best;
                        wholeOutput = false;
                        Diag.Once("meter-session", "Level meter: reading the player's own sound");
                        return;
                    }
                }

                // the player's session isn't there (yet): show the whole output meanwhile
                if (meter == null)
                {
                    var iidMeter = typeof(IAudioMeterInformation).GUID;
                    if (device.Activate(ref iidMeter, ClsctxAll, IntPtr.Zero, out endpointMeter) == 0 && endpointMeter != null)
                    {
                        meter = (IAudioMeterInformation)endpointMeter;
                        endpointMeter = null;
                        wholeOutput = true;
                        Diag.Once("meter-output", "Level meter: Apple Music's own audio session not found, showing the PC's whole output");
                    }
                }
            }
            catch (Exception ex) { Diag.Once("meter-error:" + ex.GetType().Name, "Level meter unavailable: " + ex.Message); }
            finally
            {
                if (sessions != null) try { Marshal.ReleaseComObject(sessions); } catch { }
                if (mgrObj != null) try { Marshal.ReleaseComObject(mgrObj); } catch { }
                if (endpointMeter != null) try { Marshal.ReleaseComObject(endpointMeter); } catch { }
                if (device != null) try { Marshal.ReleaseComObject(device); } catch { }
                if (enumObj != null) try { Marshal.ReleaseComObject(enumObj); } catch { }
            }
        }

        static bool IsPlayer(uint pid)
        {
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    foreach (var name in Players) if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        // ---- level -> number of lit blocks (13: 9 green, 2 yellow, 2 red), car-stereo style
        public const int Segments = 13;
        static readonly double[] Steps = { -40, -32, -26, -21, -17, -14, -11, -8.5, -6.5, -4.5, -3, -1.5, -0.5 };   // dBFS each block lights at

        public static int Lit(float peak)
        {
            if (peak <= 0.0001f) return 0;
            double db = 20 * Math.Log10(peak);
            int n = 0;
            while (n < Steps.Length && db >= Steps[n]) n++;
            return n;
        }
    }
}
