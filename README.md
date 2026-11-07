# ChainLoot

## Wersja rozproszona — uruchomienie bez internetu

Projekt zawiera lokalny system trzech niezależnych węzłów. Każdy węzeł działa
jako osobny proces i ma własną bazę PostgreSQL oraz własny trwały dziennik.
Węzły wybierają lidera, replikują operacje i zatwierdzają je po uzyskaniu
większości. Po awarii węzeł automatycznie pobiera brakujący stan.

Węzły i ich bazy są podłączone do wewnętrznej sieci Dockera. Strona i lokalne
bramki są dostępne tylko na `127.0.0.1`. Program nie korzysta z publicznego
blockchaina, zewnętrznego RPC ani usług kontowych. Operacje są podpisywane
lokalnie w przeglądarce; backend otrzymuje podpis i klucz publiczny, a nie
klucz prywatny. Lokalna bramka udostępnia węzły przeglądarce; nie jest używana
do komunikacji między samymi węzłami.

### Wymagane wcześniej na komputerze

- Docker Desktop oraz lokalnie dostępne obrazy `postgres:16-alpine`,
  `university-chainloot-backend:latest` i `university-chainloot-frontend:latest`.
- Windowsowy zestaw .NET SDK 9 lub nowszy i przywrócone lokalnie zależności
  projektu `backend` (plik `obj/project.assets.json` oraz cache NuGet).
- Ubuntu/WSL, Node.js 24 oraz lokalny katalog `frontend/node_modules`.

Skrypt nie instaluje zależności ani nie pobiera obrazów. Jeżeli czegoś brakuje,
zatrzyma się z komunikatem; nie próbuje łączyć się z internetem.

### Start

Uruchom Docker Desktop. W PowerShellu, w katalogu repozytorium, wykonaj:

```powershell
./scripts/start-cluster-offline.ps1
```

Skrypt buduje backend z `--no-restore`, buduje frontend z npm w trybie offline,
tworzy obrazy z lokalnie dostępnych obrazów bazowych i uruchamia usługi z
`--pull never`. Otwórz **http://127.0.0.1:5175/**.

1. Dodaj lokalnego użytkownika, np. Alice. Klucz prywatny zostaje zapisany jako
   nieeksportowalny klucz przeglądarki w IndexedDB.
2. Do demonstracji niezależnych użytkowników przygotuj Alice, Boba i Charliego
   w osobnych profilach przeglądarki; przekaż pozostałym tylko ich adresy.
3. Wybierz węzeł, wpisz ID i adres odbiorcy, a następnie wybierz operację.
4. Stan i historię możesz odczytać z każdego węzła. Przycisk ponownego wysłania
   ostatniej operacji pozwala sprawdzić ochronę przed replay.

Wyczyszczenie danych profilu przeglądarki usuwa jego klucze. Nie używaj tych
kont ani testowych danych do prawdziwych aktywów.

### Testy T1–T14

W Windows PowerShell, z katalogu repozytorium, uruchom test bezpośrednio przez
Node.js 24 lub nowszy:

```powershell
node --test frontend/tests/cluster.test.mjs
```

Test tworzy osobny projekt Docker z trzema osobnymi bazami, wykonuje T1–T14 i
usuwa wyłącznie własne kontenery oraz testowe wolumeny. Sprawdza podpisy,
ponowne wysłanie tej samej operacji, historię, awarię węzła, dwa konfliktujące
transfery i partycję komunikacji. Partycja jest symulowana przez zablokowanie
wysyłania i odbierania komunikatów między wskazanymi węzłami. Test T14 używa
nowego aktywa jako stanu początkowego po zakończeniu T13.

Aktualny klaster można zatrzymać bez usuwania danych:

```powershell
docker compose -p chainloot-cluster -f docker-compose.cluster.yml down
```

Nie używaj opcji `--volumes`, jeśli dane klastra mają pozostać. Do rozpoczęcia
prezentacji z czystym stanem można przygotować osobny projekt Compose z nową
nazwą i wolumenami. Wynik testu automatycznego nie zastępuje demonstracji i
oceny prowadzącego.

### Interfejsy lokalne

- `http://127.0.0.1:5101/cluster/status` — węzeł A.
- `http://127.0.0.1:5102/cluster/status` — węzeł B.
- `http://127.0.0.1:5103/cluster/status` — węzeł C.
- `/cluster/assets/{id}` — stan odczytywany z wybranego węzła.
- `/cluster/history/{id}` — chronologiczna historia zatwierdzonych operacji.
- `/cluster/operations` — przesłanie podpisanej operacji.

Algorytm wyboru lidera i replikacji jest implementacją demonstracyjną na potrzeby
projektu, a nie rozwiązaniem przetestowanym do zastosowań produkcyjnych.
Endpointy `/cluster/test/partition` są włączone tylko do lokalnych testów.

## Wersja z Anvil

W repozytorium pozostaje również wcześniejszy wariant z kontraktem ERC-721,
frontendem Vue oraz backendem .NET indeksującym zdarzenia w PostgreSQL i
publikującym aktualizacje przez MQTT i SignalR. Wymaga lokalnego Anvil i jest
odrębny od systemu trzech węzłów. Testy jego kontraktu można uruchomić w WSL:

```bash
cd contracts
forge test
cd ../frontend
npm run test:grade3
npm run test:grade35
npm run test:grade4
```

Te testy uruchamiają osobny Anvil na porcie lokalnym i nie modyfikują stanu
użytkownika. Wersja rozproszona opisana powyżej służy do testów T1–T14 ścieżki A.
