using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SerialLog.Update;

namespace SerialLog.App.Updates;

public static class PortableUpdateCoordinator
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool CanInstallInPlace(
        IUpdateService updateService,
        string installDirectory,
        out string reason)
    {
        if (IsPackaged())
        {
            reason = "当前运行的是 MSIX 安装版，请打开 GitHub Release 下载并安装新版本。";
            return false;
        }

        return updateService.CanInstallInPlace(installDirectory, out reason);
    }

    public static void StartUpdater(PreparedUpdate preparedUpdate)
    {
        var startInfo = new ProcessStartInfo(preparedUpdate.UpdaterExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(preparedUpdate.UpdaterExecutablePath)!
        };
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(preparedUpdate.JobFilePath);
        Process.Start(startInfo);
    }

    private static bool IsPackaged()
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
