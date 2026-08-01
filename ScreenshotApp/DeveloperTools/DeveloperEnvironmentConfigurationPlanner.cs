using System.IO;

namespace ScreenshotApp.DeveloperTools;

/// <summary>根据已验证安装生成最小化的 PATH 与配套变量配置，不负责执行系统写入。</summary>
public static class DeveloperEnvironmentConfigurationPlanner
{
    public static ToolchainEnvironmentPlan Build(ToolchainSummary toolchain, ToolchainInstallation installation)
    {
        if (!installation.IsVerified || !File.Exists(installation.ExecutablePath))
        {
            return Blocked(toolchain, installation, "该安装尚未通过可执行文件验证，不能自动修改环境变量。");
        }

        return toolchain.Id switch
        {
            "java" => BuildJava(toolchain, installation),
            "python" => BuildPython(toolchain, installation),
            "node" => BuildNode(toolchain, installation),
            "maven" => BuildBinTool(toolchain, installation, "mvn.cmd", "MAVEN_HOME"),
            "gradle" => BuildBinTool(toolchain, installation, "gradle.bat", "GRADLE_HOME"),
            "git" => BuildExecutableDirectory(toolchain, installation),
            "dotnet" => BuildExecutableDirectory(toolchain, installation),
            _ => Blocked(toolchain, installation, "当前工具链尚未提供安全的一键配置规则。")
        };
    }

    private static ToolchainEnvironmentPlan BuildJava(ToolchainSummary toolchain, ToolchainInstallation installation)
    {
        var root = installation.InstallationPath;
        var bin = Path.Combine(root, "bin");
        if (!File.Exists(Path.Combine(bin, "java.exe")))
        {
            return Blocked(toolchain, installation, "未能在安装根目录下找到 bin\\java.exe。");
        }
        if (!File.Exists(Path.Combine(bin, "javac.exe")))
        {
            return Blocked(toolchain, installation, "该目录只有 Java 运行时，没有 javac.exe；不能作为 JDK 自动配置。");
        }

        return Ready(toolchain, installation, new[] { bin }, new Dictionary<string, string>
        {
            ["JAVA_HOME"] = root
        }, new[] { "如果已存在 JAVA_HOME，将在确认后切换为所选 JDK；已经打开的终端不会自动切换。" });
    }

    private static ToolchainEnvironmentPlan BuildPython(ToolchainSummary toolchain, ToolchainInstallation installation)
    {
        var executable = installation.ExecutablePath;
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        if (executable.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(toolchain, installation, "这是 Windows 应用执行别名，不是可直接配置的 Python 安装。");
        }
        if (File.Exists(Path.Combine(Directory.GetParent(directory)?.FullName ?? string.Empty, "pyvenv.cfg")))
        {
            return Blocked(toolchain, installation, "项目虚拟环境不能加入全局 PATH；请在对应项目中激活它。");
        }

        var entries = new List<string> { directory };
        var scripts = Path.Combine(directory, "Scripts");
        if (Directory.Exists(scripts)) entries.Add(scripts);
        return Ready(toolchain, installation, entries, new Dictionary<string, string>(), new[]
        {
            "不会设置 PYTHONHOME；推荐使用 python -m pip，避免 pip 指向其他解释器。"
        });
    }

    private static ToolchainEnvironmentPlan BuildNode(ToolchainSummary toolchain, ToolchainInstallation installation)
    {
        var path = installation.ExecutablePath;
        if (path.Contains(@"\Volta\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\nvm\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\fnm", StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(toolchain, installation, "检测到 Node 版本管理器入口，应由 Volta、nvm 或 fnm 管理 PATH，X-Tool 不会加入具体版本目录。");
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var warnings = File.Exists(Path.Combine(directory, "npm.cmd"))
            ? Array.Empty<string>()
            : new[] { "该目录未发现 npm.cmd；加入 PATH 后可能只能使用 node。" };
        return Ready(toolchain, installation, new[] { directory }, new Dictionary<string, string>(), warnings);
    }

    private static ToolchainEnvironmentPlan BuildBinTool(
        ToolchainSummary toolchain,
        ToolchainInstallation installation,
        string commandName,
        string homeVariable)
    {
        var root = installation.InstallationPath;
        var bin = Path.Combine(root, "bin");
        if (!File.Exists(Path.Combine(bin, commandName)))
        {
            var executableDirectory = Path.GetDirectoryName(installation.ExecutablePath) ?? string.Empty;
            if (!File.Exists(Path.Combine(executableDirectory, commandName)))
            {
                return Blocked(toolchain, installation, $"未能定位 {commandName} 所在的命令目录。");
            }
            bin = executableDirectory;
            root = Directory.GetParent(bin)?.FullName ?? installation.InstallationPath;
        }

        return Ready(toolchain, installation, new[] { bin }, new Dictionary<string, string>
        {
            [homeVariable] = root
        }, Array.Empty<string>());
    }

    private static ToolchainEnvironmentPlan BuildExecutableDirectory(ToolchainSummary toolchain, ToolchainInstallation installation)
    {
        var directory = Path.GetDirectoryName(installation.ExecutablePath) ?? string.Empty;
        return Directory.Exists(directory)
            ? Ready(toolchain, installation, new[] { directory }, new Dictionary<string, string>(), Array.Empty<string>())
            : Blocked(toolchain, installation, "未能定位可执行文件所在目录。");
    }

    private static ToolchainEnvironmentPlan Ready(
        ToolchainSummary toolchain,
        ToolchainInstallation installation,
        IReadOnlyList<string> pathEntries,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<string> warnings)
        => new()
        {
            Toolchain = toolchain,
            Installation = installation,
            PathEntries = pathEntries,
            Variables = variables,
            Warnings = warnings
        };

    private static ToolchainEnvironmentPlan Blocked(
        ToolchainSummary toolchain,
        ToolchainInstallation installation,
        string reason)
        => new()
        {
            Toolchain = toolchain,
            Installation = installation,
            BlockingReason = reason
        };
}
