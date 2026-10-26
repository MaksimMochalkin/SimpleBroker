# RUNBOOK этапа 3: история сделок, индексы, keyset и пул соединений

Пошаговая инструкция для Windows и PowerShell. Команды выполняйте из корня репозитория; результаты сохраняйте в `docs/journal/data/` с префиксом `03-`. Не запускайте генерацию десятков миллионов строк, пока не проверили свободное место, память Docker и резервную копию.

Этап добавляет индексы по сторонам сделки и заменяет `OFFSET`-пагинацию на курсор `cursor`. Первый запрос: `GET /accounts/{id}/trades?size=50`. Следующий: тот же адрес с `&cursor=<NextCursor>` из ответа. `page=1` временно принимается для совместимости; `page>1` возвращает `400`.

**Окна PowerShell:** №1 — API (не закрывать), №2 — команды и замеры, №3 — наблюдение за Docker/логами (по необходимости).

---

## A. Проверка ветки и инструментов

**A1. Что проверяем:** работа ведётся в ветке этапа 3, а изменения плана или пользователя случайно не теряются.

```powershell
git branch --show-current
git status --short
```

Ожидаем: ветка `stage/03-trades-explain`. Перед продолжением сохраните собственные незакоммиченные изменения; не переключайте ветку и не очищайте дерево принудительно.

**A2. Что проверяем:** доступны Docker, .NET SDK 10, EF CLI и k6.

```powershell
dotnet --version
dotnet ef --version
docker version
k6 version
```

Ожидаем: .NET 10, EF CLI отвечает, Docker показывает Client и Server, k6 выводит версию. Для интеграционных тестов Docker Desktop должен быть запущен.

**A3. Что проверяем:** каталоги для логов созданы.

```powershell
New-Item -ItemType Directory -Force docs\journal\data | Out-Null
```

---

## B. Postgres, резервная копия и исходная схема

**B1. Что проверяем:** локальный стек запущен и Postgres готов принимать соединения.

```powershell
docker compose up -d
docker compose ps
docker compose exec postgres pg_isready -U broker -d broker
```

Ожидаем: контейнер Postgres healthy и `accepting connections`.

**B2. Что проверяем:** перед построением индексов есть резервная копия базы. Файл может быть большим.

```powershell
docker compose exec -T postgres pg_dump -U broker -d broker -Fc > docs\journal\data\03-before-indexes.dump
Get-Item docs\journal\data\03-before-indexes.dump | Select-Object FullName,Length
```

Ожидаем: файл дампа имеет ненулевой размер. Для восстановления в отдельную пустую базу используйте `pg_restore`; не удаляйте рабочий volume ради отката.

**B3. Что проверяем:** подключено расширение для анализа запросов.

```powershell
docker compose exec postgres psql -U broker -d broker -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements"
docker compose exec postgres psql -U broker -d broker -c "SELECT extname FROM pg_extension WHERE extname = 'pg_stat_statements'"
```

Ожидаем: одна строка `pg_stat_statements`. В compose расширение загружается через `shared_preload_libraries`.

**B4. Что проверяем:** схема этапов 1–2 есть в базе, а Trades пока остановлен на миграции `Initial`. Это позволяет снять baseline на таблице без новых индексов.

```powershell
$env:ConnectionStrings__Broker = "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker"
dotnet ef database update Initial -p src\SimpleBroker.Trades -s src\SimpleBroker.Host -c TradesDbContext
```

Ожидаем: схема `trades` и таблица `trades.trades` созданы; запрос `\di trades.*` пока не показывает `ix_trades_*`. Если база уже применяла миграцию `Stage03TradesIndexes`, исходный план без индексов можно получить только на отдельной свежей базе/восстановленном snapshot.

---

## C. Данные для воспроизводимых измерений

**Важно:** DataGen очищает таблицы перед загрузкой. Не запускайте его на базе с данными, которые нужно сохранить. 10M — уже долгий прогон; для 50M/100M заранее проверьте свободное место и ресурсы Docker. Рекомендуемый размер Postgres — не менее 4 CPU и 8 GB RAM для больших опытов; запишите фактические лимиты.

**C1. Что проверяем:** генератор понимает новые явные размеры и начинает с 10M строк, не меняя структуру аккаунтов/инструментов этапа L.

```powershell
dotnet run -c Release --project tools\DataGen -- --scale L --accounts 100000 --instruments 200 --orders 10000000 --trades 10000000 --seed 42 --connection "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker;Command Timeout=0" --out loadtests\data
```

Ожидаем: фазы `truncate`, `instruments`, `accounts`, `balances`, `orders`, `trades`, `analyze` завершились без исключения; строка `done in ...s` выведена. Команда создаёт 10M сделок и перезаписывает `loadtests\data\accounts.json`.

Для 50M / 100M сделок задайте соответственно `--trades 50000000` / `--trades 100000000`. Параметр `--orders` можно оставить равным `10000000`: идентификаторы заявок в этой синтетической таблице не имеют внешнего ключа на `orders.orders`. Меняйте размер только одного фактора за прогон и фиксируйте команду целиком в журнале.

**C2. Что проверяем:** в таблице ожидаемое количество строк и актуальная статистика планировщика.

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT count(*) AS trades, pg_size_pretty(pg_total_relation_size('trades.trades')) AS total_size FROM trades.trades"
docker compose exec postgres psql -U broker -d broker -c "SELECT count(*) AS active_accounts FROM (SELECT buy_account_id AS account_id FROM trades.trades UNION SELECT sell_account_id FROM trades.trades) a"
```

Ожидаем: для этого прогона `trades = 10000000`; сохраните фактический размер таблицы и число аккаунтов в `docs/journal/stage-03-trades-explain-report-ru.md` или рабочем журнале этапа.

**C3. Что проверяем:** распределение истории по активному аккаунту достаточно велико для замеров. В генераторе 20% аккаунтов получают примерно 80% участия в сделках.

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT account_id, count(*) AS trade_sides FROM (SELECT buy_account_id AS account_id FROM trades.trades UNION ALL SELECT sell_account_id FROM trades.trades) s GROUP BY account_id ORDER BY trade_sides DESC LIMIT 10"
```

Ожидаем: в результатах видны горячие аккаунты с большой историей. Выберите один и запишите его ID для всех сравнительных прогонов.

---

## D. Baseline до индексов

**D1. Что проверяем:** план старого пути с `OR`, сортировкой и `OFFSET` зафиксирован до применения новых индексов.

```powershell
$accountId = 1
docker compose exec postgres psql -U broker -d broker -c "EXPLAIN (ANALYZE, BUFFERS) SELECT id, instrument_id, buy_order_id, sell_order_id, buy_account_id, sell_account_id, price, quantity, executed_at FROM trades.trades WHERE buy_account_id = $accountId OR sell_account_id = $accountId ORDER BY executed_at DESC, id DESC OFFSET 2450 LIMIT 50" | Tee-Object docs\journal\data\03-explain-before.txt
```

Ожидаем: план и фактические `Execution Time`, `Buffers`, `Rows Removed by Filter` записаны. `Seq Scan`/дорогая сортировка на большой таблице — ожидаемая исходная проблема. Замените `1` на горячий аккаунт из C3.

**D2. Что проверяем:** самые затратные SQL за время baseline собраны из `pg_stat_statements`.

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT calls, round(mean_exec_time::numeric, 2) AS mean_ms, rows, left(query, 500) AS query FROM pg_stat_statements WHERE query ILIKE '%trades%' ORDER BY mean_exec_time DESC LIMIT 10" | Tee-Object docs\journal\data\03-pgstat-before.txt
```

Ожидаем: до 10 строк либо пустой результат, если API ещё не прогонялся. Для полезной выборки сначала выполните несколько baseline-запросов старой версии приложения.

---

## E. Миграции, тесты и API

**E1. Что проверяем:** зависимости восстановлены, решение и тесты собираются.

```powershell
dotnet restore
dotnet build SimpleBroker.slnx -v q --nologo
dotnet test tests\SimpleBroker.IntegrationTests --filter "FullyQualifiedName~TradesTests" --nologo -v q
```

Ожидаем: сборка без ошибок и тесты `TradesTests` зелёные. Интеграционные тесты поднимают отдельный Postgres через Testcontainers, поэтому нужен работающий Docker.

**E2. Что проверяем:** новые индексы созданы безопасным для конкурентных чтений способом (`CREATE INDEX CONCURRENTLY`).

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ef.ps1 update -Modules Trades
docker compose exec postgres psql -U broker -d broker -c "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'trades' ORDER BY indexname"
```

Ожидаем: присутствуют `ix_trades_buy_account_executed_at_id` и `ix_trades_sell_account_executed_at_id`. Concurrent index build нельзя выполнять внутри транзакции; при отмене/ошибке сначала проверьте `pg_index.indisvalid`. Если остался недействительный индекс, удалите только его командой `DROP INDEX CONCURRENTLY trades.<имя>` и повторите миграцию.

**E3. Что проверяем:** Postgres обновил статистику после построения индексов.

```powershell
docker compose exec postgres psql -U broker -d broker -c "ANALYZE trades.trades"
docker compose exec postgres psql -U broker -d broker -c "SELECT indexrelname, idx_scan, idx_tup_read FROM pg_stat_user_indexes WHERE schemaname = 'trades' ORDER BY indexrelname"
```

Ожидаем: `ANALYZE` завершился; сохраните исходные значения счётчиков перед нагрузкой.

**E4. Что проверяем:** приложение стартует с актуальным connection string. Ключ `ConnectionStrings__Broker` имеет приоритет над appsettings.

```powershell
$env:ConnectionStrings__Broker = "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker;Maximum Pool Size=100;Timeout=15;Command Timeout=30;Minimum Pool Size=0"
$env:ASPNETCORE_URLS = "http://localhost:5000"
dotnet run -c Release --project src\SimpleBroker.Host
```

Ожидаем: Host запущен без ошибки подключения. Оставьте окно №1 открытым. В другом окне:

```powershell
$accountId = 1
Invoke-RestMethod "http://localhost:5000/accounts/$accountId/trades?size=3" | ConvertTo-Json -Depth 5
```

Ожидаем: JSON содержит `size`, `items`, `nextCursor`. Если `nextCursor` не `null`, повторите запрос с `&cursor=<значение>`; страницы должны продолжаться без пересечений. Параметр `page=2` намеренно отвечает `400`.

---

## F. План выполнения после индексов

**F1. Что проверяем:** PostgreSQL выбирает два индексируемых прохода с объединением, а не общий `OR`.

```powershell
$accountId = 1
docker compose exec postgres psql -U broker -d broker -c "EXPLAIN (ANALYZE, BUFFERS) SELECT * FROM ((SELECT id, instrument_id, buy_order_id, sell_order_id, buy_account_id, sell_account_id, price, quantity, executed_at FROM trades.trades WHERE buy_account_id = $accountId) UNION ALL (SELECT id, instrument_id, buy_order_id, sell_order_id, buy_account_id, sell_account_id, price, quantity, executed_at FROM trades.trades WHERE sell_account_id = $accountId AND buy_account_id <> $accountId)) AS t ORDER BY executed_at DESC, id DESC LIMIT 51" | Tee-Object docs\journal\data\03-explain-after-first-page.txt
```

Ожидаем: в плане встречаются сканирования новых индексов либо bitmap index scans. Планировщик вправе выбрать последовательное чтение для низкой селективности; анализируйте `actual rows`, `Buffers` и стоимость, а не только имя узла. Эта SQL-команда соответствует первой странице. Для cursor-страницы добавьте в обе ветви условие `(executed_at, id) < (TIMESTAMPTZ '2026-01-01 00:00:00+00', 123456)`, заменив timestamp и ID значениями cursor.

**F2. Что проверяем:** запросы истории действительно используют индексы под нагрузкой.

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT indexrelname, idx_scan, idx_tup_read FROM pg_stat_user_indexes WHERE schemaname = 'trades' ORDER BY indexrelname"
docker compose exec postgres psql -U broker -d broker -c "SELECT calls, round(mean_exec_time::numeric, 2) AS mean_ms, rows, left(query, 500) AS query FROM pg_stat_statements WHERE query ILIKE '%trades%' ORDER BY mean_exec_time DESC LIMIT 10"
```

Ожидаем: после k6 растут `idx_scan` и число вызовов запроса; сравните показатели с D2. Не сбрасывайте `pg_stat_statements` между сопоставляемыми прогонами без записи этого факта.

---

## G. Keyset и нагрузочные прогоны

**G1. Что проверяем:** создаём cursor для условной страницы 20 на одном и том же горячем аккаунте.

```powershell
$accountId = 1
$cursor = $null
for ($i = 1; $i -lt 20; $i++) {
    $url = "http://localhost:5000/accounts/$accountId/trades?size=50"
    if ($cursor) { $url += "&cursor=$cursor" }
    $page = Invoke-RestMethod $url
    $cursor = $page.nextCursor
    if (-not $cursor) { throw "История закончилась раньше страницы 20" }
}
Set-Content docs\journal\data\03-cursor-page20.txt $cursor
```

Ожидаем: cursor сохранён и не пуст. Это cursor перед двадцатой страницей. Для page 5 или 50 замените верхнюю границу цикла соответственно на 5 или 50.

**G2. Что проверяем:** первая страница измерена при постоянной нагрузке 200 RPS.

```powershell
$env:BASE_URL = "http://localhost:5000"
$env:ACCOUNT_ID = "$accountId"
$env:PAGE_LABEL = "1"
Remove-Item Env:\CURSOR -ErrorAction SilentlyContinue
k6 run loadtests\trades-read.js --summary-export docs\journal\data\03-k6-page1.json | Tee-Object docs\journal\data\03-k6-page1.txt
```

Ожидаем: `http_req_failed < 1%`; сохраните RPS, p50, p95, p99 и число запросов. Если k6 не достигает заданной интенсивности, проверьте доступный CPU и `dropped_iterations`.

**G3. Что проверяем:** поздняя keyset-страница при тех же нагрузке и аккаунте не зависит от глубины линейно.

```powershell
$env:CURSOR = (Get-Content docs\journal\data\03-cursor-page20.txt -Raw).Trim()
$env:PAGE_LABEL = "20"
k6 run loadtests\trades-read.js --summary-export docs\journal\data\03-k6-page20.json | Tee-Object docs\journal\data\03-k6-page20.txt
```

Ожидаем: те же пороги ошибки и профиль RPS; сравните p95 с G2. Повторите для страниц 5 и 50, получив соответствующие курсоры через G1.

**G4. Что проверяем:** предел пула и базы измерен ступенями 300, 600 и 1000 RPS. Каждый профиль запускайте 3 минуты; между прогонами снимайте метрики.

```powershell
$env:PAGE_LABEL = "pool"
$env:CURSOR = ""
$env:RATE = "300"
k6 run loadtests\trades-read.js --summary-export docs\journal\data\03-pool-300.json | Tee-Object docs\journal\data\03-pool-300.txt
$env:RATE = "600"
k6 run loadtests\trades-read.js --summary-export docs\journal\data\03-pool-600.json | Tee-Object docs\journal\data\03-pool-600.txt
$env:RATE = "1000"
k6 run loadtests\trades-read.js --summary-export docs\journal\data\03-pool-1000.json | Tee-Object docs\journal\data\03-pool-1000.txt
```

Ожидаем: сначала растёт фактическая нагрузка; при насыщении фиксируйте `dropped_iterations`, ошибки/таймауты, p95/p99 и лимит соединений. Не повышайте `Maximum Pool Size` вслепую: суммарные соединения приложений не должны превышать `max_connections` Postgres с резервом для обслуживания. Npgsql meter уже подключён в Host; метрики пула доступны в Prometheus, если включён соответствующий экспорт.

**G5. Что проверяем:** текущие активные соединения сопоставлены с лимитами и результатами k6.

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT state, count(*) FROM pg_stat_activity WHERE datname = 'broker' GROUP BY state ORDER BY state"
docker compose exec postgres psql -U broker -d broker -c "SHOW max_connections"
```

Ожидаем: активные соединения не упираются в лимит, ошибок `TimeoutException` / `Too many connections` нет. Запишите фактический `Maximum Pool Size` и лимиты Postgres.

---

## H. EF Core и Dapper: сравнение без преждевременной замены

На этом этапе активный endpoint реализован EF Core с проекцией `AsNoTracking`, `UNION ALL`-эквивалентом и keyset-фильтром. Dapper пока не выбран как production-путь: сначала сравните обе реализации при одинаковом SQL, данных, индексе и профиле k6. Не сравнивайте разные планы или разные аккаунты.

Для сравнения сохраните для каждой реализации: команду/SQL, RPS, p50/p95/p99, CPU процесса, `pg_stat_statements` calls/mean time, `idx_scan` и долю ошибок. Если Dapper-вариант не даёт устойчивого измеримого выигрыша, оставьте EF Core. При доказанном выигрыше добавляйте отдельный контролируемый вариант и тесты контракта, затем повторите все проверки E–G.

---

## I. Завершение и журнал

**I1. Что проверяем:** весь набор тестов этапа и смежных модулей проходит.

```powershell
dotnet test SimpleBroker.slnx --nologo -v q --filter "Category!=Demo"
```

Ожидаем: ноль упавших тестов. Для диагностики запустите отдельно `dotnet test tests\SimpleBroker.IntegrationTests --filter "FullyQualifiedName~TradesTests" --nologo -v normal`.

**I2. Что фиксируем:** до/после, объём таблицы, hardware/лимиты, полную команду DataGen, SQL-планы, параметры пула, результаты L2/L5 и причины выбора EF/Dapper. Сырые выводы оставляйте в `docs/journal/data/`; отчёт этапа — в `docs/journal/`.

**I3. Что проверяем:** изменения воспроизводимы и состояние базы не забыто.

```powershell
git status --short
docker compose ps
```

Ожидаем: изменения кода/документации понятны; Postgres запущен, если нужен для следующей сессии. Перед переключением ветки сохраните результаты замеров и резервную копию.
