using System.Net;
using System.Net.Sockets;
using DocsWalker.Cli.Cli.Kernel;

namespace DocsWalker.Tests;

/// <summary>
/// Поведение <see cref="KernelHttpClient.SendCommandAsync"/> при недоступном ядре
/// (stg-0010 step-03/05): TCP-соединение не устанавливается → клиент возвращает
/// exit-code 1 и пишет JSON <c>{"code":"kernel_unreachable",...}</c> в stderr.
/// Закрывает гэп между unit-тестами <see cref="ClientConfigTests"/> и smoke-тестом
/// (step-06): убеждается, что error-code определяется именно
/// в network-fault ветке <see cref="KernelHttpClient"/>, без запуска реального
/// kernel-процесса.
/// Сериализуется через collection "ConsoleRedirect" — тест перехватывает
/// <see cref="Console.Error"/>, параллельный запуск с другими console-redirect
/// тестами ломает захват.
/// </summary>
[Collection("ConsoleRedirect")]
public class KernelHttpClientTests
{
    [Fact]
    public async Task SendCommandAsync_KernelOffline_Reports_KernelUnreachable()
    {
        var port = FindFreePort();
        var config = new ClientConfig(KernelHost: "127.0.0.1", KernelPort: port, Graph: "main");

        var originalErr = Console.Error;
        var capturedErr = new StringWriter();
        Console.SetError(capturedErr);
        int exitCode;
        try
        {
            exitCode = await KernelHttpClient.SendCommandAsync(
                new[] { "get-overview" }, config);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Equal(1, exitCode);
        var stderrText = capturedErr.ToString();
        Assert.Contains("\"code\":\"kernel_unreachable\"", stderrText);
    }

    /// <summary>
    /// Получает заведомо свободный TCP-порт на loopback'е через <see cref="TcpListener"/>
    /// с port=0 (ОС выбирает свободный) и сразу закрывает listener. Между Stop() и
    /// последующей попыткой клиента подключиться существует теоретическое окно
    /// захвата порта другим процессом, но на dev/CI-машинах вероятность ≈ 0.
    /// </summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
