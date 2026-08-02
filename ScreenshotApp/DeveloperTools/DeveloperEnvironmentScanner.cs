using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotApp.DeveloperTools;

public sealed class DeveloperEnvironmentScanner
{
    private static readonly ToolDefinition[] Definitions =
    {
        new("java", "Java", "JDK、JAVA_HOME 与命令解析", "\uE943", "#FFF0D9", "#D88B24", new[] { "java.exe" }, new[] { "-XshowSettings:properties", "-version" }),
        new("python", "Python", "解释器、启动别名与 pip 一致性", "\uE73C", "#E6F0FF", "#347ED8", new[] { "python.exe", "python3.exe" }, new[] { "--version" }),
        new("node", "Node.js", "Node、npm 与版本管理器入口", "\uE74C", "#E5F6E9", "#3C9760", new[] { "node.exe" }, new[] { "--version" }),
        new("dotnet", ".NET SDK", "并行安装的 SDK 与默认 dotnet", "\uE756", "#F0EAFF", "#7759B5", new[] { "dotnet.exe" }, new[] { "--list-sdks" }),
        new("git", "Git", "版本、命令路径与基础配置入口", "\uE8F1", "#FFEAE5", "#C45D42", new[] { "git.exe" }, new[] { "--version" }),
        new("maven", "Maven", "Maven 主目录与命令入口", "\uE8F7", "#E6F4FF", "#3B7DAA", new[] { "mvn.cmd", "mvn.bat" }, Array.Empty<string>()),
        new("gradle", "Gradle", "Gradle 主目录与命令入口", "\uE9D9", "#E2F5F3", "#318C83", new[] { "gradle.bat", "gradle.cmd" }, Array.Empty<string>())
    };

    private readonly SafeDeveloperCommandRunner _commandRunner = new();

    public async Task<DeveloperEnvironmentSnapshot> ScanAsync(
        IProgress<DeveloperScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var effectivePathEntries = ReadEffectivePersistentPathEntries();
        var results = new ConcurrentDictionary<string, IReadOnlyList<ToolchainInstallation>>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        using var gate = new SemaphoreSlim(3);

        var tasks = Definitions.Select(async definition =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new DeveloperScanProgress($"正在扫描 {definition.DisplayName}", Volatile.Read(ref completed), Definitions.Length));
                results[definition.Id] = await DiscoverToolAsync(definition, effectivePathEntries, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                results[definition.Id] = Array.Empty<ToolchainInstallation>();
            }
            finally
            {
                gate.Release();
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new DeveloperScanProgress($"已完成 {definition.DisplayName}", current, Definitions.Length));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new DeveloperEnvironmentSnapshot { ScannedAt = DateTime.Now };
        foreach (var definition in Definitions)
        {
            var summary = new ToolchainSummary
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                IconGlyph = definition.IconGlyph,
                IconBackground = definition.IconBackground,
                IconForeground = definition.IconForeground
            };

            if (results.TryGetValue(definition.Id, out var installations))
            {
                foreach (var installation in installations)
                {
                    summary.Installations.Add(installation);
                }
            }

            snapshot.Toolchains.Add(summary);
        }

        foreach (var issue in BuildDiagnostics(snapshot, effectivePathEntries))
        {
            snapshot.Issues.Add(issue);
        }

        return snapshot;
    }

    private async Task<IReadOnlyList<ToolchainInstallation>> DiscoverToolAsync(
        ToolDefinition definition,
        IReadOnlyList<string> processPathEntries,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, ToolCandidate>(StringComparer.OrdinalIgnoreCase);
        AddPathCandidates(definition, processPathEntries, candidates);
        AddEnvironmentCandidates(definition.Id, candidates);
        AddRegistryCandidates(definition.Id, candidates);
        AddKnownDirectoryCandidates(definition.Id, candidates);
        AddFixedDriveTopLevelCandidates(definition.Id, candidates);

        if (definition.Id == "dotnet")
        {
            var dotnetCandidate = candidates.Values.FirstOrDefault(item => item.IsActive && item.CanExecute)
                ?? candidates.Values.FirstOrDefault(item => item.CanExecute);
            if (dotnetCandidate is not null)
            {
                var sdkInstallations = await DiscoverDotNetSdksAsync(dotnetCandidate, cancellationToken).ConfigureAwait(false);
                if (sdkInstallations.Count > 0)
                {
                    return sdkInstallations;
                }
            }
        }

        var canonicalCandidates = candidates.Values
            .Where(candidate => definition.Id != "python" || candidate.IsActive || !IsPythonVirtualEnvironment(candidate.CanonicalExecutablePath))
            .Where(candidate => !IsPrivateHostRuntime(candidate.ExecutablePath))
            .GroupBy(candidate => candidate.CanonicalExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.IsActive).ThenBy(item => item.ExecutablePath.Length).First());

        var installations = new List<ToolchainInstallation>();
        foreach (var candidate in canonicalCandidates
                     .OrderByDescending(item => item.IsActive)
                     .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                     .Take(24))
        {
            cancellationToken.ThrowIfCancellationRequested();
            installations.Add(await ValidateCandidateAsync(definition, candidate, cancellationToken).ConfigureAwait(false));
        }

        return installations
            .GroupBy(item => $"{item.ToolchainId}|{NormalizePath(item.InstallationPath)}|{item.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.IsActive).ThenByDescending(item => item.IsVerified).First())
            .OrderByDescending(item => item.IsActive)
            .ThenByDescending(item => item.IsVerified)
            .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ToolchainInstallation> ValidateCandidateAsync(
        ToolDefinition definition,
        ToolCandidate candidate,
        CancellationToken cancellationToken)
    {
        var resolvedExecutablePath = definition.Id == "node"
            ? await TryResolveVoltaTargetAsync(candidate.ExecutablePath, cancellationToken).ConfigureAwait(false)
            : null;
        var validationExecutablePath = resolvedExecutablePath ?? candidate.ExecutablePath;
        var installationPath = GetInstallationRoot(definition.Id, validationExecutablePath);
        var architecture = GuessArchitecture(candidate.ExecutablePath);
        var version = TryReadVersionFromFiles(definition.Id, installationPath);
        var evidence = string.IsNullOrWhiteSpace(version) ? "已发现命令路径，尚未执行版本验证。" : "根据安装目录中的版本文件识别。";
        var verified = !string.IsNullOrWhiteSpace(version);

        if (candidate.CanExecute && definition.VersionArguments.Length > 0 && !IsWindowsAppAlias(candidate.ExecutablePath))
        {
            try
            {
                var result = await _commandRunner.RunAsync(validationExecutablePath, definition.VersionArguments, cancellationToken).ConfigureAwait(false);
                if (!result.TimedOut)
                {
                    var parsed = ParseVersion(definition.Id, result.CombinedOutput);
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        version = parsed;
                        verified = true;
                        evidence = FirstMeaningfulLine(result.CombinedOutput);
                        if (!string.IsNullOrWhiteSpace(resolvedExecutablePath))
                        {
                            evidence = $"Volta 实际执行目标：{resolvedExecutablePath}{Environment.NewLine}{evidence}";
                        }
                        if (definition.Id == "java")
                        {
                            var runtimeHome = ParseJavaHome(result.CombinedOutput);
                            if (!string.IsNullOrWhiteSpace(runtimeHome))
                            {
                                installationPath = NormalizeJavaInstallationRoot(runtimeHome);
                                evidence = $"java.home = {runtimeHome}";
                            }
                        }
                    }
                    else
                    {
                        evidence = result.ExitCode == 0 ? "命令执行成功，但未识别版本文本。" : $"版本命令退出码：{result.ExitCode}";
                    }
                }
                else
                {
                    evidence = "版本命令超过 8 秒，已终止验证。";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                evidence = $"版本验证失败：{ex.Message}";
            }
        }
        else if (IsWindowsAppAlias(candidate.ExecutablePath))
        {
            evidence = "检测到 Windows 应用执行别名；为避免触发安装或商店跳转，未自动运行。";
        }

        return new ToolchainInstallation
        {
            ToolchainId = definition.Id,
            DisplayName = definition.DisplayName,
            Version = string.IsNullOrWhiteSpace(version) ? "待验证" : version,
            ExecutablePath = candidate.ExecutablePath,
            ResolvedExecutablePath = resolvedExecutablePath ?? string.Empty,
            InstallationPath = installationPath,
            Source = string.IsNullOrWhiteSpace(resolvedExecutablePath) ? candidate.Source : $"{candidate.Source} · Volta 中转入口",
            Architecture = architecture,
            Evidence = evidence,
            IsActive = candidate.IsActive,
            IsVerified = verified
        };
    }

    private async Task<string?> TryResolveVoltaTargetAsync(string nodePath, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(nodePath);
            if (string.IsNullOrWhiteSpace(directory)) return null;
            var voltaPath = Path.Combine(directory, "volta.exe");
            if (!File.Exists(voltaPath)) return null;

            var result = await _commandRunner.RunAsync(voltaPath, new[] { "which", "node" }, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0) return null;
            var target = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Trim('"'))
                .FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path));
            return string.IsNullOrWhiteSpace(target) ? null : Path.GetFullPath(target);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ToolchainInstallation>> DiscoverDotNetSdksAsync(
        ToolCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var activeResult = await _commandRunner.RunAsync(candidate.ExecutablePath, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            var listResult = await _commandRunner.RunAsync(candidate.ExecutablePath, new[] { "--list-sdks" }, cancellationToken).ConfigureAwait(false);
            if (listResult.TimedOut || listResult.ExitCode != 0)
            {
                return Array.Empty<ToolchainInstallation>();
            }

            var activeVersion = activeResult.TimedOut || activeResult.ExitCode != 0
                ? string.Empty
                : FirstMeaningfulLine(activeResult.StandardOutput);

            var installations = new List<ToolchainInstallation>();
            foreach (var line in SplitLines(listResult.StandardOutput))
            {
                var match = Regex.Match(line, "^(?<version>\\S+)\\s+\\[(?<path>.+)\\]$");
                if (!match.Success)
                {
                    continue;
                }

                var sdkRoot = match.Groups["path"].Value.Trim();
                installations.Add(new ToolchainInstallation
                {
                    ToolchainId = "dotnet",
                    DisplayName = ".NET SDK",
                    Version = match.Groups["version"].Value.Trim(),
                    ExecutablePath = candidate.ExecutablePath,
                    InstallationPath = Path.Combine(sdkRoot, match.Groups["version"].Value.Trim()),
                    Source = candidate.Source,
                    Architecture = GuessArchitecture(candidate.ExecutablePath),
                    Evidence = line.Trim(),
                    IsActive = candidate.IsActive && string.Equals(match.Groups["version"].Value.Trim(), activeVersion, StringComparison.OrdinalIgnoreCase),
                    IsVerified = true
                });
            }

            return installations;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<ToolchainInstallation>();
        }
    }

    private static void AddPathCandidates(
        ToolDefinition definition,
        IReadOnlyList<string> pathEntries,
        IDictionary<string, ToolCandidate> candidates)
    {
        foreach (var commandName in definition.CommandNames)
        {
            for (var index = 0; index < pathEntries.Count; index++)
            {
                TryAddCandidate(Path.Combine(pathEntries[index], commandName), $"PATH 第 {index + 1} 项", false, candidates);
            }
        }

        // 同一命令只有 PATH 中最先命中的路径属于当前实际解析结果。
        var activeAssigned = false;
        foreach (var key in candidates.Keys.ToList())
        {
            var candidate = candidates[key];
            var shouldBeActive = !activeAssigned && candidate.Source.StartsWith("PATH ", StringComparison.Ordinal);
            candidates[key] = candidate with { IsActive = shouldBeActive };
            activeAssigned |= shouldBeActive;
        }
    }

    private static void AddEnvironmentCandidates(string toolchainId, IDictionary<string, ToolCandidate> candidates)
    {
        var variables = toolchainId switch
        {
            "java" => new[] { "JAVA_HOME", "JDK_HOME" },
            "python" => new[] { "PYTHONHOME" },
            "node" => new[] { "NODE_HOME", "VOLTA_HOME" },
            "dotnet" => new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" },
            "git" => new[] { "GIT_HOME" },
            "maven" => new[] { "MAVEN_HOME", "M2_HOME" },
            "gradle" => new[] { "GRADLE_HOME" },
            _ => Array.Empty<string>()
        };

        foreach (var variable in variables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var path in CandidateExecutablesForRoot(toolchainId, value))
            {
                TryAddCandidate(path, $"环境变量 {variable}", false, candidates);
            }
        }
    }

    private static void AddRegistryCandidates(string toolchainId, IDictionary<string, ToolCandidate> candidates)
    {
        if (toolchainId == "python")
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, registryView);
                        using var pythonCore = baseKey.OpenSubKey(@"SOFTWARE\Python\PythonCore");
                        foreach (var versionName in pythonCore?.GetSubKeyNames() ?? Array.Empty<string>())
                        {
                            using var installPathKey = pythonCore?.OpenSubKey($@"{versionName}\InstallPath");
                            var installPath = installPathKey?.GetValue(null)?.ToString();
                            if (!string.IsNullOrWhiteSpace(installPath))
                            {
                                TryAddCandidate(Path.Combine(installPath, "python.exe"), $"Python 注册表 {versionName}", false, candidates);
                            }
                        }
                    }
                    catch
                    {
                        // 单个注册表视图不可读时继续使用其他发现来源。
                    }
                }
            }
        }
        else if (toolchainId == "git")
        {
            foreach (var path in ReadRegistryInstallPaths(@"SOFTWARE\GitForWindows", "InstallPath"))
            {
                TryAddCandidate(Path.Combine(path, "cmd", "git.exe"), "Git for Windows 注册表", false, candidates);
            }
        }
    }

    private static IEnumerable<string> ReadRegistryInstallPaths(string subKey, string valueName)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? value = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(subKey);
                    value = key?.GetValue(valueName)?.ToString();
                }
                catch
                {
                    // 忽略单个不可读的注册表来源。
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static void AddKnownDirectoryCandidates(string toolchainId, IDictionary<string, ToolCandidate> candidates)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configuredVoltaHome = Environment.GetEnvironmentVariable("VOLTA_HOME", EnvironmentVariableTarget.User);
        var voltaHome = string.IsNullOrWhiteSpace(configuredVoltaHome)
            ? Path.Combine(localAppData, "Volta")
            : Environment.ExpandEnvironmentVariables(configuredVoltaHome.Trim().Trim('"'));

        var roots = toolchainId switch
        {
            "java" => new[]
            {
                Path.Combine(programFiles, "Java"),
                Path.Combine(programFiles, "Eclipse Adoptium"),
                Path.Combine(programFiles, "Microsoft"),
                Path.Combine(localAppData, "X-Tool", "Dev", "Java")
            },
            "python" => new[]
            {
                Path.Combine(localAppData, "Programs", "Python"),
                Path.Combine(roamingAppData, "uv", "python"),
                Path.Combine(localAppData, "X-Tool", "Dev", "Python", "uv")
            },
            "node" => new[]
            {
                Path.Combine(programFiles, "nodejs"),
                Path.Combine(voltaHome, "tools", "image", "node")
            },
            "dotnet" => new[] { Path.Combine(programFiles, "dotnet"), Path.Combine(programFilesX86, "dotnet") },
            "git" => new[] { Path.Combine(programFiles, "Git"), Path.Combine(programFilesX86, "Git"), Path.Combine(localAppData, "Programs", "Git") },
            _ => Array.Empty<string>()
        };

        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            foreach (var candidateRoot in EnumerateCandidateRoots(root, toolchainId is "java" or "python" or "node" ? 2 : 0))
            {
                foreach (var executable in CandidateExecutablesForRoot(toolchainId, candidateRoot))
                {
                    TryAddCandidate(executable, "常用安装目录", false, candidates);
                }
            }
        }
    }

    private static void AddFixedDriveTopLevelCandidates(string toolchainId, IDictionary<string, ToolCandidate> candidates)
    {
        var prefixes = toolchainId switch
        {
            "java" => new[] { "java", "jdk", "openjdk", "temurin" },
            "python" => new[] { "python", "cpython" },
            "node" => new[] { "node", "nodejs" },
            "dotnet" => new[] { "dotnet" },
            "git" => new[] { "git" },
            "maven" => new[] { "maven", "apache-maven" },
            "gradle" => new[] { "gradle" },
            _ => Array.Empty<string>()
        };

        if (prefixes.Length == 0)
        {
            return;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(item => item.DriveType == DriveType.Fixed && item.IsReady))
        {
            IEnumerable<string> topLevelDirectories;
            try
            {
                topLevelDirectories = Directory.EnumerateDirectories(drive.RootDirectory.FullName)
                    .Take(300)
                    .Where(path => prefixes.Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var topLevelDirectory in topLevelDirectories)
            {
                foreach (var candidateRoot in EnumerateCandidateRoots(topLevelDirectory, 2))
                {
                    foreach (var executable in CandidateExecutablesForRoot(toolchainId, candidateRoot))
                    {
                        TryAddCandidate(executable, $"{drive.Name} 顶层常用目录", false, candidates);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots(string root, int depth)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        yield return root;
        if (depth <= 0)
        {
            yield break;
        }

        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < 80)
        {
            var current = pending.Dequeue();
            if (current.Depth >= depth)
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current.Path).Take(40).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                visited++;
                yield return directory;
                pending.Enqueue((directory, current.Depth + 1));
            }
        }
    }

    private static IEnumerable<string> CandidateExecutablesForRoot(string toolchainId, string root)
    {
        root = Environment.ExpandEnvironmentVariables(root.Trim().Trim('"'));
        return toolchainId switch
        {
            "java" => new[] { Path.Combine(root, "bin", "java.exe"), Path.Combine(root, "java.exe") },
            "python" => new[] { Path.Combine(root, "python.exe"), Path.Combine(root, "bin", "python.exe") },
            "node" => new[] { Path.Combine(root, "node.exe"), Path.Combine(root, "bin", "node.exe") },
            "dotnet" => new[] { Path.Combine(root, "dotnet.exe") },
            "git" => new[] { Path.Combine(root, "cmd", "git.exe"), Path.Combine(root, "bin", "git.exe"), Path.Combine(root, "git.exe") },
            "maven" => new[] { Path.Combine(root, "bin", "mvn.cmd"), Path.Combine(root, "bin", "mvn.bat") },
            "gradle" => new[] { Path.Combine(root, "bin", "gradle.bat"), Path.Combine(root, "bin", "gradle.cmd") },
            _ => Array.Empty<string>()
        };
    }

    private static void TryAddCandidate(string path, string source, bool isActive, IDictionary<string, ToolCandidate> candidates)
    {
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!File.Exists(path))
            {
                return;
            }

            var canExecute = string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
            if (candidates.TryGetValue(path, out var existing))
            {
                candidates[path] = existing with { IsActive = existing.IsActive || isActive };
            }
            else
            {
                candidates[path] = new ToolCandidate(path, ResolveFinalPath(path), source, isActive, canExecute);
            }
        }
        catch
        {
            // 格式无效或无法解析的路径不进入结果。
        }
    }

    private static IReadOnlyList<DeveloperDiagnosticIssue> BuildDiagnostics(
        DeveloperEnvironmentSnapshot snapshot,
        IReadOnlyList<string> processPathEntries)
    {
        var issues = new List<DeveloperDiagnosticIssue>();
        AddPathIssues(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User), "用户 PATH", issues);
        AddPathIssues(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine), "系统 PATH", issues);

        foreach (var toolchain in snapshot.Toolchains)
        {
            var activeCandidates = toolchain.Installations.Where(item => item.IsActive).ToList();
            if (activeCandidates.Count > 1)
            {
                issues.Add(new DeveloperDiagnosticIssue
                {
                    Severity = DeveloperIssueSeverity.Warning,
                    Title = $"{toolchain.DisplayName} 命令入口存在歧义",
                    Description = "PATH 中发现多个可作为当前入口的命令，请确认优先顺序。",
                    Evidence = string.Join(Environment.NewLine, activeCandidates.Select(item => item.ExecutablePath))
                });
            }
            else if (toolchain.Installations.Count > 1 && toolchain.Id != "dotnet")
            {
                issues.Add(new DeveloperDiagnosticIssue
                {
                    Severity = DeveloperIssueSeverity.Info,
                    Title = $"{toolchain.DisplayName} 存在多个安装",
                    Description = "当前版本由 PATH 顺序决定，其他版本不会被自动删除或修改。",
                    Evidence = string.Join(Environment.NewLine, toolchain.Installations.Select(item => $"{item.Version}  {item.ExecutablePath}"))
                });
            }
            else if (toolchain.Id == "dotnet" && toolchain.Installations.Count > 1)
            {
                issues.Add(new DeveloperDiagnosticIssue
                {
                    Severity = DeveloperIssueSeverity.Info,
                    Title = ".NET SDK 采用并行安装",
                    Description = "多个 SDK 共用 dotnet 主机；当前目录的实际版本由 global.json 与 SDK 回退规则决定，不是由 PATH 中的 SDK 顺序决定。",
                    Evidence = string.Join(Environment.NewLine, toolchain.Installations.Select(item => $"{item.StateText}  {item.Version}  {item.InstallationPath}"))
                });
            }
        }

        AddJavaHomeIssue(snapshot, issues);
        AddPythonPipIssue(snapshot, processPathEntries, issues);
        AddNodeDirectoryVersionIssue(snapshot, issues);
        return issues;
    }

    private static void AddPathIssues(string? rawPath, string scopeName, ICollection<DeveloperDiagnosticIssue> issues)
    {
        var entries = ReadPathEntries(rawPath);
        var duplicateGroups = entries.GroupBy(NormalizePath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToList();
        if (duplicateGroups.Count > 0)
        {
            issues.Add(new DeveloperDiagnosticIssue
            {
                Severity = DeveloperIssueSeverity.Warning,
                Title = $"{scopeName} 存在重复目录",
                Description = $"发现 {duplicateGroups.Count} 组重复项；当前版本只报告，不会自动修改。",
                Evidence = string.Join(Environment.NewLine, duplicateGroups.Select(group => group.First()))
            });
        }

        var missing = entries.Where(path => !Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        if (missing.Count > 0)
        {
            issues.Add(new DeveloperDiagnosticIssue
            {
                Severity = DeveloperIssueSeverity.Warning,
                Title = $"{scopeName} 包含失效目录",
                Description = $"发现 {missing.Count} 个当前不存在的目录；可能来自已卸载或已移动的工具。",
                Evidence = string.Join(Environment.NewLine, missing)
            });
        }
    }

    private static void AddJavaHomeIssue(DeveloperEnvironmentSnapshot snapshot, ICollection<DeveloperDiagnosticIssue> issues)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var java = snapshot.Toolchains.FirstOrDefault(item => item.Id == "java")?.Installations.FirstOrDefault(item => item.IsActive);
        if (string.IsNullOrWhiteSpace(javaHome) || java is null)
        {
            return;
        }

        var expandedJavaHome = NormalizePath(Environment.ExpandEnvironmentVariables(javaHome));
        var activeRoot = NormalizePath(java.InstallationPath);
        if (!string.Equals(expandedJavaHome, activeRoot, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new DeveloperDiagnosticIssue
            {
                Severity = DeveloperIssueSeverity.Warning,
                Title = "JAVA_HOME 与当前 java.exe 不一致",
                Description = "部分构建工具读取 JAVA_HOME，终端则按 PATH 解析 java.exe，这可能导致不同工具使用不同 JDK。",
                Evidence = $"JAVA_HOME：{javaHome}{Environment.NewLine}当前 java.exe：{java.ExecutablePath}"
            });
        }
    }

    private static void AddPythonPipIssue(
        DeveloperEnvironmentSnapshot snapshot,
        IReadOnlyList<string> processPathEntries,
        ICollection<DeveloperDiagnosticIssue> issues)
    {
        var python = snapshot.Toolchains.FirstOrDefault(item => item.Id == "python")?.Installations.FirstOrDefault(item => item.IsActive);
        if (python is null)
        {
            return;
        }

        var pip = processPathEntries.Select(path => Path.Combine(path, "pip.exe")).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(pip))
        {
            return;
        }

        var pythonRoot = NormalizePath(python.InstallationPath);
        var pipDirectory = Directory.GetParent(Path.GetDirectoryName(pip) ?? string.Empty)?.FullName ?? string.Empty;
        if (!string.Equals(pythonRoot, NormalizePath(pipDirectory), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new DeveloperDiagnosticIssue
            {
                Severity = DeveloperIssueSeverity.Warning,
                Title = "python 与 pip 可能不属于同一环境",
                Description = "建议优先使用 python -m pip，以确保包被安装到当前解释器。",
                Evidence = $"python：{python.ExecutablePath}{Environment.NewLine}pip：{pip}"
            });
        }
    }

    private static void AddNodeDirectoryVersionIssue(DeveloperEnvironmentSnapshot snapshot, ICollection<DeveloperDiagnosticIssue> issues)
    {
        var node = snapshot.Toolchains.FirstOrDefault(item => item.Id == "node")?.Installations.FirstOrDefault();
        if (node is null || !node.IsVerified)
        {
            return;
        }

        var directoryName = Path.GetFileName(node.InstallationPath);
        var directoryMajor = Regex.Match(directoryName, "(?:node(?:js)?)[^0-9]*(?<major>[0-9]+)", RegexOptions.IgnoreCase);
        var versionMajor = Regex.Match(node.Version, "^(?<major>[0-9]+)");
        if (directoryMajor.Success && versionMajor.Success &&
            !string.Equals(directoryMajor.Groups["major"].Value, versionMajor.Groups["major"].Value, StringComparison.Ordinal))
        {
            issues.Add(new DeveloperDiagnosticIssue
            {
                Severity = DeveloperIssueSeverity.Warning,
                Title = "Node.js 目录名称与实际版本不一致",
                Description = "版本命令结果可信度高于文件夹名称；该目录可能曾被原位升级或替换。",
                Evidence = $"目录名称：{directoryName}（标示主版本 {directoryMajor.Groups["major"].Value}）{Environment.NewLine}" +
                           $"实际命令版本：v{node.Version}{Environment.NewLine}" +
                           $"可执行文件：{node.ExecutablePath}"
            });
        }
    }

    private static IReadOnlyList<string> ReadPathEntries(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return Array.Empty<string>();
        }

        return rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value =>
            {
                try { return Path.GetFullPath(value); }
                catch { return value; }
            })
            .ToList();
    }

    private static IReadOnlyList<string> ReadEffectivePersistentPathEntries()
    {
        var machineEntries = ReadPathEntries(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        var userEntries = ReadPathEntries(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        return machineEntries.Concat(userEntries).ToList();
    }

    private static string GetInstallationRoot(string toolchainId, string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var directoryName = Path.GetFileName(directory);
        if (toolchainId is "java" or "maven" or "gradle" && string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(directory)?.FullName ?? directory;
        }

        if (toolchainId == "git" && string.Equals(directoryName, "cmd", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(directory)?.FullName ?? directory;
        }

        if (toolchainId == "git" && string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(directory);
            return string.Equals(parent?.Name, "mingw64", StringComparison.OrdinalIgnoreCase)
                ? parent?.Parent?.FullName ?? parent?.FullName ?? directory
                : parent?.FullName ?? directory;
        }

        return directory;
    }

    private static string? TryReadVersionFromFiles(string toolchainId, string installationRoot)
    {
        try
        {
            if (toolchainId == "java")
            {
                var releaseFile = Path.Combine(installationRoot, "release");
                if (File.Exists(releaseFile))
                {
                    var versionLine = File.ReadLines(releaseFile).FirstOrDefault(line => line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
                    return versionLine?.Split('=', 2)[1].Trim().Trim('"');
                }
            }

            if (toolchainId == "maven")
            {
                return ReadJarVersion(installationRoot, "maven-core-*.jar", "maven-core-");
            }

            if (toolchainId == "gradle")
            {
                var version = ReadJarVersion(installationRoot, "gradle-core-*.jar", "gradle-core-");
                return version?.StartsWith("api-", StringComparison.OrdinalIgnoreCase) == true ? version[4..] : version;
            }
        }
        catch
        {
            // 文件版本只是辅助来源，失败时保留已发现状态。
        }

        return null;
    }

    private static string? ReadJarVersion(string root, string pattern, string prefix)
    {
        var libDirectory = Path.Combine(root, "lib");
        if (!Directory.Exists(libDirectory))
        {
            return null;
        }

        var fileName = Directory.EnumerateFiles(libDirectory, pattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return fileName?[prefix.Length..];
    }

    private static string ParseVersion(string toolchainId, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var pattern = toolchainId switch
        {
            "java" => "(?:version\\s+\"|openjdk\\s+)(?<version>[0-9][^\"\\s]*)",
            "python" => "Python\\s+(?<version>[^\\s]+)",
            "node" => "v(?<version>[0-9][^\\s]*)",
            "git" => "git version\\s+(?<version>[^\\s]+)",
            _ => "(?<version>[0-9]+(?:\\.[0-9A-Za-z-]+)+)"
        };
        var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value.Trim() : string.Empty;
    }

    private static string ParseJavaHome(string output)
    {
        var match = Regex.Match(output, "^\\s*java\\.home\\s*=\\s*(?<home>.+?)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups["home"].Value.Trim() : string.Empty;
    }

    private static string NormalizeJavaInstallationRoot(string runtimeHome)
    {
        var home = NormalizePath(runtimeHome);
        if (string.Equals(Path.GetFileName(home), "jre", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(home)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && File.Exists(Path.Combine(parent, "bin", "javac.exe")))
            {
                return parent;
            }
        }

        return home;
    }

    private static string FirstMeaningfulLine(string output) =>
        SplitLines(output).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "版本命令已成功执行。";

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

    private static string GuessArchitecture(string executablePath)
    {
        if (executablePath.Contains("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return "ARM64";
        }

        if (executablePath.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains("x64", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }

        if (executablePath.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(executablePath, "(?:^|[\\\\/_-])x86(?:[\\\\/_-]|$)", RegexOptions.IgnoreCase))
        {
            return "x86";
        }

        return Environment.Is64BitOperatingSystem ? "x64 / 未验证" : "x86 / 未验证";
    }

    private static bool IsWindowsAppAlias(string path) =>
        path.Contains("\\Microsoft\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsPythonVirtualEnvironment(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        if (File.Exists(Path.Combine(directory, "pyvenv.cfg")))
        {
            return true;
        }

        var parent = Directory.GetParent(directory)?.FullName;
        return string.Equals(Path.GetFileName(directory), "Scripts", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(parent) &&
               File.Exists(Path.Combine(parent, "pyvenv.cfg"));
    }

    private static bool IsPrivateHostRuntime(string path) =>
        path.Contains("\\.cache\\codex-runtimes\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\AppData\\Local\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase);

    private static string ResolveFinalPath(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var capacity = 512u;
            while (capacity <= 32768)
            {
                var builder = new StringBuilder((int)capacity);
                var length = GetFinalPathNameByHandle(handle, builder, capacity, 0);
                if (length == 0)
                {
                    break;
                }

                if (length < capacity)
                {
                    var resolved = builder.ToString();
                    if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        return @"\\" + resolved[8..];
                    }

                    return resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? resolved[4..] : resolved;
                }

                capacity = length + 1;
            }
        }
        catch
        {
            // 无法打开句柄时继续使用原路径，扫描仍可降级完成。
        }

        return NormalizePath(path);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private sealed record ToolDefinition(
        string Id,
        string DisplayName,
        string Description,
        string IconGlyph,
        string IconBackground,
        string IconForeground,
        string[] CommandNames,
        string[] VersionArguments);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle fileHandle, StringBuilder filePath, uint characterCount, uint flags);

    private sealed record ToolCandidate(string ExecutablePath, string CanonicalExecutablePath, string Source, bool IsActive, bool CanExecute);
}
