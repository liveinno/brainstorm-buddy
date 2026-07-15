using System.Net.Sockets;

namespace BrainstormBuddy.Services;

public class ErrorHandlingService
{
    private readonly LoggingService _logger;
    private int _consecutiveNetworkErrors = 0;
    private DateTime _lastNetworkErrorTime = DateTime.MinValue;
    private const int MaxConsecutiveErrors = 3;
    private static readonly TimeSpan NetworkCooldown = TimeSpan.FromSeconds(30);

    public ErrorHandlingService(LoggingService logger)
    {
        _logger = logger;
    }

    public event EventHandler<ErrorEventArgs>? Error;

    public bool Handle(Exception ex, string context)
    {
        var classification = Classify(ex);
        _logger.Error($"[{context}] {classification}", ex);

        var args = new ErrorEventArgs(classification, ex, context);
        Error?.Invoke(this, args);

        switch (classification)
        {
            case ErrorKind.NetworkTimeout:
            case ErrorKind.NetworkUnreachable:
                if (ShouldThrottleNetwork())
                {
                    _logger.Warn("Network errors throttled, skipping notification spam");
                    return false;
                }
                return true;
            case ErrorKind.AuthError:
                ResetNetworkErrors();
                return true;
            default:
                ResetNetworkErrors();
                return true;
        }
    }

    private ErrorKind Classify(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx when httpEx.InnerException is SocketException => ErrorKind.NetworkUnreachable,
            TaskCanceledException => ErrorKind.NetworkTimeout,
            UnauthorizedAccessException => ErrorKind.AuthError,
            InvalidOperationException ioe when ioe.Message.Contains("401") => ErrorKind.AuthError,
            _ => ErrorKind.Unknown
        };
    }

    private bool ShouldThrottleNetwork()
    {
        if (_consecutiveNetworkErrors >= MaxConsecutiveErrors &&
            DateTime.Now - _lastNetworkErrorTime < NetworkCooldown)
        {
            return true;
        }

        if (DateTime.Now - _lastNetworkErrorTime >= NetworkCooldown)
        {
            _consecutiveNetworkErrors = 0;
        }

        _consecutiveNetworkErrors++;
        _lastNetworkErrorTime = DateTime.Now;
        return false;
    }

    private void ResetNetworkErrors()
    {
        _consecutiveNetworkErrors = 0;
        _lastNetworkErrorTime = DateTime.MinValue;
    }

    public void Reset()
    {
        ResetNetworkErrors();
    }
}

public enum ErrorKind
{
    Unknown,
    NetworkTimeout,
    NetworkUnreachable,
    AuthError,
    AudioDeviceMissing,
    AudioDeviceBusy
}

public class ErrorEventArgs : EventArgs
{
    public ErrorKind Kind { get; }
    public Exception Exception { get; }
    public string Context { get; }

    public ErrorEventArgs(ErrorKind kind, Exception ex, string context)
    {
        Kind = kind;
        Exception = ex;
        Context = context;
    }
}
