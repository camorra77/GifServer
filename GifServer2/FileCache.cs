using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GifServer
{
    public class FileCache(int maxFiles)
    {
        private readonly Dictionary<string, byte[]> cache = [];
        private readonly LinkedList<string> lruOrder = new();
        private readonly Dictionary<string, TaskCompletionSource<byte[]>> loading = [];

        // SemaphoreSlim(1,1) = async-friendly mutex
        private readonly SemaphoreSlim cacheLock = new(1, 1);

        public async Task<byte[]> GetAsync(string fileName, Func<Task<byte[]>> loadFunc)
        {
            var sw = Stopwatch.StartNew();

            await cacheLock.WaitAsync();

            // Scenario 1: Cache HIT - fajl je vec u memoriji
            if (cache.TryGetValue(fileName, out byte[] cached))
            {
                lruOrder.Remove(fileName);
                lruOrder.AddLast(fileName);
                cacheLock.Release();
                sw.Stop();
                Program.Log($"[Cache] HIT: {fileName} (iz memorije: {sw.Elapsed.TotalMilliseconds:F3}ms)");
                return cached;
            }

            // Scenario 2: Neko vec ucitava ovaj fajl - cekamo bez blokiranja niti
            if (loading.TryGetValue(fileName, out TaskCompletionSource<byte[]> existingTcs))
            {
                Program.Log($"[Cache] WAIT: {fileName} - drugi task ucitava");
                cacheLock.Release();
                byte[] result = await existingTcs.Task;
                sw.Stop();
                Program.Log($"[Cache] WAIT zavrseno: {fileName} ({sw.Elapsed.TotalMilliseconds:F2}ms ukupno)");
                return result;
            }

            // Scenario 3: Cache MISS - registruj TCS i preuzmi ucitavanje
            Program.Log($"[Cache] MISS: {fileName} - ucitavam sa diska");
            var tcs = new TaskCompletionSource<byte[]>();
            loading[fileName] = tcs;
            cacheLock.Release();

            // Ucitavanje van locka - disk timing se meri u LoadFromDiskAsync
            byte[] data = null;
            try
            {
                data = await loadFunc();
            }
            catch (Exception ex)
            {
                Program.Log($"[Cache] Greska pri ucitavanju: {ex.Message}");
            }

            await cacheLock.WaitAsync();

            loading.Remove(fileName);

            if (data != null)
            {
                // LRU eviction
                while (cache.Count >= maxFiles)
                {
                    string oldest = lruOrder.First.Value;
                    lruOrder.RemoveFirst();
                    cache.Remove(oldest);
                    Program.Log($"[Cache] EVICT: {oldest} (LRU)");
                }

                cache[fileName] = data;
                lruOrder.AddLast(fileName);
                Program.Log($"[Cache] ADD: {fileName} ({cache.Count}/{maxFiles})");
            }

            cacheLock.Release();

            tcs.SetResult(data);

            return data;
        }
    }
}
