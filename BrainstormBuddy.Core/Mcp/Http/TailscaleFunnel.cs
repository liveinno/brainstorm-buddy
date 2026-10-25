using System.Diagnostics;
using System.Text.Json;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Mcp.Http;

/// <summary>
/// Обёртка над tailscale.exe для облачного доступа по Tailscale Funnel.
/// Только обнаружение/статус/start/stop процесса — tailscaled делает всё
/// сетевое. Tailscale может отсутствовать на машине: состояние NotInstalled,
/// никаких исключений наружу.
/// </summary>
public static class TailscaleFunnel
{
    public enum State
    {
        /// <summary>tailscale.exe не найден ни в PATH, ни в стандартных местах.</summary>
        NotInstalled,
        /// <summary>Бинарь есть, но tailscaled не отвечает/не залогинен.</summary>
        DaemonUnavailable,
        /// <summary>Демон жив, funnel на этот порт не поднят.</summary>
        Available,
        /// <summary>funnel активен — PublicUrl заполнен.</summary>
        Running,
    }

    public sealed record Info(State State, string? PublicUrl, string? DnsName, string? Detail);

    /// <summary>Пути поиска tailscale.exe помимо PATH.</summary>
    private static readonly string[] WellKnown =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tailscale", "tailscale.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Tailscale", "tailscale.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tailscale", "tailscale.exe"),
    };

    /// <summary>Путь tailscale.exe или null (PATH + стандартные каталоги).</summary>
    public static string? LocateExe()
    {
        foreach (var p in WellKnown)
            if (File.Exists(p)) return p;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var cand = Path.Combine(dir.Trim(), "tailscale.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { /* кривая запись PATH — пропускаем */ }
        }
        return null;
    }

    /// <summary>
    /// Полная проверка: бинарь → `tailscale status --json` → dnsname →
    /// `tailscale funnel status` → есть ли https=443 на наш порт.
    /// </summary>
    public static async Task<Info> DetectAsync(int localPort, CancellationToken ct = default)
    {
        var exe = LocateExe();
        if (exe == null)
            return new Info(State.NotInstalled, null, null,
                "Tailscale не установлен. Установите Tailscale (tailscale.com), войдите в tailnet, затем повторите.");

        var (code, stdout, stderr) = await RunAsync(exe, "status --json", 8000, ct)
            .ConfigureAwait(false);
        if (code != 0)
            return new Info(State.DaemonUnavailable, null, null,
                $"tailscale status: {(stderr.Trim() is { Length: > 0 } s ? s : "демон не отвечает")}");

        string? dnsName = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("Self", out var self) &&
                self.TryGetProperty("DNSName", out var d))
                dnsName = d.GetString()?.TrimEnd('.');
        }
        catch (JsonException) { /* статус распарсить не смогли — dnsname останется null */ }

        if (string.IsNullOrEmpty(dnsName))
            return new Info(State.DaemonUnavailable, null, null,
                "tailscale запущен, но узел не в tailnet (нужен tailscale up / вход в аккаунт)");

        // Есть ли уже funnel на наш порт: читаем serve/funnel status.
        var (_, funnelOut, _) = await RunAsync(exe, "funnel status", 8000, ct).ConfigureAwait(false);
        var publicUrl = FindFunnelUrl(funnelOut, localPort, dnsName);
        if (publicUrl != null)
            return new Info(State.Running, publicUrl, dnsName, null);

        return new Info(State.Available, $"https://{dnsName}", dnsName, null);
    }

    /// <summary>
    /// Поднять funnel: `tailscale funnel --bg --https=443 http://127.0.0.1:{port}`.
    /// При неизвестном флаге/старой версии — `tailscale serve --bg --https=443 ...`.
    /// Возвращает публичный URL (https://{dnsname}) или null+причину.
    /// </summary>
    public static async Task<(string? PublicUrl, string? Error)> StartFunnelAsync(
        int localPort, CancellationToken ct = default)
    {
        var exe = LocateExe();
        if (exe == null) return (null, "Tailscale не установлен");

        var target = $"http://127.0.0.1:{localPort}";
        var (code, stdout, stderr) =
            await RunAsync(exe, $"funnel --bg --https=443 {target}", 15000, ct).ConfigureAwait(false);
        if (code != 0)
        {
            // Старые версии: funnel был поверх serve.
            var alt = await RunAsync(exe, $"serve --bg --https=443 {target}", 15000, ct)
                .ConfigureAwait(false);
            if (alt.Code != 0)
                return (null, FirstLine(alt.Stderr) ?? FirstLine(stderr) ?? $"tailscale exit {alt.Code}");
            stdout = alt.Stdout;
        }

        var info = await DetectAsync(localPort, ct).ConfigureAwait(false);
        if (info.State == State.Running && info.PublicUrl != null)
            return (info.PublicUrl, null);

        // URL иногда печатается прямо в stdout команды — берём оттуда.
        var fromOut = FindFunnelUrl(stdout, localPort, null);
        return fromOut != null ? (fromOut, null) : (info.DnsName != null ? $"https://{info.DnsName}" : null, null);
    }

    /// <summary>Снять funnel с порта (best-effort, обе формы CLI).</summary>
    public static async Task StopFunnelAsync(int localPort, CancellationToken ct = default)
    {
        var exe = LocateExe();
        if (exe == null) return;
        await RunAsync(exe, $"funnel --https=443 off", 10000, ct).ConfigureAwait(false);
        // Для надёжности снимаем и serve-маппинг, если он был создан старым синтаксисом.
        await RunAsync(exe, $"serve --https=443 off", 8000, ct).ConfigureAwait(false);
    }

    /// <summary>https://{dnsname}[/...] из вывода funnel status для нашего порта.</summary>
    internal static string? FindFunnelUrl(string output, int localPort, string? dnsName)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var portMarker = $":{localPort}";
        var portHit = output.Contains(portMarker, StringComparison.Ordinal);
        // funnel status печатает строки вида "https://host.tail123.ts.net (Funnel on)".
        foreach (var line in output.Split('\n'))
        {
            var l = line.Trim();
            var idx = l.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var url = l[idx..].Split(' ', '\t')[0].TrimEnd('/');
            // Если в выводе вообще есть наш порт — URL строки считаем нашим.
            if (portHit || (dnsName != null && url.Contains(dnsName, StringComparison.OrdinalIgnoreCase)))
                return url;
        }
        return null;
    }

    private static string? FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string exe, string args, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* уже умер */ }
                return (-1, "", "timeout");
            }
            return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (-1, "", ex.Message);
        }
    }
}
