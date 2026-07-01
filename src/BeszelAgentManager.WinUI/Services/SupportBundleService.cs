using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BeszelAgentManager.WinUI.Services;

internal sealed partial class SupportBundleService
{
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var outputDir = Path.Combine(ManagerPaths.DataDir, "support_bundles");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"support-bundle-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeszelAgentManager-support-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            await WriteRedactedConfigAsync(Path.Combine(tempDir, "config-redacted.json"), cancellationToken);
            await CopyRedactedAsync(ManagerPaths.ManagerLogPath, Path.Combine(tempDir, "manager.log"), cancellationToken);
            await CopyRedactedAsync(ManagerPaths.AgentLogPath, Path.Combine(tempDir, "beszel-agent.log"), cancellationToken);
            await WriteCommandAsync(Path.Combine(tempDir, "service-diagnostics.txt"), "sc.exe", ["queryex", "Beszel Agent"], cancellationToken);
            await WriteCommandAsync(Path.Combine(tempDir, "service-config.txt"), "sc.exe", ["qc", "Beszel Agent"], cancellationToken);
            await WriteCommandAsync(
                Path.Combine(tempDir, "scheduled-tasks.txt"),
                "schtasks.exe",
                ["/Query", "/FO", "LIST", "/V"],
                cancellationToken);
            await WriteSystemDetailsAsync(Path.Combine(tempDir, "system-details.txt"), cancellationToken);
            ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, false);
            return outputPath;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
            }
        }
    }

    private static async Task WriteRedactedConfigAsync(string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(ManagerPaths.ConfigPath))
        {
            return;
        }

        var node = JsonNode.Parse(await File.ReadAllTextAsync(ManagerPaths.ConfigPath, cancellationToken));
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("key"))
            {
                obj["key"] = "***redacted***";
            }
            if (obj.ContainsKey("token"))
            {
                obj["token"] = "***redacted***";
            }
            if (obj.ContainsKey("github_token_enc"))
            {
                obj["github_token_enc"] = "***redacted***";
            }
        }

        await File.WriteAllTextAsync(
            destination,
            node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}",
            cancellationToken);
    }

    private static async Task CopyRedactedAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
        {
            return;
        }

        var text = await ReadSharedTextAsync(source, 5_000_000, cancellationToken);
        text = SecretRegex().Replace(text, "$1=***redacted***");
        text = SshKeyRegex().Replace(text, "ssh-$1 ***redacted***");
        text = GuidRegex().Replace(text, "***redacted***");
        await File.WriteAllTextAsync(destination, text, cancellationToken);
    }

    private static async Task<string> ReadSharedTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task WriteCommandAsync(
        string destination,
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        fileName = fileName.ToLowerInvariant() switch
        {
            "sc.exe" or "schtasks.exe" => Path.Combine(Environment.SystemDirectory, fileName),
            _ => throw new InvalidOperationException($"Diagnostic executable is not allowlisted: {fileName}"),
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        output += await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await File.WriteAllTextAsync(destination, output, cancellationToken);
    }

    private static async Task WriteSystemDetailsAsync(string destination, CancellationToken cancellationToken)
    {
        var lines = new[]
        {
            $"Timestamp={DateTime.Now:O}",
            $"MachineName={Environment.MachineName}",
            $"UserName={Environment.UserName}",
            $"OS={Environment.OSVersion}",
            $"ProcessArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            $"ProgramFiles={ManagerPaths.ProgramFiles}",
            $"ProgramData={ManagerPaths.ProgramData}",
            $"ManagerVersion={AppInfo.Version}",
        };
        await File.WriteAllLinesAsync(destination, lines, cancellationToken);
    }

    [GeneratedRegex(@"(?im)\b(KEY|TOKEN)\s*[:=]\s*[^\s\r\n]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"ssh-(rsa|ed25519)\s+[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex SshKeyRegex();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
}
