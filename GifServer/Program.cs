using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace GifServer
{
    public class Program
    {
        const int PORT = 5050;
        const int MAX_WORKERS = 5;
        const int MAX_QUEUE_SIZE = 100;

        static Queue<HttpListenerContext> requestQueue = new Queue<HttpListenerContext>();
        static object queueLock = new object();

        static FileCache cache = new FileCache(maxFiles: 50);
        static string rootFolder;
        static bool isRunning = true;

        static void Main(string[] args)
        {
            // folder gde se nalaze gif fajlovi
            rootFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            if (!Directory.Exists(rootFolder))
                Directory.CreateDirectory(rootFolder);

            Console.WriteLine("=== GIF Server ===");
            Console.WriteLine($"Adresa: http://localhost:{PORT}/");
            Console.WriteLine($"Folder: {rootFolder}");
            Console.WriteLine($"Worker niti: {MAX_WORKERS}");
            Console.WriteLine("Pritisnite Ctrl+C za zaustavljanje\n");

            // HTTP listener
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{PORT}/");
            listener.Start();

            // ctrl+C graceful shutdown
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                isRunning = false;
                listener.Stop();
                // probudi sve worker niti da prestanu
                lock (queueLock) { Monitor.PulseAll(queueLock); }
                Console.WriteLine("\nZaustavljanje...");
            };

            // worker niti
            Thread[] workers = new Thread[MAX_WORKERS];
            for (int i = 0; i < MAX_WORKERS; i++)
            {
                int id = i + 1;
                workers[i] = new Thread(() => Worker(id));
                workers[i].Start();
            }

            // listener petlja
            while (isRunning)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    EnqueueRequest(context);
                }
                catch
                {
                    break;
                }
            }

            // workeri se cekaju
            foreach (Thread t in workers)
                t.Join();

            Console.WriteLine("Server zaustavljen.");
        }

        // Producer
        static void EnqueueRequest(HttpListenerContext context)
        {
            lock (queueLock)
            {
                if (requestQueue.Count >= MAX_QUEUE_SIZE)
                {
                    SendError(context, 503, "Server preopterećen");
                    return;
                }

                requestQueue.Enqueue(context);
                Log($"[Listener] Primljen zahtev: {context.Request.Url.AbsolutePath}");

                // probudi jednu worker nit koja čeka
                Monitor.Pulse(queueLock);
            }
        }

        // Consumer
        static void Worker(int workerId)
        {
            Log($"[Worker-{workerId}] Pokrenut");

            while (true)
            {
                HttpListenerContext context;

                lock (queueLock)
                {
                    //dok red nije prazan ili dok server radi
                    while (requestQueue.Count == 0 && isRunning)
                        Monitor.Wait(queueLock);

                    // ako je server ugasen i red prazan
                    if (requestQueue.Count == 0)
                        break;

                    context = requestQueue.Dequeue();
                }

                // obrada zahteva van locka
                HandleRequest(context, workerId);
            }

            Log($"[Worker-{workerId}] Završen");
        }

        // glavna obrada zahteva
        static void HandleRequest(HttpListenerContext context, int workerId)
        {
            try
            {
                // izvuci ime fajla iz URL-a
                string fileName = Path.GetFileName(context.Request.Url.AbsolutePath);

                if (string.IsNullOrEmpty(fileName))
                {
                    SendError(context, 400, "Navedite naziv fajla u URL-u");
                    return;
                }

                Log($"[Worker-{workerId}] Obrada: {fileName}");

                // fajl iz kesa ili sa diska
                byte[] fileData = cache.Get(fileName, () => LoadFromDisk(fileName));

                if (fileData == null)
                {
                    SendError(context, 404, $"Fajl '{fileName}' nije pronađen");
                    return;
                }

                // salje se fajl klijentu
                string contentType = GetContentType(fileName);
                context.Response.StatusCode = 200;
                context.Response.ContentType = contentType;
                context.Response.ContentLength64 = fileData.Length;
                context.Response.OutputStream.Write(fileData, 0, fileData.Length);
                context.Response.OutputStream.Close();

                Log($"[Worker-{workerId}] Poslat: {fileName} ({fileData.Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                Log($"[Worker-{workerId}] GREŠKA: {ex.Message}");
                try { SendError(context, 500, "Interna greška servera"); } catch { }
            }
        }

        // ucitava fajl sa diska rekurzija
        static byte[] LoadFromDisk(string fileName)
        {
            string path = FindFile(rootFolder, fileName);
            if (path == null) return null;

            Log($"[Disk] Učitavanje: {path}");
            return File.ReadAllBytes(path);
        }

        // rekurzivna pretraga
        static string FindFile(string folder, string fileName)
        {
            // provera u trenutnom folderu
            foreach (string file in Directory.GetFiles(folder))
            {
                if (Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }

            // provera u podfolderima
            foreach (string subFolder in Directory.GetDirectories(folder))
            {
                string found = FindFile(subFolder, fileName);
                if (found != null) return found;
            }

            return null;
        }

        // odredjuje tip na osnovu ekstenzije
        static string GetContentType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLower();
            switch (ext)
            {
                case ".gif": return "image/gif";
                case ".png": return "image/png";
                case ".jpg": return "image/jpeg";
                case ".jpeg": return "image/jpeg";
                default: return "application/octet-stream";
            }
        }

        // salje gresku klijentu
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

        // Thread-safe logovanje
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
