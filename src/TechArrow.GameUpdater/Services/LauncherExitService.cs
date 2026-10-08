using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TechArrow.GameUpdater.Infrastructure.Services;

namespace TechArrow.GameUpdater.Services;

public sealed record LauncherExitResult(bool Exited, string Detail, bool Retry = false, bool Disabled = false);

public static class LauncherExitService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<LauncherExitResult> RequestAsync(ILauncherProcessReader reader, LauncherIdlePolicy policy,
        Func<bool> enabled, int timeoutSeconds, CancellationToken token,
        Func<IReadOnlyList<LauncherProcessIdentity>, CancellationToken, bool>? exitRequest = null)
    {
        await Gate.WaitAsync(token);
        try
        {
            var final = await Task.Run(reader.Read, token);
            if (!enabled()) return new(false, "Автозакрытие выключено. Клиент оставлен открытым.", Disabled: true);
            if (!policy.Evaluate(final.Sample).ReadyToClose) return new(false, "Активность изменилась. Запрос выхода отменён; мониторинг продолжается.", Retry: true);
            var frontends = final.Processes.Where(p => p.Frontend).ToArray();
            if (frontends.Length == 0) return new(true, "Процессы клиента уже завершились.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 15)));
            bool requested;
            try { requested = await Task.Run(() => exitRequest is null ? RequestExit(reader, policy, frontends, enabled, timeout.Token) : exitRequest(frontends, timeout.Token), timeout.Token).WaitAsync(timeout.Token); }
            catch (LauncherExitDeferredException) { return new(false, "Активность изменилась. Запрос выхода отменён; мониторинг продолжается.", Retry: true); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return !enabled() ? new(false, "Автозакрытие выключено. Клиент оставлен открытым.", Disabled: true) : new(false, "Меню выхода не ответило вовремя. Клиент оставлен открытым."); }
            if (!requested) return new(false, "Штатный запрос выхода недоступен. Проверьте настройки выхода и окно клиента.");
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                token.ThrowIfCancellationRequested();
                // Only frontend processes must terminate; updater services may legitimately remain running.
                if (!frontends.Any(LauncherProcessReader.Matches))
                {
                    var check = await Task.Run(reader.Read, token);
                    if (check.Sample.Reliable && !check.Sample.IsRunning) return new(true, "Клиент корректно завершён после устойчивого простоя. Фоновые службы не завершались принудительно.");
                    return new(false, "Клиент сменил процесс или перезапустился. Проверьте его окно.");
                }
                await Task.Delay(1000, token);
            }
            return new(false, "Клиент остался в фоне или трее. Включите в нём выход при закрытии окна либо используйте его меню «Выход».");
        }
        finally { Gate.Release(); }
    }
    private sealed class LauncherExitDeferredException : Exception { }
    private static void EnsureUserIdle()
    {
        if (!UserActivityClock.TryLastRealInput(out var input) || unchecked((uint)Environment.TickCount64 - input) < 60000) throw new LauncherExitDeferredException();
    }
    private static bool RequestExit(ILauncherProcessReader reader, LauncherIdlePolicy policy, IReadOnlyList<LauncherProcessIdentity> frontends, Func<bool> enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!frontends.All(LauncherProcessReader.Matches)) return false;
        try { if (ExitThroughTray(reader.Profile, frontends, enabled, token)) return true; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        token.ThrowIfCancellationRequested();
        if (!enabled()) throw new OperationCanceledException();
        if (!policy.Evaluate(reader.Read().Sample).ReadyToClose) throw new LauncherExitDeferredException();
        EnsureUserIdle();
        return RequestWindowExit(frontends, token, enabled);
    }
    public static bool RequestWindowExit(IReadOnlyList<LauncherProcessIdentity> frontends, CancellationToken token, Func<bool>? enabled = null)
    {
        bool requested = false;
        foreach (var identity in frontends)
        {
            token.ThrowIfCancellationRequested();
            if (enabled is not null && !enabled()) throw new OperationCanceledException();
            if (!LauncherProcessReader.Matches(identity)) continue;
            try
            {
                using var process = Process.GetProcessById(identity.Id);
                if (process.MainWindowHandle != IntPtr.Zero) { if (LauncherProcessReader.Matches(identity)) requested |= process.CloseMainWindow(); continue; }
                // Some clients have an owned/hidden main window, which Process.MainWindowHandle omits.
                var windows = new List<IntPtr>();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var id);
                    if (id == identity.Id && GetWindowTextLengthW(window) > 0 && GetWindowRect(window, out var bounds) && bounds.Right - bounds.Left >= 100 && bounds.Bottom - bounds.Top >= 80) windows.Add(window);
                    return true;
                }, IntPtr.Zero);
                foreach (var window in windows)
                {
                    token.ThrowIfCancellationRequested();
                    if (enabled is not null && !enabled()) throw new OperationCanceledException();
                    GetWindowThreadProcessId(window, out var owner);
                    if (owner == identity.Id && LauncherProcessReader.Matches(identity)) requested |= PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return requested;
    }
    private static bool ExitThroughTray(LauncherProcessProfile profile, IReadOnlyList<LauncherProcessIdentity> identities, Func<bool> enabled, CancellationToken token)
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        var expanded = false;
        AutomationElement? icon = FindIcon(tray, profile);
        var overflow = FindWindowW("NotifyIconOverflowWindow", null);
        if (icon is null && overflow != IntPtr.Zero && IsWindowVisible(overflow)) icon = FindIcon(overflow, profile);
        if (icon is null)
        {
            var buttons = AutomationElement.FromHandle(tray).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var arrows = buttons.Cast<AutomationElement>().Where(element => new[] { "Show hidden icons", "Показать скрытые значки", "Скрытые значки", "System tray overflow menu" }.Contains(element.Current.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
            token.ThrowIfCancellationRequested();
            if (!enabled()) throw new OperationCanceledException();
            EnsureUserIdle();
            if (arrows.Length != 1 || !identities.All(LauncherProcessReader.Matches) || !arrows[0].TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return false;
            ((InvokePattern)invoke).Invoke(); expanded = true;
            overflow = FindWindowW("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero && IsWindowVisible(overflow)) icon = FindIcon(overflow, profile);
        }
        if (icon is null) { if (expanded) Escape(token); return false; }
        var rectangle = icon.Current.BoundingRectangle;
        if (rectangle.IsEmpty || icon.Current.IsOffscreen || !GetCursorPos(out var previous)) return false;
        var target = new Point { X = (int)(rectangle.Left + rectangle.Width / 2), Y = (int)(rectangle.Top + rectangle.Height / 2) };
        token.ThrowIfCancellationRequested();
        if (!enabled()) throw new OperationCanceledException();
        if (!identities.All(LauncherProcessReader.Matches)) return false;
        EnsureUserIdle();
        UserActivityClock.BeforeInjection();
        if (!SetCursorPos(target.X, target.Y)) return false;
        var injectedTime = unchecked((uint)Environment.TickCount64);
        var input = new[] { MouseInput(0x0008, injectedTime), MouseInput(0x0010, injectedTime) };
        bool invoked = false;
        try
        {
            if (SendInput(2, input, Marshal.SizeOf<Input>()) != 2)
            { SendInput(1, [MouseInput(0x0010, injectedTime)], Marshal.SizeOf<Input>()); UserActivityClock.AfterInjection(injectedTime); return false; }
            UserActivityClock.AfterInjection(injectedTime);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                token.ThrowIfCancellationRequested();
                if (!enabled()) throw new OperationCanceledException();
                if (!GetCursorPos(out var current) || current.X != target.X || current.Y != target.Y || UserActivityClock.HasInputAfterInjection()) throw new LauncherExitDeferredException();
                if (!identities.All(LauncherProcessReader.Matches)) return false;
                var item = FindExitItem(identities.Select(p => p.Id).ToHashSet());
                if (item is not null && item.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    token.ThrowIfCancellationRequested();
                    if (!enabled()) throw new OperationCanceledException();
                    if (UserActivityClock.HasInputAfterInjection()) throw new LauncherExitDeferredException();
                    ((InvokePattern)invoke).Invoke(); invoked = true; return true;
                }
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
            return false;
        }
        finally
        {
            if (GetCursorPos(out var current) && current.X == target.X && current.Y == target.Y && !UserActivityClock.HasInputAfterInjection())
            {
                if (!invoked && !token.IsCancellationRequested) Escape(token);
                SetCursorPos(previous.X, previous.Y);
            }
        }
    }
    private static AutomationElement? FindIcon(IntPtr window, LauncherProcessProfile profile)
    {
        var root = AutomationElement.FromHandle(window);
        var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var icons = buttons.Cast<AutomationElement>().Where(element => !element.Current.IsOffscreen && profile.TrayNames.Any(name =>
            element.Current.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || element.Current.Name.StartsWith(name + ",", StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
        return icons.Length == 1 ? icons[0] : null;
    }
    private static AutomationElement? FindExitItem(HashSet<int> owners)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var id); if (owners.Contains((int)id) && IsWindowVisible(window)) windows.Add(window); return true; }, IntPtr.Zero);
        foreach (var window in windows)
        {
            var root = AutomationElement.FromHandle(window);
            var className = new System.Text.StringBuilder(256);
            GetClassNameW(window, className, className.Capacity);
            if (className.ToString() != "#32768" && root.Current.ControlType != ControlType.Menu) continue;
            var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            foreach (var item in items.Cast<AutomationElement>().Take(200))
            {
                var name = item.Current.Name.Replace("&", "").Trim();
                // Tray menu only; never invoke account/logout buttons in the main client.
                if (item.Current.IsEnabled && !item.Current.IsOffscreen && new[] { "Exit", "Quit", "Exit application", "Выход", "Выйти", "Закрыть клиент", "Выйти из Riot Client", "Exit Riot Client", "Exit EA app" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return item;
            }
        }
        return null;
    }
    private static void Escape(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UserActivityClock.BeforeInjection();
        var time = unchecked((uint)Environment.TickCount64);
        var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { VirtualKey = 0x1B, Time = time } } }, new Input { Type = 1, Data = new InputUnion { Keyboard = new Keyboard { VirtualKey = 0x1B, Flags = 2, Time = time } } } };
        SendInput(2, inputs, Marshal.SizeOf<Input>()); UserActivityClock.AfterInjection(time);
    }
    private static Input MouseInput(uint flags, uint time) => new() { Type = 0, Data = new InputUnion { Mouse = new Mouse { Flags = flags, Time = time } } };
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    private delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string className, string? name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}

public static class UserActivityClock
{
    private static readonly object Gate = new();
    private static uint? _injected;
    private static uint _real;
    public static bool TryLastRealInput(out uint tick)
    {
        lock (Gate)
        {
            var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            if (!GetLastInputInfo(ref input)) { tick = 0; return false; }
            tick = _injected == input.Tick ? _real : input.Tick; return true;
        }
    }
    public static void BeforeInjection() { if (TryLastRealInput(out var tick)) lock (Gate) _real = tick; }
    public static void AfterInjection(uint expectedTime)
    {
        lock (Gate) { var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() }; _injected = GetLastInputInfo(ref input) && input.Tick == expectedTime ? input.Tick : null; }
    }
    public static bool HasInputAfterInjection()
    {
        lock (Gate) { var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() }; return !GetLastInputInfo(ref input) || _injected != input.Tick; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInput input);
}
