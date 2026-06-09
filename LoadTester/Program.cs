using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LoadTester
{
    class Program
    {
        const string BASE_URL = "http://localhost:5050/";
        static readonly string[] FILES = ["duck.gif", "andromeda.gif", "star.gif"];

        static int totalRequests = 0;
        static int successCount = 0;
        static int errorCount = 0;
        static int rejectedCount = 0;
        static readonly List<double> responseTimes = new();
        static readonly object statsLock = new();

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== GIF Server Load Tester ===");
            Console.WriteLine($"Cilj: {BASE_URL}");
            Console.WriteLine();
            Console.WriteLine("  [1] Normalan test  (definisi broj zahteva)");
            Console.WriteLine("  [2] Flood mode     (rampuje dok ne pukne, Ctrl+C za stop)");
            Console.WriteLine();
            Console.Write("Izbor [1]: ");
            string? choice = Console.ReadLine()?.Trim();

            if (choice == "2")
                await RunFloodMode();
            else
                await RunNormalMode();
        }

        // ─── NORMALAN TEST ───────────────────────────────────────────────────────

        static async Task RunNormalMode()
        {
            Console.WriteLine();
            int concurrent = AskInt("Broj istovremenih zahteva (preporuka: 10-50): ", 20);
            int total = AskInt("Ukupan broj zahteva: ", 200);

            Console.WriteLine();
            Console.WriteLine($"Pokretanje: {total} zahteva, {concurrent} istovremeno...");
            Console.WriteLine("────────────────────────────────────────");

            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var sw = Stopwatch.StartNew();

            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(500, cts.Token).ContinueWith(_ => { });
                    PrintLiveLine(total, sw);
                }
            });

            using SemaphoreSlim sem = new SemaphoreSlim(concurrent, concurrent);
            var tasks = new List<Task>();

            for (int i = 0; i < total; i++)
            {
                string file = FILES[i % FILES.Length];
                await sem.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try { await SendRequest(http, file); }
                    finally { sem.Release(); }
                }));
            }

            await Task.WhenAll(tasks);
            sw.Stop();
            cts.Cancel();
            await Task.Delay(150);

            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────");
            PrintFinalStats(sw.Elapsed);
        }

        // ─── FLOOD MODE ──────────────────────────────────────────────────────────

        static async Task RunFloodMode()
        {
            Console.WriteLine();
            int startConcurrent = AskInt("Pocetni concurrent: ", 10);
            int rampStep = AskInt("Povecaj concurrent svakih 3s za: ", 10);
            int maxConcurrent = AskInt("Maksimalni concurrent (0 = bez granice): ", 0);

            Console.WriteLine();
            Console.WriteLine("Flood mode - Ctrl+C za stop");
            Console.WriteLine("────────────────────────────────────────");

            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var cts = new CancellationTokenSource();
            int currentConcurrent = startConcurrent;

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var sw = Stopwatch.StartNew();
            int fileIndex = 0;

            // Ramp task - povecava concurrent svake 3 sekunde
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(3000, cts.Token).ContinueWith(_ => { });
                    if (maxConcurrent == 0 || currentConcurrent < maxConcurrent)
                    {
                        currentConcurrent += rampStep;
                        if (maxConcurrent > 0 && currentConcurrent > maxConcurrent)
                            currentConcurrent = maxConcurrent;
                    }
                }
            });

            // Live stats task
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(500, cts.Token).ContinueWith(_ => { });
                    int done = totalRequests;
                    int ok = successCount;
                    int rej = rejectedCount;
                    int err = errorCount;
                    double elapsed = sw.Elapsed.TotalSeconds;
                    double rps = elapsed > 0 ? done / elapsed : 0;
                    int pctRej = done > 0 ? (int)(100.0 * rej / done) : 0;
                    Console.Write($"\r  Concurrent: {currentConcurrent,4}  |  Poslato: {done,6}  OK: {ok,6}  503: {rej,6} ({pctRej,3}%)  Err: {err,4}  RPS: {rps,7:F1}   ");
                }
            });

            // Worker pool - drzi uvek `currentConcurrent` aktivnih zahteva
            using SemaphoreSlim sem = new SemaphoreSlim(startConcurrent, int.MaxValue);
            int activeSem = startConcurrent;

            // Pratimo promenu concurrent i prilagodjavamo semafor
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(200, cts.Token).ContinueWith(_ => { });
                    int target = currentConcurrent;
                    int diff = target - activeSem;
                    if (diff > 0)
                    {
                        sem.Release(diff);
                        activeSem = target;
                    }
                }
            });

            var activeTasks = new List<Task>();
            object listLock = new();

            while (!cts.IsCancellationRequested)
            {
                await sem.WaitAsync(cts.Token).ContinueWith(_ => { });
                if (cts.IsCancellationRequested) break;

                string file = FILES[Interlocked.Increment(ref fileIndex) % FILES.Length];
                var t = Task.Run(async () =>
                {
                    try { await SendRequest(http, file); }
                    finally { sem.Release(); }
                });

                lock (listLock)
                {
                    activeTasks.Add(t);
                    if (activeTasks.Count > 500) activeTasks.RemoveAll(x => x.IsCompleted);
                }
            }

            sw.Stop();
            await Task.Delay(300);

            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────");
            PrintFinalStats(sw.Elapsed);
        }

        // ─── SHARED ──────────────────────────────────────────────────────────────

        static async Task SendRequest(HttpClient http, string file)
        {
            var reqSw = Stopwatch.StartNew();
            try
            {
                var response = await http.GetAsync(BASE_URL + file);
                reqSw.Stop();
                double ms = reqSw.Elapsed.TotalMilliseconds;

                Interlocked.Increment(ref totalRequests);

                if ((int)response.StatusCode == 503)
                {
                    Interlocked.Increment(ref rejectedCount);
                }
                else if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref successCount);
                    lock (statsLock) responseTimes.Add(ms);
                }
                else
                {
                    Interlocked.Increment(ref errorCount);
                }
            }
            catch
            {
                reqSw.Stop();
                Interlocked.Increment(ref totalRequests);
                Interlocked.Increment(ref errorCount);
            }
        }

        static void PrintLiveLine(int total, Stopwatch sw)
        {
            int done = totalRequests;
            int ok = successCount;
            int rej = rejectedCount;
            int err = errorCount;
            double elapsed = sw.Elapsed.TotalSeconds;
            double rps = elapsed > 0 ? done / elapsed : 0;
            Console.Write($"\r  Poslato: {done}/{total}  OK: {ok}  503: {rej}  Greska: {err}  RPS: {rps:F1}   ");
        }

        static void PrintFinalStats(TimeSpan elapsed)
        {
            int ok = successCount;
            int rej = rejectedCount;
            int err = errorCount;
            int total = ok + rej + err;

            Console.WriteLine($"  Ukupno zahteva:    {total}");
            Console.WriteLine($"  Uspesno (200):     {ok}  ({Pct(ok, total)})");
            Console.WriteLine($"  Odbijeno (503):    {rej}  ({Pct(rej, total)})");
            Console.WriteLine($"  Greske/timeout:    {err}  ({Pct(err, total)})");
            Console.WriteLine();
            Console.WriteLine($"  Ukupno vreme:      {elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"  Propusnost:        {total / elapsed.TotalSeconds:F1} req/s");

            lock (statsLock)
            {
                if (responseTimes.Count > 0)
                {
                    responseTimes.Sort();
                    double avg = responseTimes.Average();
                    double min = responseTimes[0];
                    double max = responseTimes[^1];
                    double p50 = responseTimes[(int)(responseTimes.Count * 0.50)];
                    double p95 = responseTimes[(int)(responseTimes.Count * 0.95)];
                    double p99 = responseTimes[Math.Min((int)(responseTimes.Count * 0.99), responseTimes.Count - 1)];

                    Console.WriteLine();
                    Console.WriteLine("  Vremena odgovora (samo 200 OK):");
                    Console.WriteLine($"    Min:   {min:F1} ms");
                    Console.WriteLine($"    Avg:   {avg:F1} ms");
                    Console.WriteLine($"    P50:   {p50:F1} ms");
                    Console.WriteLine($"    P95:   {p95:F1} ms");
                    Console.WriteLine($"    P99:   {p99:F1} ms");
                    Console.WriteLine($"    Max:   {max:F1} ms");
                }
            }

            Console.WriteLine();
            if (rej > 0)
                Console.WriteLine($"  >> Server odbio {rej} zahteva ({Pct(rej, total)}) - red pun (MAX_QUEUE_SIZE=100)");
            if (err > 0)
                Console.WriteLine($"  >> {err} timeout/greska - server verovatno preopterecen");
            if (ok == total)
                Console.WriteLine("  >> Server obradio sve zahteve bez greske!");

            Console.WriteLine();
            Console.Write("Pritisni Enter za izlaz...");
            Console.ReadLine();
        }

        static string Pct(int n, int total) => total == 0 ? "0%" : $"{100.0 * n / total:F1}%";

        static int AskInt(string prompt, int defaultVal)
        {
            Console.Write($"{prompt}[{defaultVal}]: ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input) || !int.TryParse(input, out int val))
                return defaultVal;
            return Math.Max(0, val);
        }
    }
}
