// SPDX-License-Identifier: GPL-3.0-only
// TalkMonitor.cs: watches the audio sessions of other programs (CrewChief, a spotter, Discord) and says when one of
// them is talking (#58). WASAPI keeps one session per process per render device, with a peak meter; every 50 ms, on
// its own MTA thread, the monitor reads the meters of the sessions whose process name is in the settings' list (on
// every active render device, since CrewChief may not play on the device rigPlay uses) and feeds the highest peak to a
// TalkGate. The session list and the device list are refreshed every 2 s, which also catches a program that starts
// later. TalkingChanged is raised on the monitor thread; AudioOutput ducks the media mix on it and TalkPauser toggles
// the phone. Without NAudio or any device the monitor reports why and stays quiet. Kept thin: the decision logic is
// in TalkGate and TalkPauser, which are unit-tested.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using NAudio.CoreAudioApi;

namespace RigPlayPlugin.Audio
{
    public sealed class TalkMonitor : IDisposable
    {
        public const int PollMs = 50;
        public const int RefreshMs = 2000;

        private readonly Func<RigPlaySettings> settings;
        private readonly TalkGate gate = new TalkGate();
        private readonly Thread worker;
        private readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();
        private readonly List<WatchedSession> watched = new List<WatchedSession>();
        private readonly List<MMDevice> devices = new List<MMDevice>();
        private MMDeviceEnumerator enumerator;
        private long lastRefreshMs = long.MinValue;
        private string lastProcessList;
        private volatile bool disposed;
        private volatile bool talking;
        private volatile string status = "Off";
        private volatile string loudest = "";

        private sealed class WatchedSession
        {
            public AudioSessionControl Session;
            public string Process;
        }

        public TalkMonitor(Func<RigPlaySettings> settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            worker = new Thread(Run) { IsBackground = true, Name = "rigPlay talk monitor" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        /// <summary>A watched program started (true) or stopped (false) talking. Raised on the monitor thread.</summary>
        public event Action<bool> TalkingChanged;

        public bool Talking
        {
            get { return talking; }
        }

        /// <summary>One line for the page, e.g. "CrewChiefV4 is talking", "CrewChiefV4: quiet", "no watched program is playing audio".</summary>
        public string Status()
        {
            return status;
        }

        public void Dispose()
        {
            disposed = true;
            try { worker.Join(2000); } catch { }
        }

        private void Run()
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
            }
            catch (Exception ex)
            {
                status = "Unavailable: " + ex.Message;
                Log.Warn("The talk monitor cannot enumerate audio devices: " + ex.Message);
                return;
            }
            var clock = Stopwatch.StartNew();
            while (!disposed)
            {
                try
                {
                    Tick(clock.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    status = "Error: " + ex.Message;
                    Log.Warn("The talk monitor failed: " + ex.Message);
                    ReleaseSessions();
                    lastRefreshMs = long.MinValue;
                }
                Thread.Sleep(PollMs);
            }
            ReleaseSessions();
            ReleaseDevices();
            try { enumerator?.Dispose(); } catch { }
        }

        private void Tick(long nowMs)
        {
            var s = settings();
            if (s == null || !s.TalkWatchEnabled || s.TalkProcesses.Count == 0)
            {
                if (watched.Count > 0 || devices.Count > 0)
                {
                    ReleaseSessions();
                    ReleaseDevices();
                }
                status = s != null && s.TalkWatchEnabled ? "No program to watch" : "Off";
                if (gate.Reset()) SetTalking(false);
                return;
            }

            var list = string.Join(",", s.TalkProcesses);
            if (lastRefreshMs == long.MinValue || nowMs - lastRefreshMs >= RefreshMs || !string.Equals(list, lastProcessList, StringComparison.Ordinal))
            {
                Refresh(s.TalkProcesses);
                lastRefreshMs = nowMs;
                lastProcessList = list;
            }

            var peak = -1.0;
            string who = null;
            for (var i = watched.Count - 1; i >= 0; i--)
            {
                var w = watched[i];
                float p;
                try
                {
                    p = w.Session.AudioMeterInformation.MasterPeakValue;
                }
                catch (Exception)
                {
                    // The session went away (the program closed): drop it until the next refresh.
                    try { w.Session.Dispose(); } catch { }
                    watched.RemoveAt(i);
                    continue;
                }
                if (p > peak)
                {
                    peak = p;
                    who = w.Process;
                }
            }
            if (who != null) loudest = who;

            var changed = gate.Update(peak, nowMs);
            status = watched.Count == 0
                ? "No watched program is playing audio (" + list + ")"
                : gate.Talking
                    ? loudest + " is talking"
                    : loudest + ": quiet (" + AudioMath.GainToDb(Math.Max(gate.LastPeak, 1e-5)).ToString("0", CultureInfo.InvariantCulture) + " dBFS)";
            if (changed) SetTalking(gate.Talking);
        }

        private void SetTalking(bool value)
        {
            talking = value;
            try { TalkingChanged?.Invoke(value); } catch (Exception ex) { Log.Warn("A talk listener failed: " + ex.Message); }
        }

        /// <summary>Re-reads the render devices and their sessions, keeping those whose process is watched.</summary>
        private void Refresh(List<string> processes)
        {
            ReleaseSessions();
            ReleaseDevices();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(device);
                AudioSessionManager manager;
                try
                {
                    manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                }
                catch (Exception)
                {
                    continue;
                }
                var sessions = manager.Sessions;
                if (sessions == null) continue;
                for (var i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl session = null;
                    try
                    {
                        session = sessions[i];
                        var name = ProcessName(session.GetProcessID);
                        if (name != null && TalkProcessMatch.Matches(processes, name))
                        {
                            watched.Add(new WatchedSession { Session = session, Process = name });
                            session = null;
                        }
                    }
                    catch (Exception)
                    {
                        // A session we cannot read; skip it.
                    }
                    finally
                    {
                        try { session?.Dispose(); } catch { }
                    }
                }
            }
        }

        private string ProcessName(uint pid)
        {
            if (pid == 0) return null;
            string name;
            if (processNames.TryGetValue(pid, out name)) return name;
            try
            {
                using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName;
            }
            catch (Exception)
            {
                name = null;
            }
            if (processNames.Count > 512) processNames.Clear();
            processNames[pid] = name;
            return name;
        }

        private void ReleaseSessions()
        {
            foreach (var w in watched)
            {
                try { w.Session.Dispose(); } catch { }
            }
            watched.Clear();
            processNames.Clear();
        }

        private void ReleaseDevices()
        {
            foreach (var d in devices)
            {
                try { d.Dispose(); } catch { }
            }
            devices.Clear();
        }
    }
}
