// Copyright 2022 Bingxing Wang
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// If you are Microsoft (and/or its affiliates) employee, vendor or contractor who is working on Windows-specific integration projects, you may use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so without the restriction above.

using EnergyStarX.Contracts.Services;
using EnergyStarX.Interop;
using Microsoft.Windows.System.Power;
using NLog;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EnergyStarX.Services;

public class EnergyService : IEnergyService
{
    private static readonly Logger logger = LogManager.GetCurrentClassLogger();
    private readonly object lockObject = new();
    private CancellationTokenSource houseKeepingCancellationTokenSource = new();

    private readonly IWindowService windowService;
    private readonly ISettingsService settingsService;

    // Speical handling needs for UWP to get the child window process
    private const string UWPFrameHostApp = "ApplicationFrameHost.exe";

    private readonly IntPtr pThrottleOn;
    private readonly IntPtr pThrottleOff;
    private readonly int szControlBlock;

    private uint pendingProcPid = 0;
    private string pendingProcName = "";

    /// <summary>
    /// Priority class of each throttled process before it got throttled, so it can be restored when throttling stops.
    /// Keyed by process id.
    /// </summary>
    private readonly Dictionary<uint, Win32Api.PriorityClass> originalPriorityClasses = new();

    public ThrottleStatus ThrottleStatus { get; private set; } = ThrottleStatus.Stopped;

    private bool pauseThrottling = false;

    public bool PauseThrottling
    {
        get => pauseThrottling;
        set
        {
            lock (lockObject)
            {
                if (pauseThrottling != value)
                {
                    pauseThrottling = value;
                    UpdateThrottleStatusAndNotify();
                }
            }
        }
    }

    public bool ThrottleWhenPluggedIn
    {
        get => settingsService.ThrottleWhenPluggedIn;
        set
        {
            lock (lockObject)
            {
                if (settingsService.ThrottleWhenPluggedIn != value)
                {
                    settingsService.ThrottleWhenPluggedIn = value;
                    UpdateThrottleStatusAndNotify();
                }
            }
        }
    }

    public bool IsOnBattery => PowerManager.PowerSourceKind == PowerSourceKind.DC;

    /// <inheritdoc/>
    public IReadOnlySet<string> ProcessWhitelist { get; private set; } = new HashSet<string>();

    /// <summary>
    /// A subset of <see cref="ProcessWhitelist" />, where the process name contains "?" or "*".
    /// </summary>
    private IReadOnlySet<string> WildcardProcessWhitelist { get; set; } = new HashSet<string>();

    /// <inheritdoc/>
    public IReadOnlySet<string> ProcessBlacklist { get; private set; } = new HashSet<string>();

    /// <summary>
    /// A subset of <see cref="ProcessBlacklist" />, where the process name contains "?" or "*".
    /// </summary>
    private IReadOnlySet<string> WildcardProcessBlacklist { get; set; } = new HashSet<string>();

    public event EventHandler<ThrottleStatus>? ThrottleStatusChanged;

    public EnergyService(IWindowService windowService, ISettingsService settingsService)
    {
        szControlBlock = Marshal.SizeOf<Win32Api.PROCESS_POWER_THROTTLING_STATE>();
        pThrottleOn = Marshal.AllocHGlobal(szControlBlock);
        pThrottleOff = Marshal.AllocHGlobal(szControlBlock);

        Win32Api.PROCESS_POWER_THROTTLING_STATE throttleState = new()
        {
            Version = Win32Api.PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = Win32Api.ProcessorPowerThrottlingFlags.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = Win32Api.ProcessorPowerThrottlingFlags.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };

        Win32Api.PROCESS_POWER_THROTTLING_STATE unthrottleState = new()
        {
            Version = Win32Api.PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = Win32Api.ProcessorPowerThrottlingFlags.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = Win32Api.ProcessorPowerThrottlingFlags.None,
        };

        Marshal.StructureToPtr(throttleState, pThrottleOn, false);
        Marshal.StructureToPtr(unthrottleState, pThrottleOff, false);

        this.windowService = windowService;
        this.settingsService = settingsService;

        this.windowService.AppExiting += WindowService_AppExiting;
    }

    public void Initialize()
    {
        lock (lockObject)
        {
            HookManager.SubscribeToWindowEvents();
            HookManager.SystemForegroundWindowChanged += HookManager_SystemForegroundWindowChanged;

            ApplyProcessWhitelist(settingsService.ProcessWhitelistString);
            ApplyProcessBlacklist(settingsService.ProcessBlacklistString);
            UpdateThrottleStatusAndNotify();
            PowerManager.PowerSourceKindChanged += PowerManager_PowerSourceKindChanged;
        }
    }

    private void WindowService_AppExiting(object? sender, EventArgs e)
    {
        lock (lockObject)
        {
            PowerManager.PowerSourceKindChanged -= PowerManager_PowerSourceKindChanged;
            StopThrottling(ThrottleStatus);

            HookManager.SystemForegroundWindowChanged -= HookManager_SystemForegroundWindowChanged;
            HookManager.UnsubscribeWindowEvents();
        };
    }

    public void ApplyAndSaveProcessWhitelist(string processWhitelistString)
    {
        lock (lockObject)
        {
            ApplyProcessWhitelist(processWhitelistString);
            settingsService.ProcessWhitelistString = processWhitelistString;
            logger.Info("ProcessWhitelist saved");
        }
    }

    public void ApplyProcessWhitelist(string processWhitelistString)
    {
        lock (lockObject)
        {
            ThrottleStatus previousThrottleStatus = ThrottleStatus;

            if (previousThrottleStatus != ThrottleStatus.Stopped)
            {
                StopThrottling(previousThrottleStatus);
            }

            (HashSet<string> processWhitelist, HashSet<string> wildcardProcessWhitelist) = ParseProcessList(processWhitelistString);
#if DEBUG
            processWhitelist.Add("devenv.exe");    // Visual Studio
#endif
            ProcessWhitelist = processWhitelist;
            WildcardProcessWhitelist = wildcardProcessWhitelist;

            logger.Info("Apply ProcessWhitelist:\n{0}", string.Join(Environment.NewLine, processWhitelist));

            if (previousThrottleStatus != ThrottleStatus.Stopped)
            {
                StartThrottling(previousThrottleStatus);
            }
        }
    }

    public void ApplyAndSaveProcessBlacklist(string processBlacklistString)
    {
        lock (lockObject)
        {
            ApplyProcessBlacklist(processBlacklistString);
            settingsService.ProcessBlacklistString = processBlacklistString;
            logger.Info("ProcessBlacklist saved");
        }
    }

    public void ApplyProcessBlacklist(string processBlacklistString)
    {
        lock (lockObject)
        {
            ThrottleStatus previousThrottleStatus = ThrottleStatus;

            if (previousThrottleStatus != ThrottleStatus.Stopped)
            {
                StopThrottling(previousThrottleStatus);
            }

            (HashSet<string> processBlacklist, HashSet<string> wildcardProcessBlacklist) = ParseProcessList(processBlacklistString);
            ProcessBlacklist = processBlacklist;
            WildcardProcessBlacklist = wildcardProcessBlacklist;

            logger.Info("Apply ProcessBlacklist:\n{0}", string.Join(Environment.NewLine, processBlacklist));

            if (previousThrottleStatus != ThrottleStatus.Stopped)
            {
                StartThrottling(previousThrottleStatus);
            }
        }
    }

    /// <summary>
    /// Get process name list from <paramref name="processListString"/>. 
    /// <br />
    /// Each line of <paramref name="processListString"/> contains one process name;
    /// Double slash and content after it in each line will be ignored.
    /// </summary>
    /// <returns>
    /// In the returned tuple, "wildcardProcessList" is a subset of "fullProcessList", where the process name contains "?" or "*".
    /// </returns>
    private (HashSet<string> fullProcessList, HashSet<string> wildcardProcessList) ParseProcessList(string processListString)
    {
        HashSet<string> fullProcessList = new();
        HashSet<string> wildcardProcessList = new();

        Regex doubleSlashRegex = new("//");

        using StringReader stringReader = new(processListString);
        while (stringReader.ReadLine() is string line)
        {
            Match doubleSlashMatch = doubleSlashRegex.Match(line);
            string processName = (doubleSlashMatch.Success ? line[..doubleSlashMatch.Index] : line).Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(processName))
            {
                fullProcessList.Add(processName);

                if (processName.Contains("?") || processName.Contains("*"))
                {
                    wildcardProcessList.Add(processName);
                }
            }
        }

        return (fullProcessList, wildcardProcessList);
    }

    /// <summary>
    /// Returns true if <see cref="ThrottleStatus"/> changes after this method executes. Otherwise false.
    /// </summary>
    private bool UpdateThrottleStatusAndNotify()
    {
        lock (lockObject)
        {
            ThrottleStatus fromThrottleStatus = ThrottleStatus;
            ThrottleStatus toThrottleStatus = (PauseThrottling, IsOnBattery, ThrottleWhenPluggedIn) switch
            {
                (true, _, _) => ThrottleStatus.Stopped,
                (false, true, _) => ThrottleStatus.BlacklistAndAllButWhitelist,
                (false, false, true) => ThrottleStatus.BlacklistAndAllButWhitelist,
                (false, false, false) => ThrottleStatus.OnlyBlacklist
            };

            bool throttleStatusChanged = (fromThrottleStatus, toThrottleStatus) switch
            {
                (ThrottleStatus.Stopped, ThrottleStatus.OnlyBlacklist) => StartThrottling(toThrottleStatus),
                (ThrottleStatus.Stopped, ThrottleStatus.BlacklistAndAllButWhitelist) => StartThrottling(toThrottleStatus),

                (ThrottleStatus.OnlyBlacklist, ThrottleStatus.Stopped) => StopThrottling(fromThrottleStatus),
                (ThrottleStatus.OnlyBlacklist, ThrottleStatus.BlacklistAndAllButWhitelist) => ThrottleUserBackgroundProcesses(toThrottleStatus),

                (ThrottleStatus.BlacklistAndAllButWhitelist, ThrottleStatus.Stopped) => StopThrottling(fromThrottleStatus),
                (ThrottleStatus.BlacklistAndAllButWhitelist, ThrottleStatus.OnlyBlacklist) => RecoverUserProcesses(fromThrottleStatus) && ThrottleUserBackgroundProcesses(toThrottleStatus),

                _ when fromThrottleStatus == toThrottleStatus => false,
                _ => throw new ArgumentException($"Unknown ThrottleStatus transition: {fromThrottleStatus} -> {toThrottleStatus}")
            };

            if (throttleStatusChanged)
            {
                ThrottleStatus = toThrottleStatus;
                ThrottleStatusChanged?.Invoke(this, ThrottleStatus);
                logger.Info("ThrottleStatus changed to: {0}", toThrottleStatus);
            }

            return throttleStatusChanged;
        }
    }

    /// <summary>
    /// Start throttling to <paramref name="toThrottleStatus"/>.
    /// </summary>
    ///
    /// <returns>
    /// Returns true if <paramref name="toThrottleStatus"/> is not <see cref="ThrottleStatus.Stopped"/> and throttling has started.
    /// Otherwise false.
    /// </returns>
    private bool StartThrottling(ThrottleStatus toThrottleStatus)
    {
        lock (lockObject)
        {
            if (toThrottleStatus == ThrottleStatus.Stopped)
            {
                return false;
            }

            logger.Info("Start throttling");
            ThrottleUserBackgroundProcesses(toThrottleStatus);
            houseKeepingCancellationTokenSource = new CancellationTokenSource();
            _ = HouseKeeping(houseKeepingCancellationTokenSource.Token);

            return true;
        }
    }

    /// <summary>
    /// Stop throttling from <paramref name="fromThrottleStatus"/>.
    /// </summary>
    ///
    /// <returns>
    /// Returns true if <paramref name="fromThrottleStatus"/> is not <see cref="ThrottleStatus.Stopped"/> and throttling has stopped.
    /// Otherwise false.
    /// </returns>
    private bool StopThrottling(ThrottleStatus fromThrottleStatus)
    {
        lock (lockObject)
        {
            if (fromThrottleStatus == ThrottleStatus.Stopped)
            {
                return false;
            }

            logger.Info("Stop throttling");
            houseKeepingCancellationTokenSource.Cancel();
            RecoverUserProcesses(fromThrottleStatus);

            return true;
        }
    }

    /// <summary>
    /// Throttle background processes according to <paramref name="toThrottleStatus"/>.
    /// </summary>
    ///
    /// <returns>
    /// Returns true if <paramref name="toThrottleStatus"/> is not <see cref="ThrottleStatus.Stopped"/> and user background processes got throttled.
    /// Otherwise false.
    /// </returns>
    private bool ThrottleUserBackgroundProcesses(ThrottleStatus toThrottleStatus)
    {
        lock (lockObject)
        {
            if (toThrottleStatus == ThrottleStatus.Stopped)
            {
                return false;
            }

            Process[] runningProcesses = Process.GetProcesses();
            int currentSessionID = Process.GetCurrentProcess().SessionId;

            IEnumerable<Process> sameAsThisSession = runningProcesses.Where(p => p.SessionId == currentSessionID);
            foreach (Process proc in sameAsThisSession)
            {
                if (proc.Id == pendingProcPid) { continue; }
                if (ShouldBypassProcess($"{proc.ProcessName}.exe".ToLowerInvariant(), toThrottleStatus)) { continue; }
                IntPtr hProcess = Win32Api.OpenProcess((uint)(Win32Api.ProcessAccessFlags.QueryLimitedInformation |
                    Win32Api.ProcessAccessFlags.SetInformation), false, (uint)proc.Id);
                if (hProcess == IntPtr.Zero)
                {
                    logger.Debug("Failed to open process {0} (pid {1}) to throttle it. Win32 error: {2}",
                        proc.ProcessName, proc.Id, Marshal.GetLastWin32Error());
                    continue;
                }

                ThrottleProcess(hProcess, (uint)proc.Id);
                Win32Api.CloseHandle(hProcess);
            }

            return true;
        }
    }

    /// <summary>
    /// Recover throttled user processes from <paramref name="fromThrottleStatus"/>.
    /// </summary>
    ///
    /// <returns>
    /// Returns true if <paramref name="fromThrottleStatus"/> is not <see cref="ThrottleStatus.Stopped"/> and user processes got recovered from being throttled.
    /// Otherwise false.
    /// </returns>
    private bool RecoverUserProcesses(ThrottleStatus fromThrottleStatus)
    {
        lock (lockObject)
        {
            if (fromThrottleStatus == ThrottleStatus.Stopped)
            {
                return false;
            }

            Process[] runningProcesses = Process.GetProcesses();
            int currentSessionID = Process.GetCurrentProcess().SessionId;

            IEnumerable<Process> sameAsThisSession = runningProcesses.Where(p => p.SessionId == currentSessionID);
            foreach (Process proc in sameAsThisSession)
            {
                if (ShouldBypassProcess($"{proc.ProcessName}.exe".ToLowerInvariant(), fromThrottleStatus)) { continue; }
                IntPtr hProcess = Win32Api.OpenProcess((uint)(Win32Api.ProcessAccessFlags.QueryLimitedInformation |
                    Win32Api.ProcessAccessFlags.SetInformation), false, (uint)proc.Id);
                if (hProcess == IntPtr.Zero)
                {
                    logger.Debug("Failed to open process {0} (pid {1}) to unthrottle it. Win32 error: {2}",
                        proc.ProcessName, proc.Id, Marshal.GetLastWin32Error());
                    continue;
                }

                UnthrottleProcess(hProcess, (uint)proc.Id);
                Win32Api.CloseHandle(hProcess);
            }

            return true;
        }
    }

    private async Task HouseKeeping(CancellationToken cancellationToken)
    {
        logger.Info("House keeping task started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                ThrottleUserBackgroundProcesses(ThrottleStatus);
                logger.Info("House keeping task throttling background processes");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                logger.Error(e, "House keeping task error");
            }
        }

        logger.Info("House keeping task stopped.");
    }

    private void HookManager_SystemForegroundWindowChanged(object? sender, IntPtr hwnd)
    {
        lock (lockObject)
        {
            if (ThrottleStatus == ThrottleStatus.Stopped) { return; }

            uint windowThreadId = Win32Api.GetWindowThreadProcessId(hwnd, out uint procId);
            // This is invalid, likely a process is dead, or idk
            if (windowThreadId == 0 || procId == 0) { return; }

            IntPtr procHandle = Win32Api.OpenProcess(
                (uint)(Win32Api.ProcessAccessFlags.QueryLimitedInformation | Win32Api.ProcessAccessFlags.SetInformation), false, procId);
            if (procHandle == IntPtr.Zero) { return; }

            // Get the process
            string appName = GetProcessNameFromHandle(procHandle);

            // UWP needs to be handled in a special case
            if (appName == UWPFrameHostApp)
            {
                bool found = false;
                Win32Api.EnumChildWindows(hwnd, (innerHwnd, lparam) =>
                {
                    if (found) { return true; }
                    if (Win32Api.GetWindowThreadProcessId(innerHwnd, out uint innerProcId) > 0)
                    {
                        if (procId == innerProcId) { return true; }

                        IntPtr innerProcHandle = Win32Api.OpenProcess((uint)(Win32Api.ProcessAccessFlags.QueryLimitedInformation |
                            Win32Api.ProcessAccessFlags.SetInformation), false, innerProcId);
                        if (innerProcHandle == IntPtr.Zero) { return true; }

                        // Found. Set flag, reinitialize handles and call it a day
                        found = true;
                        Win32Api.CloseHandle(procHandle);
                        procHandle = innerProcHandle;
                        procId = innerProcId;
                        appName = GetProcessNameFromHandle(procHandle);
                    }

                    return true;
                }, IntPtr.Zero);
            }

            // Boost the current foreground app, and then impose EcoQoS for previous foreground app
            bool bypass = ShouldBypassProcess(appName, ThrottleStatus);
            if (!bypass)
            {
                logger.Info("Boosting {0}", appName);
                UnthrottleProcess(procHandle, procId);
            }

            if (pendingProcPid != 0)
            {
                logger.Info("Throttle {0}", pendingProcName);

                IntPtr prevProcHandle = Win32Api.OpenProcess((uint)(Win32Api.ProcessAccessFlags.QueryLimitedInformation |
                    Win32Api.ProcessAccessFlags.SetInformation), false, pendingProcPid);
                if (prevProcHandle != IntPtr.Zero)
                {
                    ThrottleProcess(prevProcHandle, pendingProcPid);
                    Win32Api.CloseHandle(prevProcHandle);
                }
                else
                {
                    logger.Debug("Failed to open previously foreground process {0} (pid {1}) to throttle it. Win32 error: {2}",
                        pendingProcName, pendingProcPid, Marshal.GetLastWin32Error());
                }

                // Always clear the pending process, even if it could not be opened (for example because it already
                // exited). Otherwise it would stay exempt from throttling forever, and we would retry opening it on
                // every foreground window change. If it is still running, house keeping will throttle it later.
                pendingProcPid = 0;
                pendingProcName = "";
            }

            if (!bypass)
            {
                pendingProcPid = procId;
                pendingProcName = appName;
            }

            Win32Api.CloseHandle(procHandle);
        }
    }

    private bool ShouldBypassProcess(string processName, ThrottleStatus throttleStatus) => !ShouldThrottleProcess(processName, throttleStatus);

    private bool ShouldThrottleProcess(string processName, ThrottleStatus throttleStatus)
    {
        return throttleStatus switch
        {
            ThrottleStatus.Stopped => false,
            ThrottleStatus.OnlyBlacklist => IsProcessInBlacklist(processName),
            ThrottleStatus.BlacklistAndAllButWhitelist => IsProcessInBlacklist(processName) || !IsProcessInWhitelist(processName),
            _ => throw new ArgumentException("Unknown ThrottleStatus")
        };
    }

    private bool IsProcessInWhitelist(string processName)
    {
        return IsProcessInList(processName, ProcessWhitelist, WildcardProcessWhitelist);
    }

    private bool IsProcessInBlacklist(string processName)
    {
        return IsProcessInList(processName, ProcessBlacklist, WildcardProcessBlacklist);
    }

    private bool IsProcessInList(string processName, IReadOnlySet<string> fullProcessList, IReadOnlySet<string> wildcardProcessList)
    {
        if (fullProcessList.Contains(processName.ToLowerInvariant()))
        {
            return true;
        }

        if (wildcardProcessList.Any(wildcardExpression => FileSystemName.MatchesSimpleExpression(wildcardExpression, processName, true)))
        {
            return true;
        }

        return false;
    }

    private void ThrottleProcess(IntPtr hProcess, uint processId)
    {
        // Remember the priority class this process has before it gets throttled, so UnthrottleProcess can restore it.
        // Skip recording when the process is already running at IDLE_PRIORITY_CLASS (it either got throttled before,
        // or it is idle by design) or when GetPriorityClass failed, otherwise we would remember a bogus "original" value.
        Win32Api.PriorityClass currentPriorityClass = Win32Api.GetPriorityClass(hProcess);
        if ((uint)currentPriorityClass != 0 && currentPriorityClass != Win32Api.PriorityClass.IDLE_PRIORITY_CLASS)
        {
            originalPriorityClasses[processId] = currentPriorityClass;
        }

        if (!Win32Api.SetProcessInformation(hProcess, Win32Api.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                pThrottleOn, (uint)szControlBlock))
        {
            logger.Warn("Failed to throttle process {0}. Win32 error: {1}", processId, Marshal.GetLastWin32Error());
        }

        if (!Win32Api.SetPriorityClass(hProcess, Win32Api.PriorityClass.IDLE_PRIORITY_CLASS))
        {
            logger.Warn("Failed to set idle priority for process {0}. Win32 error: {1}", processId, Marshal.GetLastWin32Error());
        }
    }

    private void UnthrottleProcess(IntPtr hProcess, uint processId)
    {
        if (!Win32Api.SetProcessInformation(hProcess, Win32Api.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                pThrottleOff, (uint)szControlBlock))
        {
            logger.Warn("Failed to unthrottle process {0}. Win32 error: {1}", processId, Marshal.GetLastWin32Error());
        }

        // Restore the priority class the process had before it got throttled.
        if (!originalPriorityClasses.Remove(processId, out Win32Api.PriorityClass originalPriorityClass))
        {
            // This process was not throttled by the current run of the app, so its priority class must be left alone.
            // The only exception is a process that is still stuck at IDLE_PRIORITY_CLASS: that means it was throttled
            // by a previous run of this app which did not get to restore it.
            if (Win32Api.GetPriorityClass(hProcess) != Win32Api.PriorityClass.IDLE_PRIORITY_CLASS)
            {
                return;
            }

            originalPriorityClass = Win32Api.PriorityClass.NORMAL_PRIORITY_CLASS;
        }

        if (!Win32Api.SetPriorityClass(hProcess, originalPriorityClass))
        {
            logger.Warn("Failed to restore priority class {0} for process {1}. Win32 error: {2}",
                originalPriorityClass, processId, Marshal.GetLastWin32Error());
        }
    }

    private void PowerManager_PowerSourceKindChanged(object? sender, object e)
    {
        lock (lockObject)
        {
            if (IsOnBattery)
            {
                logger.Info("Power source changed to battery");
            }
            else
            {
                logger.Info("Power source changed to AC");
            }

            UpdateThrottleStatusAndNotify();
        }
    }

    private string GetProcessNameFromHandle(IntPtr hProcess)
    {
        int capacity = 1024;
        StringBuilder sb = new(capacity);

        if (Win32Api.QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
        {
            return Path.GetFileName(sb.ToString());
        }

        return "";
    }
}
