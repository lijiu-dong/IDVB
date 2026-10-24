using System.Diagnostics;

namespace IDVBuff.Lifecycle;

internal static class LegalDocumentReader
{
    internal static ProcessStartInfo CreateStartInfo(string document)
    {
        if (document is not ("LICENSE" or "TERMS")) throw new ArgumentOutOfRangeException(nameof(document));
        using var resource = typeof(LegalDocumentReader).Assembly.GetManifestResourceStream($"IDVB.Legal.{document}.txt")
            ?? throw new FileNotFoundException("缺少软件协议。");
        var directory = Path.Combine(Path.GetTempPath(), "IDVB-Legal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, document == "LICENSE" ? "软件许可.txt" : "使用条款.txt");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            resource.CopyTo(output);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"),
            UseShellExecute = false
        };
        start.ArgumentList.Add(path);
        return start;
    }
}
