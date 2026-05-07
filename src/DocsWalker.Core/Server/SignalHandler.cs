using System.Runtime.InteropServices;

namespace DocsWalker.Core.Server;

/// <summary>
/// Токен завершения: обёртка над <see cref="CancellationToken"/>, которую
/// <see cref="SignalHandler"/> триггерит при получении сигнала.
/// </summary>
public interface IShutdownToken
{
    CancellationToken Token { get; }
}

/// <summary>
/// Подписывается на сигналы ОС и запрашивает graceful-shutdown через
/// <see cref="IShutdownToken.Token"/>.
/// <para>
/// Первый сигнал (Ctrl+C / SIGINT / SIGTERM / SIGHUP / ProcessExit) →
/// отменяет <see cref="Token"/>. Второй сигнал — force-exit(1) для защиты
/// от зависшего shutdown.
/// </para>
/// <para>
/// Намеренно не интегрирован в <see cref="ServerLifecycle"/> — lifecycle
/// принимает <see cref="CancellationToken"/> снаружи.
/// </para>
/// </summary>
public sealed class SignalHandler : IShutdownToken, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _signalCount;
    private readonly List<IDisposable> _registrations = new();

    public CancellationToken Token => _cts.Token;

    public SignalHandler()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        if (!OperatingSystem.IsWindows())
        {
            _registrations.Add(
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal));
            _registrations.Add(
                PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnPosixSignal));
        }
    }

    private void TriggerShutdown()
    {
        var count = Interlocked.Increment(ref _signalCount);
        if (count == 1)
            _cts.Cancel();
        else
            Environment.Exit(1);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true; // подавляем немедленное завершение — даём graceful-shutdown
        TriggerShutdown();
    }

    private void OnProcessExit(object? sender, EventArgs e) => TriggerShutdown();

    private void OnPosixSignal(PosixSignalContext ctx)
    {
        ctx.Cancel = true; // подавляем дефолтное поведение сигнала
        TriggerShutdown();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        foreach (var r in _registrations)
            r.Dispose();
        _registrations.Clear();
        _cts.Dispose();
    }
}
