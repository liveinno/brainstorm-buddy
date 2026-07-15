using System.Diagnostics;
using Xunit;

namespace BrainstormBuddy.Tests;

public sealed class LlmFactAttribute : FactAttribute
{
    public LlmFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BSB_RUN_LLM_TESTS")))
            Skip = "Set BSB_RUN_LLM_TESTS=1 to run LLM-dependent tests";
    }
}

public static class TestRateLimiter
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly Queue<DateTime> _requestTimestamps = new();
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private const int MinIntervalSeconds = 30;
    private const int MaxRequestsPerDay = 10;

    public static async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            while (_requestTimestamps.Count > 0 && (now - _requestTimestamps.Peek()).TotalHours >= 24)
                _requestTimestamps.Dequeue();

            if (_requestTimestamps.Count >= MaxRequestsPerDay)
            {
                var oldest = _requestTimestamps.Peek();
                var waitUntil = oldest.AddHours(24);
                var delay = waitUntil - now;
                if (delay > TimeSpan.Zero)
                {
                    Debug.WriteLine($"[TestRateLimiter] Daily limit ({MaxRequestsPerDay}) reached. Waiting {delay.TotalMinutes:F1} min...");
                    await Task.Delay(delay, ct);
                }
                _requestTimestamps.Clear();
            }

            var elapsed = (now - _lastRequestUtc).TotalSeconds;
            if (elapsed < MinIntervalSeconds)
            {
                var waitMs = (int)((MinIntervalSeconds - elapsed) * 1000);
                Debug.WriteLine($"[TestRateLimiter] Rate limit: waiting {waitMs}ms (last request {elapsed:F1}s ago)");
                await Task.Delay(waitMs, ct);
            }

            _lastRequestUtc = DateTime.UtcNow;
            _requestTimestamps.Enqueue(_lastRequestUtc);
        }
        finally
        {
            _gate.Release();
        }
    }
}
