using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ScreenshotApp.Converters;

internal sealed record PdfEngineStatus(bool HasMicrosoftOffice, bool HasWps, string? LibreOfficePath)
{
    public bool HasLibreOffice => !string.IsNullOrWhiteSpace(LibreOfficePath);

    public string Summary => $"Office：{(HasMicrosoftOffice ? "已检测到" : "未检测到")}  ·  WPS：{(HasWps ? "已检测到" : "未检测到")}  ·  LibreOffice：{(HasLibreOffice ? "已检测到" : "未检测到")}";
}

internal sealed class PdfConversionService
{
    internal static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
        ".pdf", ".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ods", ".ppt", ".pptx", ".odp"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public PdfEngineStatus GetEngineStatus() => new(
        HasProgId("Word.Application") || HasProgId("Excel.Application") || HasProgId("PowerPoint.Application"),
        HasAnyProgId("kwps.Application", "wps.Application", "ket.Application", "et.Application", "kwpp.Application", "wpp.Application"),
        FindLibreOffice());

    public async Task<string> AppendSourceAsync(string sourcePath, PdfDocument target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            AppendPdf(sourcePath, target);
            return "本地合并";
        }

        if (ImageExtensions.Contains(extension))
        {
            AppendImage(sourcePath, target);
            return "X-Tool 本地生成";
        }

        var converted = await ConvertOfficeFileAsync(sourcePath, cancellationToken);
        try
        {
            AppendPdf(converted.OutputPath, target);
            return converted.EngineName;
        }
        finally
        {
            TryDelete(converted.OutputDirectory);
        }
    }

    private async Task<(string OutputPath, string OutputDirectory, string EngineName)> ConvertOfficeFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var engineStatus = GetEngineStatus();

        if (engineStatus.HasMicrosoftOffice)
        {
            try
            {
                var result = await RunOnStaThreadAsync(() => ExportWithOffice(sourcePath), cancellationToken);
                return (result, Path.GetDirectoryName(result)!, "Microsoft Office");
            }
            catch (Exception exception)
            {
                errors.Add($"Office：{ReadableError(exception)}");
            }
        }

        if (engineStatus.HasWps)
        {
            try
            {
                var result = await RunOnStaThreadAsync(() => ExportWithWps(sourcePath), cancellationToken);
                return (result, Path.GetDirectoryName(result)!, "WPS Office");
            }
            catch (Exception exception)
            {
                errors.Add($"WPS：{ReadableError(exception)}");
            }
        }

        if (engineStatus.HasLibreOffice)
        {
            try
            {
                var result = await ExportWithLibreOfficeAsync(sourcePath, engineStatus.LibreOfficePath!, cancellationToken);
                return (result, Path.GetDirectoryName(result)!, "LibreOffice");
            }
            catch (Exception exception)
            {
                errors.Add($"LibreOffice：{ReadableError(exception)}");
            }
        }

        if (!engineStatus.HasMicrosoftOffice && !engineStatus.HasWps && !engineStatus.HasLibreOffice)
        {
            throw new InvalidOperationException("未检测到 Microsoft Office、WPS 或 LibreOffice。图片和已有 PDF 可直接处理；请安装 LibreOffice 后再转换办公文档。");
        }

        throw new InvalidOperationException($"办公文档转换失败。{string.Join("；", errors)}");
    }

    private static void AppendPdf(string sourcePath, PdfDocument target)
    {
        using var input = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        foreach (var page in input.Pages)
        {
            target.AddPage(page);
        }
    }

    private static void AppendImage(string sourcePath, PdfDocument target)
    {
        try
        {
            AppendImageCore(sourcePath, target);
        }
        catch
        {
            var normalizedDirectory = CreateTemporaryDirectory();
            var normalizedPath = Path.Combine(normalizedDirectory, "normalized.png");
            try
            {
                NormalizeImageToPng(sourcePath, normalizedPath);
                AppendImageCore(normalizedPath, target);
            }
            finally
            {
                TryDelete(normalizedDirectory);
            }
        }
    }

    private static void AppendImageCore(string sourcePath, PdfDocument target)
    {
        using var image = XImage.FromFile(sourcePath);
        var page = target.AddPage();
        if (image.PointWidth > image.PointHeight)
        {
            page.Orientation = PdfSharp.PageOrientation.Landscape;
        }

        page.Size = PdfSharp.PageSize.A4;
        using var graphics = XGraphics.FromPdfPage(page);
        const double margin = 30;
        var scale = Math.Min((page.Width.Point - margin * 2) / image.PointWidth, (page.Height.Point - margin * 2) / image.PointHeight);
        var width = image.PointWidth * scale;
        var height = image.PointHeight * scale;
        graphics.DrawImage(image, (page.Width.Point - width) / 2, (page.Height.Point - height) / 2, width, height);
    }

    private static void NormalizeImageToPng(string sourcePath, string outputPath)
    {
        var decoder = BitmapDecoder.Create(new Uri(sourcePath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames[0]);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static string ExportWithOffice(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return extension switch
        {
            ".doc" or ".docx" or ".rtf" => ExportWord(sourcePath, "Word.Application"),
            ".xls" or ".xlsx" or ".csv" => ExportExcel(sourcePath, "Excel.Application"),
            ".ppt" or ".pptx" => ExportPowerPoint(sourcePath, "PowerPoint.Application"),
            _ => throw new InvalidOperationException("当前格式需要 LibreOffice 转换")
        };
    }

    private static string ExportWithWps(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return extension switch
        {
            ".doc" or ".docx" or ".rtf" or ".odt" => ExportWord(sourcePath, FindProgId("kwps.Application", "wps.Application")!),
            ".xls" or ".xlsx" or ".csv" or ".ods" => ExportExcel(sourcePath, FindProgId("ket.Application", "et.Application")!),
            ".ppt" or ".pptx" or ".odp" => ExportPowerPoint(sourcePath, FindProgId("kwpp.Application", "wpp.Application")!),
            _ => throw new InvalidOperationException("当前文件格式不受支持")
        };
    }

    private static string ExportWord(string sourcePath, string progId)
    {
        dynamic app = CreateApplication(progId);
        object? document = null;
        var output = CreateTemporaryOutputPath(sourcePath);
        try
        {
            app.Visible = false;
            app.DisplayAlerts = 0;
            document = app.Documents.Open(sourcePath, ReadOnly: true, AddToRecentFiles: false, Visible: false);
            ((dynamic)document).ExportAsFixedFormat(output, 17);
            return output;
        }
        finally
        {
            TryClose(document, false);
            TryQuit(app);
        }
    }

    private static string ExportExcel(string sourcePath, string progId)
    {
        dynamic app = CreateApplication(progId);
        object? workbook = null;
        var output = CreateTemporaryOutputPath(sourcePath);
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            workbook = app.Workbooks.Open(sourcePath, ReadOnly: true);
            ((dynamic)workbook).ExportAsFixedFormat(0, output);
            return output;
        }
        finally
        {
            TryClose(workbook, false);
            TryQuit(app);
        }
    }

    private static string ExportPowerPoint(string sourcePath, string progId)
    {
        dynamic app = CreateApplication(progId);
        object? presentation = null;
        var output = CreateTemporaryOutputPath(sourcePath);
        try
        {
            app.Visible = false;
            presentation = app.Presentations.Open(sourcePath, false, false, false);
            ((dynamic)presentation).ExportAsFixedFormat(output, 2);
            return output;
        }
        finally
        {
            TryClose(presentation);
            TryQuit(app);
        }
    }

    private static async Task<string> ExportWithLibreOfficeAsync(string sourcePath, string executablePath, CancellationToken cancellationToken)
    {
        var outputDirectory = CreateTemporaryDirectory();
        var profileDirectory = Path.Combine(outputDirectory, "profile");
        Directory.CreateDirectory(profileDirectory);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add($"-env:UserInstallation={new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri}");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("pdf");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(sourcePath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 LibreOffice");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        await process.WaitForExitAsync(cancellationToken);
        var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException("LibreOffice 未生成 PDF 文件");
        }

        return output;
    }

    private static async Task<T> RunOnStaThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private static dynamic CreateApplication(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"未找到 {progId}");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"无法启动 {progId}");
    }

    private static bool HasProgId(string progId) => Type.GetTypeFromProgID(progId) is not null;
    private static bool HasAnyProgId(params string[] progIds) => progIds.Any(HasProgId);
    private static string? FindProgId(params string[] progIds) => progIds.FirstOrDefault(HasProgId);

    private static string? FindLibreOffice()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.com")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string CreateTemporaryOutputPath(string sourcePath)
    {
        var directory = CreateTemporaryDirectory();
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "XTool", "Pdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadableError(Exception exception) => exception is COMException ? "应用程序未能完成导出" : exception.Message;

    private static void TryClose(object? value, params object[] arguments)
    {
        if (value is null) return;
        try
        {
            if (arguments.Length == 0) ((dynamic)value).Close();
            else ((dynamic)value).Close(arguments[0]);
        }
        catch { }
        TryRelease(value);
    }

    private static void TryQuit(object? value)
    {
        if (value is null) return;
        try { ((dynamic)value).Quit(); } catch { }
        TryRelease(value);
    }

    private static void TryRelease(object value)
    {
        try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }
}
