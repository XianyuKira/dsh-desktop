using System;
using System.IO;
using System.Windows.Forms;

namespace DshDesktopUpdater
{
    /// <summary>
    /// Thin command-line front end for <see cref="Updater"/>. The app starts this process
    /// before exiting, which is the only way to replace the running executable on Windows.
    ///
    /// Usage: DshDesktopUpdater.exe &lt;zip&gt; &lt;installDir&gt; &lt;parentPid&gt; &lt;exeToRestart&gt;
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 4)
            {
                MessageBox.Show(
                    "更新助手由 DeepSeek Harness 桌面版自动调用，不需要手动运行。\n\n" +
                    "参数：<更新包> <安装目录> <父进程号> <要重启的程序>",
                    "DeepSeek Harness 更新助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 2;
            }

            var plan = new UpdatePlan
            {
                ZipPath = args[0],
                InstallDirectory = args[1],
                ParentProcessId = int.TryParse(args[2], out var pid) ? pid : -1,
                RestartExecutable = args[3],
            };

            var outcome = Updater.Apply(plan);

            if (!outcome.Succeeded)
            {
                try
                {
                    MessageBox.Show(
                        "自动更新失败：\n\n" + outcome.Error +
                        "\n\n程序文件可能处于半更新状态，建议重新下载最新包手动解压覆盖。\n" +
                        "详细日志：" + Updater.DefaultLogPath,
                        "DeepSeek Harness 更新助手",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }

            return 0;
        }
    }
}
