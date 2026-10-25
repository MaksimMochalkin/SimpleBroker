# RUNBOOK: запуск Simple Broker на новой машине (Windows, PowerShell)

Выполняйте шаги по порядку, команды копируйте целиком. Каждый шаг содержит проверку («Ожидаем»): не идите дальше, пока она не выполнена.
Все команды из **корня репозитория**, если не сказано иное. Результаты прогонов сохраняйте в `docs/journal/data/`.

Порты, которые должны быть свободны: **5433** (Postgres), **5000** (API), 3000 (Grafana), 9090 (Prometheus), 16686 (Jaeger), 4317/4318/8889 (Collector).

---

## Часть A. Установка (один раз)

**A1.** Включите виртуализацию в BIOS, если Docker Desktop её требует (обычно уже включена).

**A2.** Установите инструменты (PowerShell от обычного пользователя):

```powershell
winget install Git.Git
winget install Microsoft.DotNet.SDK.10
winget install Docker.DockerDesktop
winget install k6 --source winget
```

**A3.** Закройте и откройте PowerShell заново (иначе PATH не обновится). Запустите Docker Desktop и дождитесь статуса Running.
В Docker Desktop: Settings -> Resources -> Memory не менее 6 GB (Postgres получает 4 GB, остальное нужно Collector, Jaeger, Grafana).

**A4.** Установите глобальные .NET-инструменты:

```powershell
dotnet tool install --global dotnet-ef
dotnet tool install --global dotnet-counters
```

**A5.** Проверка версий:

```powershell
git --version; dotnet --version; docker --version; k6 version; dotnet ef --version
```

Ожидаем: у `dotnet --version` старший номер 10 (минимум 10.0.100), все команды отвечают без ошибок.

---

## Часть B. Репозиторий

**B1.** Клонируйте **вне OneDrive** (OneDrive блокирует файлы в `bin/obj` во время сборки и искажает замеры), например в `C:\src`:

```powershell
New-Item -ItemType Directory -Force C:\src | Out-Null; cd C:\src
git clone <URL-репозитория> simple-broker; cd simple-broker
git checkout stage/01-skeleton
```

**B2.** Политику выполнения PowerShell менять не нужно: везде ниже скрипты запускаются через `-ExecutionPolicy Bypass`.

**B3.** Проверка занятости портов:

```powershell
5433,5000,3000,9090,16686,4317 | % { $p=$_; if (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) { "BUSY: $p" } }
```

Ожидаем: пустой вывод. Если `BUSY: 5433`, освободите порт или измените его в `docker-compose.yml`, `appsettings.json`, трёх `*DbContext.cs` и `tools/DataGen/Program.cs`. Локальный Postgres на 5432 не мешает.

---

## Часть C. Инфраструктура и база

**C1.** Поднимите контейнеры:

```powershell
docker compose up -d
```

**C2.** Дождитесь, пока Postgres станет healthy (до ~30 с):

```powershell
docker compose ps
```

Ожидаем: у `postgres` статус `healthy`, остальные `running`. Если не стартует после ошибки пароля или старого volume: `docker compose down -v; docker compose up -d`.

**C3.** Проверка логина:

```powershell
docker compose exec postgres psql -U broker -d broker -c "select 1"
```

Ожидаем: таблица с `1`. Ошибка пароля означает, что порт 5433 занят чужим Postgres (см. B3) или volume создан со старым паролем (`docker compose down -v`).

**C4.** Соберите решение:

```powershell
dotnet restore
dotnet build SimpleBroker.slnx -v q --nologo
```

Ожидаем: `Build succeeded`, ноль ошибок.

**C5.** Примените миграции (файлы миграций уже в репозитории, `ef.ps1 add` **не нужен**):

```powershell
$env:ConnectionStrings__Broker = "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker"
powershell -ExecutionPolicy Bypass -File .\tools\ef.ps1 update
```

Ожидаем: три раза `Applying migration '..._Initial'. Done.` Строки `Failed executing DbCommand ... __EFMigrationsHistory` при первом применении нормальны.

**C6.** Проверка схем:

```powershell
docker compose exec postgres psql -U broker -d broker -c "\dt accounts.*" -c "\dt orders.*" -c "\dt trades.*"
```

Ожидаем: таблицы accounts/balances/positions, instruments/orders, trades.

**C7.** Проверьте расширение для анализа запросов:

```powershell
docker compose exec postgres psql -U broker -d broker -c "select count(*) from pg_stat_statements"
```

Если ошибка `relation does not exist`: `docker compose exec postgres psql -U broker -d broker -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements"`.

---

## Часть D. Тесты

**D1.** Docker должен работать (тесты сами поднимают отдельный Postgres на случайном порту):

```powershell
dotnet test SimpleBroker.slnx --nologo -v q
```

Ожидаем: `Passed! ... Passed: 9` (ArchitectureTests) и `Passed! ... Passed: 18` (IntegrationTests). Первый запуск дольше из-за скачивания образа Postgres.

---

## Часть E. Подготовка машины к замерам (важно для ноутбука)

**E1.** Подключите питание, включите план электропитания «Высокая производительность» и отключите сон:

```powershell
powercfg /setactive SCHEME_MIN
powercfg /change standby-timeout-ac 0
```

**E2.** Закройте браузеры, IDE, мессенджеры, синхронизацию OneDrive/антивирусные сканирования. Фоновая нагрузка искажает цифры.

**E3.** Запишите окружение (вставьте в journal как «Environment machine B»):

```powershell
Get-CimInstance Win32_Processor | Select Name,NumberOfCores,NumberOfLogicalProcessors
[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,1)
Get-PhysicalDisk | Select FriendlyName,MediaType
dotnet --version; docker --version; k6 version
docker compose exec postgres psql -U broker -d broker -c "select version()"
```

Docker-лимиты Postgres: 2 CPU, 4 GB (из `docker-compose.yml`). Если у ноутбука меньше 4 логических ядер, поменяйте `cpus:` в compose и **запишите** это в journal: результаты сравнимы только при одинаковых лимитах.

---

## Часть F. Данные и прогоны

Серия на одном масштабе: загрузка данных, запуск API, прогрев, три прогона. Начните с **S** (быстрее), потом **M**.
Между масштабами данные заменяются (DataGen делает `TRUNCATE`).

### F1. Загрузка данных (повторите для каждого масштаба: `S`, затем `M`)

```powershell
dotnet run -c Release --project tools/DataGen -- --scale S --seed 42 --out loadtests/data
```

Ожидаем: строки по фазам и `done in ...s` (на машине A: S 2.1 с, M 11.8 с), создан `loadtests/data/accounts.json`.

Проверка размера БД (на машине A: S 77 MB, M 693 MB):

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT pg_size_pretty(pg_database_size('broker'));"
```

### F2. Запуск API (отдельное окно PowerShell №1, не закрывать)

```powershell
cd C:\src\simple-broker
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"; dotnet run -c Release --project src/SimpleBroker.Host --launch-profile http
```

Ожидаем: `Now listening on: http://localhost:5000`. Проверка из окна №2:

```powershell
$ids = Get-Content loadtests/data/accounts.json | ConvertFrom-Json
curl.exe -s -o NUL -w "%{http_code}`n" "http://localhost:5000/accounts/$($ids[0])/balance"
```

Ожидаем: `200`. Если запустили API раньше загрузки данных, перезапустите его после F1.

### F3. Прогрев и ANALYZE (окно №2, перед первым прогоном серии)

```powershell
1..300 | % { curl.exe -s -o NUL "http://localhost:5000/accounts/$($ids[(Get-Random -Max $ids.Count)])/balance" }
1..60  | % { curl.exe -s -o NUL -w "%{http_code}`n" "http://localhost:5000/accounts/$($ids[(Get-Random -Max $ids.Count)])/trades?page=1&size=50" }
1..300 | % { curl.exe -s -o NUL -w "%{http_code}`n" "http://localhost:5000/accounts/$($ids[(Get-Random -Max $ids.Count)])/balance"}
docker compose exec postgres psql -U broker -d broker -c "ANALYZE;"
```

Подождите 2 минуты (JIT, пул соединений, кэш Postgres).

### F4. Три прогона L1 (окно №2)

Прогон занимает ~6.5 минуты. Между прогонами пауза 60 секунд. Запускайте так, чтобы вывод сохранялся в файл (`<масштаб>` = `S` или `M`, `<N>` = 1, 2, 3):

```powershell
New-Item -ItemType Directory -Force docs/journal/data | Out-Null
k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js 2>&1 | Tee-Object docs/journal/data/01-machineB-<масштаб>-run<N>.txt
```

Ожидаем в конце: блоки `THRESHOLDS` и `TOTAL RESULTS`, в `http_req_duration` теперь есть `p(99)`.
Для M порог `p(95)<1000` на машине A провален (это ожидаемо, это baseline), `ERRO ... thresholds ... crossed` не является ошибкой запуска.

Во время прогона (необязательно, но нужно для M, чтобы подтвердить причину):

```powershell
# окно №3: CPU контейнеров, снимок каждые ~10 с на середине прогона (минуты 3-5)
docker stats --no-stream
# окно №4: счётчики .NET
dotnet-counters monitor --process-id (Get-Process SimpleBroker.Host).Id System.Runtime Microsoft.AspNetCore.Hosting
```

### F5. Что делал запрос дольше всех (только после прогона на M)

Перед **первым** прогоном M один раз сбросьте статистику:

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT pg_stat_statements_reset();"
```

После трёх прогонов:

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT calls, round(total_exec_time) AS total_ms, round(mean_exec_time::numeric,1) AS mean_ms, left(query,90) AS q FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 5;"
```

### F6. Что записать (шаблон)

| Run | Scale | Actual RPS (`http_reqs`/с) | p50 (`med`) | p95 | p99 | Errors (`http_req_failed`) | `dropped_iterations` |
|---|---|---|---|---|---|---|---|

Берите **медиану трёх** прогонов. Сравнивайте только цифры одной машины: машину B и машину A в одну таблицу не смешивайте.

---

## Часть G. Просмотр данных и наблюдаемость

**G1.** Интерактивный psql:

```powershell
docker compose exec postgres psql -U broker -d broker
```

Внутри: `\dn` (схемы), `\dt trades.*` (таблицы), `\d trades.trades` (структура), `SELECT * FROM accounts.accounts LIMIT 10;`, `SELECT count(*) FROM trades.trades;`, `\q` (выход).

**G2.** Одна команда без входа:

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT * FROM orders.orders ORDER BY id DESC LIMIT 5;"
```

**G3.** DBeaver или другой GUI: host `localhost`, port `5433`, db `broker`, user `broker`, password `broker`.

**G4.** Веб-интерфейсы: Grafana http://localhost:3000 (дашборд «Simple Broker: overview»), Jaeger http://localhost:16686, Prometheus http://localhost:9090.

---

## Часть H. Завершение

**H1.** Остановить API: Ctrl+C в окне №1.

**H2.** Остановить стек, данные сохранить: `docker compose down`. Остановить и удалить данные: `docker compose down -v`.

**H3.** Закоммитить результаты машины B (из `docs/journal/data/`) и отправить ветку:

```powershell
git add docs/journal; git commit -m "Stage 1: baseline on machine B"; git push -u origin stage/01-skeleton
```

---

## Частые ошибки и быстрые решения

| Симптом | Причина | Решение |
|---|---|---|
| `password authentication failed for user "broker"` | порт 5433 занят чужим Postgres или старый volume | шаг B3; `docker compose down -v; docker compose up -d` |
| `ef.ps1 cannot be loaded ... running scripts is disabled` | политика выполнения | запускать через `powershell -ExecutionPolicy Bypass -File .\tools\ef.ps1 update` |
| `dotnet ef` не найден | не установлен или не обновлён PATH | шаг A4, перезапустить PowerShell |
| Интеграционные тесты: `Docker is either not running` | Docker Desktop не запущен | запустить Docker Desktop, дождаться Running |
| API отвечает 500 на `/trades` | миграции не применены или данных нет | шаги C5 и F1 |
| k6: `accounts.json not found` | не выполнена загрузка данных | шаг F1 |
| k6: `Insufficient VUs` | система не успевает отвечать (на M это ожидаемо) | не ошибка, фиксируйте `dropped_iterations` |
| k6: `100k unique time series` | URL с id счёта | предупреждение, на цифры не влияет критично; в следующем этапе сгруппировать тегом `name` |
| Build падает, файл занят | репозиторий в OneDrive | клонировать в `C:\src` (шаг B1) |
