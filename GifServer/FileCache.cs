using System;
using System.Collections.Generic;
using System.Threading;

namespace GifServer
{
    public class FileCache
    {
        // kes
        private Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();

        // LRU lista
        private LinkedList<string> lruOrder = new LinkedList<string>();

        // Lista fajlova koji se trenutno ucitavaju (cache stampede)
        private HashSet<string> loading = new HashSet<string>();

        // Jedan lock za sve
        private object cacheLock = new object();

        private int maxFiles;

        public FileCache(int maxFiles)
        {
            this.maxFiles = maxFiles;
        }

        public byte[] Get(string fileName, Func<byte[]> loadFunc)
        {
            while (true)
            {
                lock (cacheLock)
                {
                    // 1.scenario
                    if (cache.ContainsKey(fileName))
                    {
                        Program.Log($"[Cache] HIT: {fileName}");
                        // Premesti na kraj LRU liste (najskorije korišćen)
                        lruOrder.Remove(fileName);
                        lruOrder.AddLast(fileName);
                        return cache[fileName];
                    }

                    // 2.scenario
                    if (loading.Contains(fileName))
                    {
                        Program.Log($"[Cache] WAIT: {fileName} - druga nit učitava");
                        Monitor.Wait(cacheLock);
                        // Posle buđenja petlja se ponavlja - probaj opet
                        continue;
                    }

                    // 3.scenario
                    Program.Log($"[Cache] MISS: {fileName} - učitavam");
                    loading.Add(fileName);
                }

                // izvan locka da ne blokiramo druge niti
                byte[] data = null;
                try
                {
                    data = loadFunc();
                }
                catch (Exception ex)
                {
                    Program.Log($"[Cache] Greška: {ex.Message}");
                }

                lock (cacheLock)
                {
                    loading.Remove(fileName);

                    // ako je uspesno ucitano
                    if (data != null)
                    {
                        //izbaci najstarije ako je kes pun
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

                    // Probudi sve niti koje čekaju
                    Monitor.PulseAll(cacheLock);
                }

                return data;
            }
        }
    }
}
