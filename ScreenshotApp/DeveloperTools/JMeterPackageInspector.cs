using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotApp.DeveloperTools;

/// <summary>从 JMeter 核心 JAR 的清单读取发行版本，避免为了识别版本而启动批处理脚本。</summary>
internal static class JMeterPackageInspector
{
    public static string? TryReadVersion(string installationRoot)
    {
        try
        {
            var coreJar = Path.Combine(installationRoot, "lib", "ext", "ApacheJMeter_core.jar");
            if (!File.Exists(coreJar))
            {
                return null;
            }

            using var archive = ZipFile.OpenRead(coreJar);
            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null)
            {
                return null;
            }

            using var stream = manifest.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            var match = Regex.Match(
                content,
                "^Implementation-Version:\\s*(?<version>[^\\s]+)\\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return match.Success ? match.Groups["version"].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
