using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Barep.Services.Net;

public enum SpeedPhase { Latency, Download, Upload, Done }

/// <summary>Avance del test: fase, velocidad del momento (Mbps) y fracción completada de la fase.</summary>
public readonly record struct SpeedProgress(SpeedPhase Phase, double Mbps, double Fraction, double PingMs);

/// <summary>
/// Test de velocidad contra la red de Cloudflare (la misma que usa
/// speed.cloudflare.com): latencia y variación con peticiones vacías, y bajada
/// y subida con varias conexiones a la vez durante unos segundos, descartando
/// el arranque para medir la velocidad sostenida.
/// </summary>
public static class SpeedTest
{
    private const string Base = "https://speed.cloudflare.com";
    private static readonly TimeSpan PhaseLength = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1.5);

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<SpeedResult> RunAsync(IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        using var client = NewClient();

        var (ping, jitter) = await LatencyAsync(client, progress, ct);
        var down = await DownloadAsync(client, ping, progress, ct);
        var up = await UploadAsync(client, ping, progress, ct);
        progress?.Report(new SpeedProgress(SpeedPhase.Done, 0, 1, ping));

        return new SpeedResult
        {
            At = DateTime.Now,
            PingMs = Math.Round(ping, 1),
            JitterMs = Math.Round(jitter, 1),
            DownMbps = Math.Round(down, 1),
            UpMbps = Math.Round(up, 1)
        };
    }

    // ────────────────────────── Latencia ──────────────────────────

    private static async Task<(double Ping, double Jitter)> LatencyAsync(HttpClient client,
        IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        // La primera abre la conexión (DNS + TLS): no cuenta.
        using (await client.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct)) { }

        var samples = new List<double>();
        const int count = 16;
        for (var i = 0; i < count; i++)
        {
            var clock = Stopwatch.StartNew();
            using (await client.GetAsync($"{Base}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct)) { }
            samples.Add(clock.Elapsed.TotalMilliseconds);
            progress?.Report(new SpeedProgress(SpeedPhase.Latency, 0, (i + 1) / (double)count, samples.Min()));
        }

        // Ping: la mediana (aguanta mejor un pico suelto). Variación: diferencia media entre consecutivas.
        var sorted = samples.OrderBy(s => s).ToList();
        var median = sorted[sorted.Count / 2];
        var jitter = samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return (median, jitter);
    }

    // ────────────────────────── Bajada ──────────────────────────

    private static async Task<double> DownloadAsync(HttpClient client, double ping, IProgress<SpeedProgress>? progress,
        CancellationToken ct)
    {
        long total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();

        async Task Worker()
        {
            var buffer = new byte[64 * 1024];
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var response = await client.GetAsync($"{Base}/__down?bytes=25000000",
                        HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    await using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, stop.Token)) > 0)
                        Interlocked.Add(ref total, read);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpRequestException) { await Task.Delay(200, CancellationToken.None); }
                catch (IOException) { await Task.Delay(200, CancellationToken.None); }
            }
        }

        var workers = Enumerable.Range(0, 6).Select(_ => Worker()).ToList();
        var result = await Measure(() => Interlocked.Read(ref total), clock, SpeedPhase.Download, ping, progress, stop, ct);
        await Task.WhenAll(workers);
        return result;
    }

    // ────────────────────────── Subida ──────────────────────────

    private static async Task<double> UploadAsync(HttpClient client, double ping, IProgress<SpeedProgress>? progress,
        CancellationToken ct)
    {
        long total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();

        // Datos al azar: si fueran ceros, alguien por el camino podría comprimirlos.
        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        async Task Worker()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var content = new CountingContent(payload, n => Interlocked.Add(ref total, n), stop.Token);
                    using var response = await client.PostAsync($"{Base}/__up", content, stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpRequestException) { await Task.Delay(200, CancellationToken.None); }
                catch (IOException) { await Task.Delay(200, CancellationToken.None); }
            }
        }

        var workers = Enumerable.Range(0, 4).Select(_ => Worker()).ToList();
        var result = await Measure(() => Interlocked.Read(ref total), clock, SpeedPhase.Upload, ping, progress, stop, ct);
        await Task.WhenAll(workers);
        return result;
    }

    /// <summary>
    /// Mide durante la fase y devuelve los Mbps sostenidos: los bytes después
    /// del calentamiento entre el tiempo después del calentamiento.
    /// </summary>
    private static async Task<double> Measure(Func<long> bytes, Stopwatch clock, SpeedPhase phase, double ping,
        IProgress<SpeedProgress>? progress, CancellationTokenSource stop, CancellationToken ct)
    {
        long warmBytes = -1;
        var warmTime = TimeSpan.Zero;
        long lastBytes = 0;
        var lastTime = TimeSpan.Zero;
        var smoothed = 0.0;

        try
        {
            while (clock.Elapsed < PhaseLength)
            {
                await Task.Delay(200, ct);
                var now = clock.Elapsed;
                var b = bytes();

                if (warmBytes < 0 && now >= Warmup)
                {
                    warmBytes = b;
                    warmTime = now;
                }

                var instant = (b - lastBytes) * 8 / Math.Max(0.001, (now - lastTime).TotalSeconds) / 1_000_000;
                smoothed = smoothed == 0 ? instant : smoothed * 0.7 + instant * 0.3;
                lastBytes = b;
                lastTime = now;

                progress?.Report(new SpeedProgress(phase, smoothed, now / PhaseLength, ping));
            }
        }
        finally { stop.Cancel(); }

        var seconds = (clock.Elapsed - warmTime).TotalSeconds;
        var measured = warmBytes < 0 ? bytes() : bytes() - warmBytes;
        return seconds <= 0 ? 0 : measured * 8 / seconds / 1_000_000;
    }

    /// <summary>Cuerpo de la subida que va contando lo que de verdad sale por la red.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly Action<int> _sent;
        private readonly CancellationToken _ct;

        public CountingContent(byte[] data, Action<int> sent, CancellationToken ct)
        {
            _data = data;
            _sent = sent;
            _ct = ct;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            const int chunk = 64 * 1024;
            for (var offset = 0; offset < _data.Length; offset += chunk)
            {
                var size = Math.Min(chunk, _data.Length - offset);
                await stream.WriteAsync(_data.AsMemory(offset, size), _ct);
                _sent(size);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }

    // ────────────────────────── Interpretación ──────────────────────────

    /// <summary>Qué se puede hacer con esa conexión, en palabras de andar por casa.</summary>
    public static string Verdict(SpeedResult r)
    {
        var lines = new List<string>();
        if (r.DownMbps >= 100) lines.Add("Sobra para 4K en varias pantallas a la vez y descargas grandes rápidas.");
        else if (r.DownMbps >= 40) lines.Add("Va bien para 4K en una pantalla y varias personas conectadas.");
        else if (r.DownMbps >= 15) lines.Add("Suficiente para vídeo en HD y videollamadas; el 4K puede ir justo.");
        else if (r.DownMbps >= 5) lines.Add("Da para navegar y vídeo en calidad media. Con varias personas se notará.");
        else lines.Add("Es lenta: el vídeo se cortará y las páginas tardarán en cargar.");

        if (r.UpMbps < 3) lines.Add("La subida es baja: las videollamadas y subir archivos pueden fallar.");
        else if (r.UpMbps >= 20) lines.Add("La subida es buena para videollamadas, directos y copias en la nube.");

        if (r.PingMs > 100) lines.Add("El ping es alto: los juegos en línea irán con retraso.");
        else if (r.PingMs <= 30) lines.Add("El ping es muy bueno para jugar en línea.");

        if (r.JitterMs > 30) lines.Add("La conexión es inestable: las llamadas pueden entrecortarse.");
        return string.Join(" ", lines);
    }
}
