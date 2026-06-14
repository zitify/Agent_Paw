using System.Diagnostics;
using AgentPaw.Services;

namespace AgentPaw.Services;

public class ClaudeCliService
{
    private readonly ApiKeyService _apiKeyService;
    private const string SettingKey = "CLAUDE_CLI_ENABLED";
    private const string CliCommand = "claude";
    private const int TimeoutMs = 180_000;

    private readonly List<Process> _activeProcesses = [];
    private readonly Queue<Process> _warmPool = new();
    private readonly object _lock = new();
    // 콜드스타트 선제거용 워밍업 풀 크기. 오케스트레이터는 순차 호출이라 1로 충분(소비 즉시 백그라운드 보충).
    private const int WarmPoolSize = 1;
    private int _warmingStarted;  // 0 = 현재 앱 세션에서 미시작, 1 = 시작됨

    public ClaudeCliService(ApiKeyService apiKeyService)
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
                // npm 셸심(claude.cmd)은 cmd.exe를 통해 실행해야 한다 (Windows)
                FileName = "cmd.exe",
                Arguments = "/c claude --version",
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

    // 앱 시작 시 1회 호출 — Claude CLI 활성 시 프로세스를 미리 띄워 풀에 데워둔다.
    // claude -p 는 호출마다 새 프로세스라 매번 콜드스타트(Node 부팅)가 든다. 이를 선제 지불해
    // 풀에 idle 프로세스로 대기시켜 두면, 실제 채팅 시 stdin만 흘려보내 콜드스타트 없이 바로 추론한다.
    // 각 호출은 여전히 독립 프로세스라 페르소나별 시스템프롬프트·무상태성이 보존된다(컨텍스트 누수 없음).
    public async Task EnsureWarmStartedAsync()
    {
        if (!await IsEnabledAsync().ConfigureAwait(false)) return;       // CLI 비활성이면 워밍 불필요
        if (Interlocked.Exchange(ref _warmingStarted, 1) == 1) return;   // 현재 앱 세션에서 이미 시작
        for (int i = 0; i < WarmPoolSize; i++)
            _ = Task.Run(SpawnWarmProcess);
    }

    private Process StartClaudeProcess()
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            // npm 셸심(claude.cmd)은 cmd.exe를 통해 실행해야 한다 (Windows)
            FileName = "cmd.exe",
            Arguments = "/c claude -p --output-format text",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        process.Start();   // 여기서 Node 부팅(콜드스타트)이 일어나고 stdin 대기 상태가 된다
        lock (_lock) _activeProcesses.Add(process);
        return process;
    }

    // 풀에 데워진 프로세스 1개를 추가로 띄운다 (백그라운드)
    private void SpawnWarmProcess()
    {
        try
        {
            var p = StartClaudeProcess();
            lock (_lock) _warmPool.Enqueue(p);
        }
        catch { /* 워밍 실패는 무시 — CallAsync 가 필요 시 즉석 spawn 한다 */ }
    }

    // 데워둔 살아있는 프로세스가 있으면 재사용(콜드스타트 회피), 없으면 즉석 spawn
    private Process TakeOrSpawn()
    {
        lock (_lock)
        {
            while (_warmPool.Count > 0)
            {
                var p = _warmPool.Dequeue();
                if (!p.HasExited) return p;
                try { _activeProcesses.Remove(p); p.Dispose(); } catch { }   // idle 중 죽은 프로세스 폐기
            }
        }
        return StartClaudeProcess();
    }

    public async Task<string> CallAsync(string systemPrompt, string userPrompt)
    {
        var fullPrompt = !string.IsNullOrEmpty(systemPrompt)
            ? $"{systemPrompt}\n\n===\n{userPrompt}"
            : userPrompt;

        // 데워둔 프로세스 우선 사용(콜드스타트 회피) → 소비분은 백그라운드로 즉시 보충
        var process = TakeOrSpawn();
        _ = Task.Run(SpawnWarmProcess);

        try
        {
            // stdin으로 프롬프트 전달 (Windows cmd.exe argument splitting 우회)
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
                throw new InvalidOperationException("CLAUDE_CLI_FAILED: timeout (180s)");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"CLAUDE_CLI_FAILED: exit {process.ExitCode} {stderr.Trim()}");

            return stdout.Trim();
        }
        finally
        {
            lock (_lock) _activeProcesses.Remove(process);
            process.Dispose();
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
            _warmPool.Clear();   // 데워둔 idle 프로세스도 위 _activeProcesses 루프에서 종료됨
        }
    }
}
