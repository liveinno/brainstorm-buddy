using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Services;

public enum LocalSttStatus
{
    Stopped,
    Starting,
    Building,
    Running,
    Stopping,
    Error
}

public class LocalSttService : IDisposable
{
    private readonly LocalSttConfig _config;
    private readonly LoggingService _logger;
    private readonly HttpClient _http;
    private Process? _dockerProcess;
    private CancellationTokenSource? _buildCts;
    private string _previousSttBaseUrl = string.Empty;
    private string _previousSttModel = string.Empty;

    public LocalSttStatus Status { get; private set; } = LocalSttStatus.Stopped;
    public string BuildLog { get; private set; } = string.Empty;
    public string CurrentCpu { get; private set; } = "-";
    public string CurrentRam { get; private set; } = "-";
    public string CurrentPingMs { get; private set; } = "-";
    public string CurrentUptime { get; private set; } = "-";
    public string ContainerName => _config.ContainerName;
    public int Port => _config.Port;
    public string EndpointUrl => $"http://localhost:{_config.Port}/v1";

    public event EventHandler<string>? LogReceived;
    public event EventHandler<LocalSttStatus>? StatusChanged;
    public event EventHandler? StatsUpdated;

    public LocalSttService(LocalSttConfig config, LoggingService logger)
    {
        _config = config;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info --format '{{.ServerVersion}}'")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync(Action<string>? onProgress = null)
    {
        SetStatus(LocalSttStatus.Starting);
        AppendLog("Проверка Docker...");

        if (!await IsDockerAvailableAsync())
        {
            SetStatus(LocalSttStatus.Error);
            AppendLog("Ошибка: Docker Desktop не обнаружен");
            throw new InvalidOperationException(
                "Docker Desktop не найден. Скачайте и установите Docker Desktop с https://www.docker.com/products/docker-desktop/ , затем перезапустите приложение.");
        }

        AppendLog("Попытка запуска существующего контейнера...");
        try
        {
            await RunDockerCommandAsync("start", _config.ContainerName, CancellationToken.None);
            SetStatus(LocalSttStatus.Running);
            AppendLog($"Контейнер запущен на порту {_config.Port}");
            _ = Task.Run(() => StatsPollLoopAsync(CancellationToken.None));
            return;
        }
        catch
        {
            AppendLog("Контейнер не найден или остановлен с ошибкой. Начинаем сборку...");
        }

        SetStatus(LocalSttStatus.Building);
        var bridgeDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LocalSttBridge");
        if (!Directory.Exists(bridgeDir))
            bridgeDir = Path.Combine(AppContext.BaseDirectory, "LocalSttBridge");
        if (!Directory.Exists(bridgeDir))
        {
            SetStatus(LocalSttStatus.Error);
            AppendLog($"Ошибка: папка LocalSttBridge не найдена ({bridgeDir})");
            throw new InvalidOperationException("Папка LocalSttBridge не найдена. Переустановите приложение.");
        }

        AppendLog($"Сборка Docker-образа (может занимать 10-30 секунд)...");
        AppendLog($"Путь: {bridgeDir}");

        _buildCts = new CancellationTokenSource();
        await RunDockerCommandAsync("build", $"-t brainstorm-local-stt \"{bridgeDir}\"", _buildCts.Token, onProgress);

        if (_buildCts.IsCancellationRequested) return;

        SetStatus(LocalSttStatus.Starting);
        AppendLog("Запуск нового контейнера...");

        await StopExistingContainerAsync();

        var runArgs = $"-d --name {_config.ContainerName} -p {_config.Port}:8000 -v brainstorm_stt_cache:/root/.cache/huggingface --restart no brainstorm-local-stt";
        await RunDockerCommandAsync("run", runArgs, CancellationToken.None, onProgress);

        SetStatus(LocalSttStatus.Running);
        AppendLog($"Контейнер создан и запущен на порту {_config.Port}");

        _ = Task.Run(() => StatsPollLoopAsync(CancellationToken.None));
    }

    public async Task StopAsync()
    {
        SetStatus(LocalSttStatus.Stopping);
        AppendLog("Остановка контейнера...");

        _buildCts?.Cancel();

        try {
            await RunDockerCommandAsync("stop", _config.ContainerName, CancellationToken.None);
        } catch { }

        SetStatus(LocalSttStatus.Stopped);
        AppendLog("Контейнер приостановлен.");
    }

    public async Task RemoveAsync()
    {
        SetStatus(LocalSttStatus.Stopping);
        AppendLog("Удаление контейнера...");

        _buildCts?.Cancel();

        try {
            await RunDockerCommandAsync("stop", _config.ContainerName, CancellationToken.None);
        } catch { }
        try {
            await RunDockerCommandAsync("rm", _config.ContainerName, CancellationToken.None);
        } catch { }

        SetStatus(LocalSttStatus.Stopped);
        AppendLog("Контейнер полностью удален.");
    }

    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var resp = await _http.GetAsync($"{EndpointUrl}/models");
            sw.Stop();
            CurrentPingMs = $"{sw.ElapsedMilliseconds} мс";
            StatsUpdated?.Invoke(this, EventArgs.Empty);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            CurrentPingMs = "нет ответа";
            StatsUpdated?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    public async Task<string> TestWithSampleAudioAsync(byte[] audioData)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var content = new MultipartFormDataContent();
            var audioContent = new ByteArrayContent(audioData);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/flac");
            content.Add(audioContent, "file", "test_audio.flac");
            content.Add(new StringContent(_config.Model), "model");
            content.Add(new StringContent("json"), "response_format");

            // Даем 5 минут, так как первый запуск скачивает веса (450+ МБ)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            var response = await _http.PostAsync($"{EndpointUrl}/audio/transcriptions", content, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync();
            sw.Stop();

            if (!response.IsSuccessStatusCode)
                return $"Ошибка {response.StatusCode}: {body}";

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("text").GetString() ?? "(пусто)";
            return $"OK за {sw.ElapsedMilliseconds} мс\nТекст: {text}";
        }
        catch (TaskCanceledException)
        {
            return "Таймаут (5 минут). Проверьте интернет или логи (возможно, модель еще скачивается).";
        }
        catch (Exception ex)
        {
            return $"Ошибка: {ex.Message}";
        }
    }

    public void SaveOriginalSttSettings(string sttBaseUrl, string sttModel)
    {
        _previousSttBaseUrl = sttBaseUrl;
        _previousSttModel = sttModel;
    }

    public (string baseUrl, string model) GetOriginalSttSettings()
    {
        return (_previousSttBaseUrl, _previousSttModel);
    }

    private async Task RunDockerCommandAsync(string command, string args, CancellationToken ct, Action<string>? onProgress = null)
    {
        var psi = new ProcessStartInfo("docker", $"{command} {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("Не удалось запустить Docker");

        var outputTask = ReadStreamAsync(proc.StandardOutput, ct, onProgress);
        var errorTask = ReadStreamAsync(proc.StandardError, ct, onProgress);
        await Task.WhenAll(outputTask, errorTask);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 && !ct.IsCancellationRequested)
        {
            var error = proc.StandardError.ReadToEnd();
            AppendLog($"Docker {command} failed (exit {proc.ExitCode}): {error}");
            throw new InvalidOperationException($"Docker {command} завершился с ошибкой (код {proc.ExitCode})");
        }
    }

    private async Task ReadStreamAsync(StreamReader reader, CancellationToken ct, Action<string>? onProgress)
    {
        try
        {
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line != null)
                {
                    AppendLog(line);
                    onProgress?.Invoke(line);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task StopExistingContainerAsync()
    {
        try
        {
            await RunDockerCommandAsync("stop", _config.ContainerName, CancellationToken.None);
        }
        catch { }
        try
        {
            await RunDockerCommandAsync("rm", _config.ContainerName, CancellationToken.None);
        }
        catch { }
    }

    private async Task StatsPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Status == LocalSttStatus.Running)
        {
            try
            {
                var psi = new ProcessStartInfo("docker", $"stats --no-stream --format \"{{{{.CPUPerc}}}}\\t{{{{.MemUsage}}}}\" {_config.ContainerName}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        var parts = output.Trim().Split('\t');
                        if (parts.Length >= 2)
                        {
                            CurrentCpu = parts[0].Trim();
                            CurrentRam = parts[1].Trim();
                        }
                    }
                }
                await HealthCheckAsync();
                var uptime = await GetContainerUptimeAsync();
                CurrentUptime = uptime;
                StatsUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch { }
            await Task.Delay(3000, ct);
        }
    }

    private async Task<string> GetContainerUptimeAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"inspect --format='{{{{.State.StartedAt}}}}' {_config.ContainerName}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "-";
            var output = (await proc.StandardOutput.ReadToEndAsync()).Trim().Trim('\'');
            if (DateTime.TryParse(output, out var started))
            {
                var elapsed = DateTime.UtcNow - started.ToUniversalTime();
                if (elapsed.TotalHours >= 1)
                    return $"{(int)elapsed.TotalHours}ч {elapsed.Minutes}м";
                return $"{(int)elapsed.TotalMinutes}м {elapsed.Seconds}с";
            }
            return "-";
        }
        catch { return "-"; }
    }

    private void SetStatus(LocalSttStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void AppendLog(string line)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        BuildLog += $"[{timestamp}] {line}\n";
        LogReceived?.Invoke(this, $"[{timestamp}] {line}");
        _logger.Debug($"LocalSTT: {line}", "LocalStt");
    }

    public void Dispose()
    {
        _buildCts?.Cancel();
        _buildCts?.Dispose();
        _http.Dispose();
    }
}



