using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace BeszelAgentManager.WinUI.Services;

internal sealed class ElevatedHelperService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _pipeName;
    private Process? _helperProcess;

    public async Task<int> RunServiceActionAsync(string action)
    {
        if (action is not ("start" or "stop" or "restart" or "edit"))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync($"service {action}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> OpenServiceEditorAsync()
    {
        return RunServiceActionAsync("edit");
    }

    public async Task<int> ApplyConfigurationAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync("config apply");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ApplyManagerTasksAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync("manager tasks apply");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ApplyHubUrlOverrideAsync(string hubUrl)
    {
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            throw new ArgumentException("A Hub URL is required.", nameof(hubUrl));
        }

        var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(hubUrl.Trim()));
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync($"config hub-url {encodedUrl}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> InstallAgentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync("agent install");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> UpdateAgentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync("agent update");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> InstallAgentVersionAsync(string version)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync($"agent install-version {version}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> UninstallAgentAsync(bool keepAgentLogs)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync(keepAgentLogs ? "agent uninstall keep-logs" : "agent uninstall");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RotateAgentLogsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync("agent logs rotate");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ResetAgentFingerprintAsync()
    {
        await _gate.WaitAsync();
        try
        {
            EnsureHelperStarted();
            return await SendCommandWithRetryAsync("agent fingerprint reset");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureHelperStarted()
    {
        if (_helperProcess is { HasExited: false } && !string.IsNullOrWhiteSpace(_pipeName))
        {
            return;
        }

        var helperPath = Path.Combine(AppContext.BaseDirectory, "BeszelAgentManager.Helper.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("The elevated helper is not present in the application folder.", helperPath);
        }

        _pipeName = $"BeszelAgentManager-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _helperProcess = Process.Start(new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = $"--server {_pipeName} {Environment.ProcessId}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException("Could not start the elevated helper.");
    }

    private async Task<int> SendCommandWithRetryAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(_pipeName))
        {
            throw new InvalidOperationException("The elevated helper is not initialized.");
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            if (_helperProcess is { HasExited: true })
            {
                throw new InvalidOperationException($"The elevated helper exited with code {_helperProcess.ExitCode}.");
            }

            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(300);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await writer.WriteLineAsync(command);
                var response = await reader.ReadLineAsync();
                return int.TryParse(response, out var exitCode) ? exitCode : 1;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("Timed out connecting to the elevated helper.", lastError);
    }
}
