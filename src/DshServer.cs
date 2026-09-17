using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DshDesktop
{
    /// <summary>One line of process output, tagged with its stream.</summary>
    internal sealed class ServerLine
    {
        public ServerLine(bool isError, string text)
        {
            IsError = isError;
            Text = text;
        }

        public bool IsError { get; }
        public string Text { get; }
    }

    /// <summary>How an individual start attempt ended.</summary>
    internal enum ServerStartOutcome
    {
        Ready,

        /// <summary>The web port this launcher chose is taken; trying another one helps.</summary>
        PortBusy,

        /// <summary>A plugin's own fixed port is taken (e.g. dsh-mobile on 3443); another web port will not help.</summary>
        PluginPortBusy,

        PortTimedOut,
        Exited,
        TimedOut,
        Failed,
    }

    internal sealed class ServerStartResult
    {
        public ServerStartOutcome Outcome;
        public string Url;
        public string Detail;
    }

    /// <summary>
    /// Owns the bundled <c>dsh web</c> child process: it spawns it, watches stdout for the
    /// authenticated URL dsh prints at startup, and keeps the whole process tree inside a
    /// kill-on-close job object so closing this app always takes its server down with it.
    /// </summary>
    internal sealed class DshServer : IDisposable
    {
        /// <summary>dsh prints exactly one of these, with the process token appended, once it is serving.</summary>
        private static readonly Regex UrlLine = new Regex(@"dsh web:\s*(http://\S+)", RegexOptions.Compiled);

        private static readonly Regex PortBusyLine = new Regex(
            @"EADDRINUSE|address already in use|only one usage of each socket address",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly object _gate = new object();
        private readonly StringBuilder _tail = new StringBuilder();
        private JobObject _job;
        private Process _process;

        /// <summary>Raised for every stdout/stderr line of the child process (thread-pool thread).</summary>
        public event Action<ServerLine> Output;

        public string CommandLine { get; private set; }
        public string WorkingDirectory { get; private set; }
        public string Url { get; private set; }

        public bool IsRunning
        {
            get
            {
                var process = _process;
                try { return process != null && !process.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>Everything the child printed, capped to the last few thousand lines.</summary>
        public string Tail
        {
            get { lock (_gate) return _tail.ToString(); }
        }

        public ServerStartResult Start(int port, string workspace, TimeSpan readyTimeout)
        {
            CommandLine = DshLocator.BuildCommandLine(port);
            WorkingDirectory = workspace;

            if (CommandLine == null)
            {
                return new ServerStartResult
                {
                    Outcome = ServerStartOutcome.Failed,
                    Detail = "找不到 dsh 可执行文件或 node，请确认已安装 DeepSeek Harness。",
                };
            }

            var startInfo = DshLocator.BuildStartInfo(CommandLine, workspace);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                process.OutputDataReceived += (s, e) => OnLine(false, e.Data);
                process.ErrorDataReceived += (s, e) => OnLine(true, e.Data);
                process.Start();
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Park the child in a kill-on-close job object immediately, before it can fork
                // helpers of its own, so nothing outlives this app.
                try
                {
                    _job = new JobObject();
                    _job.AddProcess(process);
                }
                catch (Exception ex)
                {
                    Emit(false, "[launcher] 进程看守创建失败（不影响使用）：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                return new ServerStartResult
                {
                    Outcome = ServerStartOutcome.Failed,
                    Detail = "启动 dsh 失败：" + ex.Message,
                };
            }

            Emit(false, "[launcher] " + CommandLine);
            Emit(false, "[launcher] 工作目录 " + workspace);

            var deadline = DateTime.UtcNow + readyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Url != null)
                {
                    return new ServerStartResult { Outcome = ServerStartOutcome.Ready, Url = Url };
                }
                try
                {
                    if (process.HasExited)
                    {
                        var outcome = ClassifyExit(port);
                        return new ServerStartResult
                        {
                            Outcome = outcome,
                            Detail = outcome == ServerStartOutcome.PortBusy
                                ? "端口 " + port + " 被占用。"
                                : outcome == ServerStartOutcome.PluginPortBusy
                                    ? "另一个 dsh 实例正在运行，占用了插件自己的固定端口（如 dsh-mobile 的 3443）。"
                                    : "dsh 进程提前退出（代码 " + process.ExitCode + "）。",
                        };
                    }
                }
                catch
                {
                    // HasExited can throw while the process is tearing down; just look again.
                }
                Thread.Sleep(120);
            }

            var timedOut = ClassifyExit(port);
            return new ServerStartResult
            {
                Outcome = timedOut == ServerStartOutcome.PortBusy ? ServerStartOutcome.PortBusy : ServerStartOutcome.TimedOut,
                Detail = "等待 dsh 启动超时（" + (int)readyTimeout.TotalSeconds + " 秒）。",
            };
        }

        /// <summary>
        /// Tells the two kinds of "address already in use" apart.
        ///
        /// Only one of them is worth retrying on another port: the web port this launcher chose.
        /// A plugin can bind a fixed port of its own (dsh-mobile listens on 3443), and when that
        /// one collides, no other web port will help — the usual cause is that another dsh
        /// instance is already running on this machine.
        /// </summary>
        private ServerStartOutcome ClassifyExit(int port)
        {
            var tail = Tail;

            if (tail != null && tail.Contains(":" + port + " ") || (tail != null && tail.Contains(":" + port + "\r")))
            {
                return ServerStartOutcome.PortBusy;
            }

            if (PortBusyLine.IsMatch(tail)) return ServerStartOutcome.PluginPortBusy;
            return ServerStartOutcome.Exited;
        }

        /// <summary>Kills the child process and everything it spawned.</summary>
        public void Stop()
        {
            var process = _process;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch { }

                try { process.WaitForExit(6000); }
                catch { }

                try { process.Dispose(); }
                catch { }
            }

            var job = _job;
            _job = null;
            try { job?.Dispose(); }
            catch { }

            _process = null;
        }

        public void Dispose() => Stop();

        private void OnLine(bool isError, string text)
        {
            if (text == null) return;

            if (!isError)
            {
                var match = UrlLine.Match(text);
                if (match.Success && Url == null) Url = match.Groups[1].Value.Trim();
            }

            Emit(isError, text);
        }

        private void Emit(bool isError, string text)
        {
            lock (_gate)
            {
                _tail.AppendLine((isError ? "! " : "  ") + text);
                if (_tail.Length > 256 * 1024)
                {
                    _tail.Remove(0, _tail.Length - 192 * 1024);
                }
            }

            try { Output?.Invoke(new ServerLine(isError, text)); }
            catch { }
        }
    }

    /// <summary>
    /// Resolves how to run dsh: the node entry point of the installed package when it can be
    /// found, otherwise whatever <c>dsh</c> resolves to on PATH.
    /// </summary>
    internal static class DshLocator
    {
        private static string _cachedKind;
        private static string _cachedNode;
        private static string _cachedEntry;
        private static string _cachedExe;

        public static string BuildCommandLine(int port)
        {
            Resolve();
            if (_cachedKind == "node") return _cachedNode + " " + Quote(_cachedEntry) + " web --no-open --port " + port;
            if (_cachedKind == "exe") return Quote(_cachedExe) + " web --no-open --port " + port;
            return null;
        }

        public static ProcessStartInfo BuildStartInfo(string commandLine, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"" + commandLine + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            var workspace = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : workingDirectory;

            startInfo.WorkingDirectory = Directory.Exists(workspace)
                ? workspace
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["FORCE_COLOR"] = "0";
            startInfo.Environment["DSH_WEB_LAUNCHER"] = "1";

            // A packaged install keeps its profile (and therefore its plugin list and DSH_HOME)
            // under the data folder the user chose, so nothing lands on the system drive by default.
            if (!string.IsNullOrEmpty(PackagedDshHome))
            {
                startInfo.Environment["DSH_HOME"] = PackagedDshHome;
            }

            return startInfo;
        }

        /// <summary>
        /// Set by the launcher for a packaged install: the DSH_HOME the bundled dsh should use.
        /// Null for a plain build, where dsh keeps its own default (<c>~/.dsh</c>).
        /// </summary>
        public static string PackagedDshHome { get; set; }

        /// <summary>
        /// Node and the dsh entry point of a packaged install, injected by the launcher when
        /// <c>runtime\</c> sits next to the executable.
        /// </summary>
        public static void UseBundledRuntime(string nodeExe, string vendorDirectory)
        {
            _cachedKind = "node";
            _cachedNode = nodeExe;
            _cachedEntry = Path.Combine(vendorDirectory, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        }

        private static void Resolve()
        {
            if (_cachedKind != null) return;

            var node = FindNode();
            var entry = node == null ? null : FindPackageEntry();

            if (node != null && entry != null)
            {
                _cachedKind = "node";
                _cachedNode = node;
                _cachedEntry = entry;
                return;
            }

            var exe = FindOnPath("dsh.cmd") ?? FindOnPath("dsh.exe") ?? FindOnPath("dsh.bat");
            if (exe != null)
            {
                _cachedKind = "exe";
                _cachedExe = exe;
                return;
            }

            _cachedKind = "none";
        }

        public static string FindNode()
        {
            // Packaged install: its own Node, so a machine without Node installed still works.
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "node", "node.exe");
            try { if (File.Exists(bundled)) return bundled; } catch { }

            var onPath = FindOnPath("node.exe");
            if (onPath != null) return onPath;

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "nodejs", "node.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>Locates <c>@deepseek-ai/dsh/lib/bin.js</c> in the usual global npm roots.</summary>
        public static string FindPackageEntry()
        {
            var roots = new List<string>();

            // A packaged install wins: its runtime ships with the app and needs no system Node.
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "vendor", "node_modules");
            try { if (Directory.Exists(bundled)) roots.Add(bundled); } catch { }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                roots.Add(Path.Combine(appData, "npm", "node_modules"));
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                roots.Add(Path.Combine(programFiles, "nodejs", "node_modules"));
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NODE_PATH")))
            {
                foreach (var part in Environment.GetEnvironmentVariable("NODE_PATH").Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(part)) roots.Add(part.Trim());
                }
            }

            // Walk up from this executable: a portable build dropped inside a project checkout
            // still finds a locally installed dsh.
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int depth = 0; depth < 4 && dir != null; depth++, dir = dir.Parent)
                {
                    roots.Add(Path.Combine(dir.FullName, "node_modules"));
                }
            }
            catch { }

            foreach (var root in roots)
            {
                try
                {
                    var entry = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(entry)) return entry;
                }
                catch { }
            }
            return null;
        }

        private static string FindOnPath(string fileName)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var raw in path.Split(Path.PathSeparator))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static string Quote(string value) => "\"" + value + "\"";
    }

    /// <summary>
    /// A Windows job object configured with KILL_ON_JOB_CLOSE. Disposing it (or dying with it)
    /// terminates every process still assigned to it, which is what keeps orphaned dsh servers
    /// from surviving a crash of this app.
    /// </summary>
    internal sealed class JobObject : IDisposable
    {
        private IntPtr _handle;

        public JobObject()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(info);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    throw new InvalidOperationException("SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void AddProcess(Process process)
        {
            if (_handle == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(_handle, process.Handle))
                throw new InvalidOperationException("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
