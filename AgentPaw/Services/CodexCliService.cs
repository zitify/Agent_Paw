using System.Diagnostics;
using System.IO;

namespace AgentPaw.Services;

public class CodexCliService
{
    private readonly ApiKeyService _apiKeyService;
    private const string SettingKey = "CODEX_CLI_ENABLED";
    private const int TimeoutMs = 180_000;

    private readonly List<Process> _activeProcesses = [];
    private readonly object _lock = new();

    public CodexCliService(ApiKeyService apiKeyService)
    {
        _apiKeyService = apiKeyService;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                // npm 셸심(codex.cmd)은 cmd.exe를 통해 실행해야 한다 (Windows)
                FileName = "cmd.exe",
                Arguments = "/c codex --version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.Start();
            using var cts = new CancellationTokenSource(5000);
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsEnabledAsync()
    {
        var val = await _apiKeyService.GetApiKeyAsync(SettingKey);
        return val == "true";
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _apiKeyService.SetApiKeyAsync(SettingKey, enabled ? "true" : "false");
    }

    public async Task<string> CallAsync(string systemPrompt, string userPrompt)
    {
        var fullPrompt = !string.IsNullOrEmpty(systemPrompt)
            ? $"{systemPrompt}\n\n===\n{userPrompt}"
            : userPrompt;

        // codex exec의 stdout에는 배너·세션 로그가 섞이므로, 최종 답변만 파일로 받는다.
        var outPath = Path.Combine(Path.GetTempPath(), $"codex-out-{Guid.NewGuid():N}.txt");

        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            // npm 셸심(codex.cmd)은 cmd.exe를 통해 실행해야 한다 (Windows)
            FileName = "cmd.exe",
            Arguments = $"/c codex exec --skip-git-repo-check -s read-only --color never --output-last-message \"{outPath}\" -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        lock (_lock) _activeProcesses.Add(process);

        try
        {
            process.Start();

            // stdin으로 프롬프트 전달 후 즉시 닫아 무한 대기 방지
            await process.StandardInput.WriteAsync(fullPrompt);
            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(TimeoutMs);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw new InvalidOperationException("CODEX_CLI_FAILED: timeout (180s)");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"CODEX_CLI_FAILED: exit {process.ExitCode} {stderr.Trim()}");

            // 최종 답변 파일을 우선 사용, 비어 있으면 stdout 폴백
            var lastMsg = File.Exists(outPath) ? (await File.ReadAllTextAsync(outPath)).Trim() : string.Empty;
            return string.IsNullOrEmpty(lastMsg) ? stdout.Trim() : lastMsg;
        }
        finally
        {
            lock (_lock) _activeProcesses.Remove(process);
            process.Dispose();
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    public void KillAll()
    {
        lock (_lock)
        {
            foreach (var p in _activeProcesses)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
            }
            _activeProcesses.Clear();
        }
    }
}
