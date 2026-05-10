# GifServer
Konzolna aplikacija u C# koja servira GIF fajlove preko HTTP-a.
Pokretanje
bashdotnet run
Server se pokreće na adresi: http://localhost:5050/
Korišćenje
Stavite GIF fajlove u wwwroot/ folder (ili bilo koji podfolder) i otvorite u browseru:
http://localhost:5050/naziv_fajla.gif
Primer: http://localhost:5050/panda.gif
Ako fajl ne postoji, prikazuje se poruka 404.
Zaustavljanje
Pritisnuti Ctrl+C u terminalu.
Struktura projekta
GifServer/
├── Program.cs        - Glavni fajl: listener, worker niti, obrada zahteva
├── FileCache.cs      - Thread-safe LRU keš sa cache stampede zaštitom
├── GifServer.csproj  - Konfiguracija projekta
└── wwwroot/          - Folder gde se nalaze GIF fajlovi
Arhitektura

Listener nit prima HTTP zahteve i stavlja ih u red
5 worker niti uzima zahteve iz reda i obrađuje ih paralelno
Keš drži poslednje učitane fajlove u memoriji (max 50 fajlova, LRU strategija)

Sinhronizacija
MehanizamGde se koristiSvrhalockRed zahteva, keš, loggerZaštita deljenih strukturaMonitor.WaitWorker niti, kešBlokirajuće čekanjeMonitor.PulseStavljanje u redBuđenje jedne worker nitiMonitor.PulseAllZavršetak učitavanja, gašenjeBuđenje svih čekajućih niti
Cache Stampede zaštita
Ako više niti istovremeno traži isti fajl koji nije u kešu:

Prva nit počinje učitavanje sa diska
Ostale niti čekaju na Monitor.Wait
Kada prva završi, poziva Monitor.PulseAll i sve dobijaju rezultat iz keša

Tako se fajl učitava samo jednom, bez obzira koliko niti ga istovremeno traži.
LRU strategija keša
Keš može da drži maksimalno 50 fajlova. Kada se popuni, izbacuje se fajl kojem se najduže nije pristupalo (Least Recently Used).
Zahtevi

.NET SDK 8.0 ili noviji
