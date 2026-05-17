namespace DocsWalker.Cli;

/// <summary>
/// <c>dw health [&lt;kernel-url&gt;]</c>: GET /health у kernel-а.
/// Default URL — <c>http://127.0.0.1:18080</c>. Возвращает тело ответа
/// как-есть в stdout. Exit 0 при 200 OK, 1 при любой другой ситуации.
/// </summary>
internal static class HealthCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 1)
        {
            throw new CliArgumentException("health: ожидается максимум один аргумент <kernel-url>");
        }
        var baseUrl = args.Length == 1 ? args[0] : "http://127.0.0.1:18080";
        baseUrl = baseUrl.TrimEnd('/');
        var url = $"{baseUrl}/health";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            Console.Out.WriteLine(body);
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"health: kernel unreachable at {url}: {ex.Message}");
            return 1;
        }
    }
}
