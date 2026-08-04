using System.Diagnostics;
using System.IO;

namespace AgentPaw.Services;

public class CodexCliService
{
    private readonly ApiKeyService _apiKeyService;
    private const string SettingKey = "CODEX_CLI_ENABLED";
    private const int TimeoutMs = 180_000;

    private readonly List<Process> _activeProcesses = [];
    private readonly Queue<(Process Proc, string OutPath)> _warmPool = new();
    private readonly object _lock = new();
    // 콜드스타트 선제거용 워밍업 풀. codex exec는 호출마다 outPath를 인자에 박으므로 (프로세스, outPath) 쌍으로 보관.
    private const int WarmPoolSize = 1;
    private int _warmingStarted;

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

    // 앱 시작 시 1회 — Codex CLI 활성 시 프로세스를 미리 띄워 풀에 데워둔다(콜드스타트 선지불).
    public async Task EnsureWarmStartedAsync()
    {
        if (!await IsEnabledAsync().ConfigureAwait(false)) return;       // CLI 비활성이면 워밍 불필요
        if (Interlocked.Exchange(ref _warmingStarted, 1) == 1) return;   // 현재 앱 세션에서 이미 시작
        for (int i = 0; i < WarmPoolSize; i++)
            _ = Task.Run(SpawnWarmProcess);
    }

    // codex exec는 outPath를 인자에 박아야 하므로 프로세스와 outPath를 쌍으로 만들어 반환한다.
    private (Process Proc, string OutPath) StartCodexProcess()
    {
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
        process.Start();   // 여기서 콜드스타트가 일어나고 stdin 대기 상태가 된다
        lock (_lock) _activeProcesses.Add(process);
        return (process, outPath);
    }

    private void SpawnWarmProcess()
    {
        try
        {
            var entry = StartCodexProcess();
            lock (_lock) _warmPool.Enqueue(entry);
        }
        catch { /* 워밍 실패는 무시 — CallAsync 가 필요 시 즉석 spawn */ }
    }

    private (Process Proc, string OutPath) TakeOrSpawn()
    {
        lock (_lock)
        {
            while (_warmPool.Count > 0)
            {
                var entry = _warmPool.Dequeue();
                if (!entry.Proc.HasExited) return entry;
                try { _activeProcesses.Remove(entry.Proc); entry.Proc.Dispose(); } catch { }
                try { if (File.Exists(entry.OutPath)) File.Delete(entry.OutPath); } catch { }
            }
        }
        return StartCodexProcess();
    }

    public async Task<string> CallAsync(string systemPrompt, string userPrompt)
    {
        var fullPrompt = !string.IsNullOrEmpty(systemPrompt)
            ? $"{systemPrompt}\n\n===\n{userPrompt}"
            : userPrompt;

        // 데워둔 프로세스 우선 사용(콜드스타트 회피) → 소비분은 백그라운드로 즉시 보충
        var (process, outPath) = TakeOrSpawn();
        _ = Task.Run(SpawnWarmProcess);

        try
        {
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
            foreach (var entry in _warmPool)
            {
                try { if (File.Exists(entry.OutPath)) File.Delete(entry.OutPath); } catch { }
            }
            _warmPool.Clear();
        }
    }
}
