using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace GifServer
{
    public class Program
    {
        const int PORT = 5050;
        const int MAX_WORKERS = 5;
        const int MAX_QUEUE_SIZE = 100;

        static FileCache cache = new FileCache(maxFiles: 50);
        static string rootFolder;
        static CancellationTokenSource cts = new CancellationTokenSource();

        static readonly Queue<HttpListenerContext> requestQueue = new();
        static readonly Lock queueLock = new();
        // SemaphoreSlim broji dostupne stavke - workers cekaju bez blokiranja niti
        static readonly SemaphoreSlim queueSignal = new(0, MAX_QUEUE_SIZE);

        static async Task Main(string[] args)
        {
            rootFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            if (!Directory.Exists(rootFolder))
                Directory.CreateDirectory(rootFolder);

            Console.WriteLine("=== GIF Server ===");
            Console.WriteLine($"Adresa: http://localhost:{PORT}/");
            Console.WriteLine($"Folder: {rootFolder}");
            Console.WriteLine($"Worker taskovi: {MAX_WORKERS}");
            Console.WriteLine("Pritisnite Ctrl+C za zaustavljanje\n");

            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{PORT}/");
            listener.Start();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                listener.Stop();
                Console.WriteLine("\nZaustavljanje...");
            };

            Task[] workers = new Task[MAX_WORKERS];
            for (int i = 0; i < MAX_WORKERS; i++)
            {
                int id = i + 1;
                workers[i] = Task.Run(() => WorkerAsync(id));
            }

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();

                    bool enqueued = false;
                    lock (queueLock)
                    {
                        if (requestQueue.Count < MAX_QUEUE_SIZE)
                        {
                            requestQueue.Enqueue(context);
                            enqueued = true;
                        }
                    }

                    if (enqueued)
                    {
                        queueSignal.Release();
                        Log($"[Listener] Primljen zahtev: {context.Request.Url.AbsolutePath}");
                    }
                    else
                    {
                        SendError(context, 503, "Server preopterecen");
                        Log("[Listener] Red pun - zahtev odbijen");
                    }
                }
                catch
                {
                    break;
                }
            }

            await Task.WhenAll(workers);
            Console.WriteLine("Server zaustavljen.");
        }

        static async Task WorkerAsync(int workerId)
        {
            Log($"[Worker-{workerId}] Pokrenut");

            while (true)
            {
                try
                {
                    await queueSignal.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                HttpListenerContext context;
                lock (queueLock)
                {
                    if (requestQueue.Count == 0) continue;
                    context = requestQueue.Dequeue();
                }

                await HandleRequestAsync(context, workerId);
            }

            Log($"[Worker-{workerId}] Zavrsen");
        }

        static async Task HandleRequestAsync(HttpListenerContext context, int workerId)
        {
            try
            {
                string fileName = Path.GetFileName(context.Request.Url.AbsolutePath);

                if (string.IsNullOrEmpty(fileName))
                {
                    SendError(context, 400, "Navedite naziv fajla u URL-u");
                    return;
                }

                Log($"[Worker-{workerId}] Obrada: {fileName}");

                await cache.GetAsync(fileName, () => LoadFromDiskAsync(fileName))
                    .ContinueWith(cacheTask =>
                    {
                        if (cacheTask.IsFaulted)
                        {
                            Log($"[Worker-{workerId}] GRESKA u kesu: {cacheTask.Exception.InnerException?.Message}");
                            SendError(context, 500, "Interna greska servera");
                            return;
                        }

                        byte[] fileData = cacheTask.Result;

                        if (fileData == null)
                        {
                            SendError(context, 404, $"Fajl '{fileName}' nije pronadjen");
                            return;
                        }

                        string contentType = GetContentType(fileName);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = contentType;
                        context.Response.ContentLength64 = fileData.Length;
                        context.Response.OutputStream.Write(fileData, 0, fileData.Length);
                        context.Response.OutputStream.Close();

                        Log($"[Worker-{workerId}] Poslat: {fileName} ({fileData.Length / 1024} KB)");
                    });
            }
            catch (Exception ex)
            {
                Log($"[Worker-{workerId}] GRESKA: {ex.Message}");
                try { SendError(context, 500, "Interna greska servera"); } catch { }
            }
        }

        static Task<byte[]> LoadFromDiskAsync(string fileName)
        {
            string path = FindFile(rootFolder, fileName);
            if (path == null)
                return Task.FromResult<byte[]>(null);

            Log($"[Disk] Ucitavanje: {path}");
            var sw = Stopwatch.StartNew();

            return File.ReadAllBytesAsync(path)
                .ContinueWith(readTask =>
                {
                    sw.Stop();
                    Log($"[Disk] Ucitano: {fileName} ({readTask.Result.Length / 1024} KB) za {sw.Elapsed.TotalMilliseconds:F2}ms");
                    return readTask.Result;
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        static string FindFile(string folder, string fileName)
        {
            foreach (string file in Directory.GetFiles(folder))
            {
                if (Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }

            foreach (string subFolder in Directory.GetDirectories(folder))
            {
                string found = FindFile(subFolder, fileName);
                if (found != null) return found;
            }

            return null;
        }

        static string GetContentType(string fileName) =>
            Path.GetExtension(fileName).ToLower() switch
            {
                ".gif" => "image/gif",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

        static void SendError(HttpListenerContext context, int code, string message)
        {
            try
            {
                string html = $"<html><body style='font-family:sans-serif;text-align:center;padding:50px'>" +
                              $"<h1>{code}</h1><p>{message}</p></body></html>";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(html);

                context.Response.StatusCode = code;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = data.Length;
                context.Response.OutputStream.Write(data, 0, data.Length);
                context.Response.OutputStream.Close();
            }
            catch { }
        }

        static object logLock = new object();
        public static void Log(string message)
        {
            lock (logLock)
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                int tid = Thread.CurrentThread.ManagedThreadId;
                Console.WriteLine($"[{time}] [T-{tid}] {message}");
            }
        }
    }
}
