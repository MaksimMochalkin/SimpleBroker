# RUNBOOK этапа 2: счета, транзакции, deadlock, outbox (Windows, PowerShell)

Формат как в `docs/RUNBOOK.md`: шаги по порядку, команды копируйте целиком. У каждого шага три части: **Что проверяем** (зачем шаг), команда, **Ожидаем** (с чем сравнивать). Не идите дальше, пока ожидание не выполнено.
Все команды из **корня репозитория**, если не сказано иное. Результаты прогонов сохраняйте в `docs/journal/data/` (имена `02-*.txt`).

**Что строго, что ориентир.** Инварианты (`lost=0`, сумма балансов, коды ответов, `deadlocks=0`) проверяются строго: расхождение = ошибка в коде или в запуске. Цифры скорости (rps, p95) зависят от машины: это ориентир порядка величин, в журнал пишем свои.

Порты: те же, что в этапе 1 (**5433**, **5000**, 3000, 9090, 16686, 4317/4318/8889).

Окна PowerShell: №1 API (не закрывать), №2 команды, №3 наблюдение (по необходимости).

---

## Часть A. Репозиторий и распаковка

**A1. Что проверяем:** этап 1 закрыт, дерево чистое, ветку этапа 2 создаём от `main`.

```powershell
cd C:\src\simple-broker
git status --short
git checkout main; git pull
git tag --list v0.1
git checkout -b stage/02-accounts-transactions
```

Ожидаем: `git status --short` пустой, тег `v0.1` в списке, `Switched to a new branch 'stage/02-accounts-transactions'`. Если тега нет, этап 1 не закрыт: сначала закончите его (S14).

**A2. Что проверяем:** файлы этапа 2 легли поверх репозитория.

```powershell
Expand-Archive -Path "$HOME\Downloads\stage02-changes.zip" -DestinationPath $env:TEMP\s2 -Force
Copy-Item -Path "$env:TEMP\s2\simple-broker\*" -Destination . -Recurse -Force
git status --short
```

Ожидаем: изменённые (` M`) `docker-compose.yml`, `SimpleBroker.slnx`, `appsettings.json`, файлы в `src/SimpleBroker.Accounts*`, `src/SimpleBroker.Orders`, `tests/`, `tools/`, `loadtests/`; новые (`??`) `src/SimpleBroker.Platform/`, `tools/Contention/`, `loadtests/hot-account.js`, `docs/RUNBOOK-stage-02.md`, `tests/SimpleBroker.IntegrationTests/Api.cs` и др. Предупреждения `LF will be replaced by CRLF` безвредны.

---

## Часть B. Сборка и тесты

**B1. Что проверяем:** весь код этапа (написан без компиляции) собирается.

```powershell
dotnet restore
dotnet build SimpleBroker.slnx -v q --nologo
```

Ожидаем: `Build succeeded`, `0 Error(s)`. Если есть ошибки: скопируйте **весь** вывод ошибок ассистенту (строки вида `error CS....`), исправим и пришлём файлы. Предупреждения (`warning`) можно пропустить.

**B2. Что проверяем:** старое поведение не сломано, новое работает. Docker должен быть запущен (тесты поднимают свой Postgres).

```powershell
dotnet test SimpleBroker.slnx --nologo -v q --filter "Category!=Demo"
```

Ожидаем: `Passed! ... Passed: 10` (ArchitectureTests: 9 из этапа 1 и новый `Platform_does_not_reference_modules`) и `Passed! ... Passed: 40` (IntegrationTests: 18 из этапа 1 и 22 новых). Цифры 10 и 40 посчитаны вручную: если все тесты зелёные, а число отличается на 1–2, это не проблема. `Failed` > 0 является проблемой: пришлите имя теста и сообщение.

Что именно проверяют новые тесты:

| Тест | Что доказывает |
|---|---|
| `WithdrawTests.Safe_modes_keep_the_invariant...` (5 режимов) | при 200 параллельных списаниях нет потерянных обновлений и перепродажи; `Atomic`/`ForUpdate` проводят ровно 100 из 100 |
| `TransferTests.Ordered_opposing_transfers_do_not_deadlock` | встречные переводы с упорядоченными блокировками не дают ни одного deadlock, сумма денег неизменна |
| `ReservationTests` | Buy резервирует `price × qty`; нехватка средств = 422 без заявки; резерв и снятие резерва идемпотентны |
| `OutboxTests` | `OrderCreated` и `TransferCompleted` доходят до sink; два публикатора не дублируют сообщения |

**B3. Что проверяем (необязательно, наглядно):** демонстрационные тесты показывают сами проблемы.

```powershell
dotnet test tests/SimpleBroker.IntegrationTests --no-build --filter "Category=Demo" --logger "console;verbosity=detailed" --nologo
```

Ожидаем: тесты зелёные, в выводе строка `Naive: ok=... expected=... actual=... lost=...` с `lost` больше 0 (потерянные обновления), и для `Unordered` строка с кодами `200=..., 409=...` (deadlock). Если `lost=0`, на этой машине гонка не поймалась: не страшно, на шаге F1 мы поднимем параллелизм. Занимает до пары минут (жертва deadlock ждёт 1 секунду).

---

## Часть C. База: миграции

**C1. Что проверяем:** Postgres перезапущен с новыми параметрами (`log_lock_waits`, `deadlock_timeout`); данные в volume сохранены.

```powershell
docker compose up -d
docker compose ps
docker compose exec postgres psql -U broker -d broker -c "SHOW deadlock_timeout" -c "SHOW log_lock_waits"
```

Ожидаем: `postgres` healthy; `deadlock_timeout` = `1s`, `log_lock_waits` = `on`. (Почему это важно: deadlock обнаруживается не сразу, а через `deadlock_timeout`: жертва ждёт 1 секунду, это увидите в p95 на шаге F3.)

**C2. Что проверяем:** EF видит изменения модели (резервы, outbox, колонка заявки, CHECK) и создаёт миграцию. `Trades` не затронут.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ef.ps1 add -MigrationName Stage02 -Modules Accounts,Orders
Select-String -Path src\SimpleBroker.Accounts\Migrations\*Stage02.cs, src\SimpleBroker.Orders\Migrations\*Stage02.cs -Pattern "CreateTable|AddColumn|AddCheckConstraint|DropTable|DropColumn|AlterColumn|name: """
```

Ожидаем: создано два файла `..._Stage02.cs`. В Accounts: `CreateTable ... "reservations"`, `CreateTable ... "outbox"`, `AddCheckConstraint ... "ck_balances_nonneg"`. В Orders: `CreateTable ... "outbox"`, `AddColumn ... "reservation_id"`. Строк `DropTable`, `DropColumn`, `AlterColumn` быть **не должно**: если есть, остановитесь и пришлите вывод.

**C3. Что проверяем:** миграции применяются к существующей базе этапа 1.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\ef.ps1 update
```

Ожидаем: `Applying migration '..._Stage02'. Done.` для Accounts и Orders; для Trades `No migrations were applied. The database is already up to date.`

**C4. Что проверяем:** схема в базе соответствует ожиданиям.

```powershell
docker compose exec postgres psql -U broker -d broker -c "\d accounts.reservations" -c "\d accounts.balances" -c "\d orders.orders" -c "\dt *.outbox"
```

Ожидаем: у `accounts.reservations` колонки `id uuid, account_id, amount, status, created_at`; у `accounts.balances` внизу `Check constraints: "ck_balances_nonneg" CHECK (available >= 0 AND reserved >= 0)`; у `orders.orders` колонка `reservation_id uuid`; две таблицы `outbox` (в схемах accounts и orders).

---

## Часть D. Данные и запуск API

**D1. Что проверяем:** данные S загружаются (DataGen теперь очищает и новые таблицы, поэтому миграция из части C обязательна).

```powershell
dotnet run -c Release --project tools/DataGen -- --scale S --seed 42 --out loadtests/data
```

Ожидаем: как в этапе 1 (фазы и `done in ...s`, около 2 с). Ошибка `relation "accounts.reservations" does not exist` значит, не применён шаг C3.

**D2. Что проверяем:** API стартует с включённым переопределением режима. Флаг `Accounts__AllowModeOverride` разрешает параметр `?mode=` (без него режим берётся из конфигурации: `Atomic`/`Ordered`; в «проде» переключатель выключен).

Окно №1:

```powershell
cd C:\src\simple-broker
$env:Accounts__AllowModeOverride="true"
$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run -c Release --project src/SimpleBroker.Host --launch-profile http
```

Ожидаем: `Now listening on: http://localhost:5000`. Дальше в этом окне будут появляться строки `outbox <id> <Type> {...}`: это работает заглушка-получатель outbox.

**D3.** Окно №2:

```powershell
$ids = Get-Content loadtests/data/accounts.json | ConvertFrom-Json
curl.exe -s -o NUL -w "%{http_code}`n" "http://localhost:5000/accounts/$($ids[0])/balance"
```

Ожидаем: `200`.

---

## Часть E. Ручная проверка функций (smoke)

Все команды в окне №2. Сначала помощники (один раз на окно):

```powershell
$b = "http://localhost:5000"
function Call($m, $u, $j) {
  try { $r = Invoke-WebRequest -UseBasicParsing -Method $m "$b$u" -ContentType "application/json" -Body $j; "$([int]$r.StatusCode) $($r.Content)" }
  catch { "$([int]$_.Exception.Response.StatusCode) $($_.ErrorDetails.Message)" }
}
function NewAcc($dep) {
  $id = (Invoke-RestMethod -Method Post "$b/accounts" -ContentType "application/json" -Body '{"owner":"smoke","currency":"USD"}').id
  if ($dep) { Invoke-RestMethod -Method Post "$b/accounts/$id/deposit" -ContentType "application/json" -Body ('{"amount":' + $dep + '}') | Out-Null }
  $id
}
function Bal($id) { Invoke-RestMethod "$b/accounts/$id/balance" | Format-List }
```

**E1. Списание.** Что проверяем: базовый сценарий и рост `version`.

```powershell
$a = NewAcc 1000
Call POST "/accounts/$a/withdraw" '{"amount":30.5}'
Bal $a
```

Ожидаем: `200 {"accountId":...,"available":969.5,"reserved":0,"version":2}` (версия 2: депозит + списание).

**E2. Ошибки списания.** Что проверяем: правильные коды ответа.

```powershell
Call POST "/accounts/$a/withdraw" '{"amount":5000}'
Call POST "/accounts/999999999/withdraw" '{"amount":1}'
Call POST "/accounts/$a/withdraw" '{"amount":0}'
Call POST "/accounts/$a/withdraw?mode=Bogus" '{"amount":1}'
Bal $a
```

Ожидаем по порядку: `422 {"error":"insufficient funds"}`, `404` (без тела), `400 {"error":"amount must be positive"}`, `400 {"error":"unknown mode"}`. Баланс не изменился (969.5).

**E3. Перевод.** Что проверяем: деньги переходят целиком, обе версии растут.

```powershell
$c = NewAcc 0
Call POST "/transfers" ('{"fromAccountId":' + $a + ',"toAccountId":' + $c + ',"amount":100}')
Bal $a; Bal $c
Call POST "/transfers" ('{"fromAccountId":' + $a + ',"toAccountId":' + $a + ',"amount":1}')
Call POST "/transfers" ('{"fromAccountId":' + $a + ',"toAccountId":' + $c + ',"amount":100000}')
```

Ожидаем: `200 {...}`; у `$a` available 869.5, version 3; у `$c` available 100, version 1; затем `400 {"error":"accounts must differ"}` и `422 {"error":"insufficient funds"}`.

**E4. Резерв при заявке.** Что проверяем: Buy замораживает `price × quantity` (здесь 10 × 5 = 50), Sell ничего не замораживает, плохая заявка баланс не трогает.

```powershell
$inst = (docker compose exec -T postgres psql -U broker -d broker -t -A -c "SELECT min(id) FROM orders.instruments").Trim()
Call POST "/orders" ('{"accountId":' + $a + ',"instrumentId":' + $inst + ',"side":"Buy","type":"Limit","price":10,"quantity":5}')
Bal $a
Call POST "/orders" ('{"accountId":' + $a + ',"instrumentId":' + $inst + ',"side":"Buy","type":"Limit","price":100000,"quantity":10}')
Call POST "/orders" ('{"accountId":' + $a + ',"instrumentId":' + $inst + ',"side":"Buy","type":"Market","quantity":1}')
Call POST "/orders" ('{"accountId":' + $a + ',"instrumentId":' + $inst + ',"side":"Sell","type":"Limit","price":10,"quantity":5}')
Bal $a
```

Ожидаем: `201 {"id":...,"status":"New"}`; после первой заявки available 819.5, reserved 50; затем `422 {"error":"insufficient funds"}`, `400` (Market Buy без `price`: цена нужна как потолок), `201` для Sell. Итоговый баланс всё ещё available 819.5, reserved 50.

**E5. Outbox.** Что проверяем: события из бизнес-транзакции доезжают до получателя (в нашем случае в лог) и помечаются отправленными. Подождите 2 секунды.

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT id, type, published_at IS NOT NULL AS published, round(extract(epoch FROM published_at - created_at)::numeric, 3) AS lag_s FROM accounts.outbox ORDER BY id DESC LIMIT 3" -c "SELECT id, type, published_at IS NOT NULL AS published FROM orders.outbox ORDER BY id DESC LIMIT 3" -c "SELECT id, account_id, amount, status FROM accounts.reservations ORDER BY created_at DESC LIMIT 3"
```

Ожидаем: в `accounts.outbox` строка `TransferCompleted`, `published = t`, `lag_s` меньше ~0.5; в `orders.outbox` две строки `OrderCreated` (Buy и Sell), `published = t`; в `reservations` строка на 50 со статусом `Active`. В окне №1 видны строки `outbox N TransferCompleted {...}` и `outbox N OrderCreated {...}`. Неудачные заявки (422, 400) в outbox попадать не должны.

---

## Часть F. Кейсы конкуренции

Подготовка (окно №2, один раз):

```powershell
New-Item -ItemType Directory -Force docs/journal/data | Out-Null
dotnet build tools/Contention -c Release -v q --nologo
$tool = "tools/Contention/bin/Release/net10.0/Contention.dll"
```

Как читать вывод `Contention`: `rps`, `p50/p95/p99` (мс), `codes:` (сколько каких ответов), для C1 `expected = deposit - ok`, `actual` (баланс из базы), **`lost = actual - expected`**: если больше 0, часть списаний «исчезла» (деньги появились из воздуха); **`oversell`**: сколько списаний прошло сверх депозита.

### F1. C1: потерянное обновление (один счёт, 50 параллельных клиентов)

**Что проверяем:** `Naive` (прочитал, проверил, записал число) ломается под нагрузкой, остальные пять способов защиты сохраняют инвариант. Сравниваем и цену защиты (rps, p95, число 409).

```powershell
foreach ($m in 'naive','atomic','forupdate','optimistic','repeatableread','serializable') {
  1..3 | % { dotnet $tool --case c1 --mode $m --deposit 10000 --ops 5000 --parallel 50 2>&1 | Tee-Object "docs/journal/data/stage-02/02-c1-$m-run$_.txt" }
}
```

Ожидаем (депозит 10 000, 5 000 списаний по 1, значит по смыслу должны пройти все):

| Режим | codes | lost | Что это значит |
|---|---|---|---|
| naive | `200=5000` | **больше 0** (сотни и тысячи) | потерянные обновления: гонка «прочитал-записал». Если `lost=0`, повторите с `--parallel 150` |
| atomic | `200=5000` | 0 | `UPDATE ... WHERE available >= x` в одном запросе: самый быстрый и простой |
| forupdate | `200=5000` | 0 | пессимистичная блокировка строки: корректно, чуть медленнее |
| optimistic | `200` + много `409` | 0 | конфликт версии, 5 повторов, потом сдаёмся (409). Корректно, но часть запросов отвергнута |
| repeatableread | `200` + `409` | 0 | Postgres сам отменяет конфликтную транзакцию (40001), `TxRetry` повторяет |
| serializable | `200` + `409` | 0 | то же, строже; обычно больше повторов |

Строго: `lost=0` у пяти режимов (не naive), в `codes` нет ничего кроме `200`, `409`, `422`. Ориентир: `atomic` быстрее всех по rps, `optimistic`/`serializable` медленнее и дают долю 409.

**Вариант «перепродажа»** (депозит меньше числа запросов: должно пройти ровно 1000):

```powershell
foreach ($m in 'naive','atomic','forupdate','optimistic','repeatableread','serializable') {
  dotnet $tool --case c1 --mode $m --deposit 1000 --ops 5000 --parallel 50 2>&1 | Tee-Object "docs/journal/data/stage-02/02-c1-oversell-$m.txt"
}
```

Ожидаем: `naive`: `200` больше 1000, `oversell` больше 0 (продали то, чего нет). `atomic` и `forupdate`: ровно `200=1000`, `422=4000`, `actual=0`, `oversell=0`. Остальные: `oversell=0`, `lost=0`, но `200` может быть меньше 1000 (часть запросов получила 409, а на балансе остались деньги: это не потеря, а отказ).

Метрики повторов (необязательно; приходят с задержкой до минуты):

```powershell
curl.exe -s http://localhost:8889/metrics | Select-String "simple_broker_db_retries|optimistic_conflicts"
```

### F2. C2: deadlock на встречных переводах

**Что проверяем:** два перевода A→B и B→A, которые блокируют счета в разном порядке, зависают друг на друге: Postgres через 1 с убивает одну транзакцию (deadlock). Лечится порядком блокировок (`Ordered`) или повтором (`UnorderedRetry`).

```powershell
foreach ($m in 'unordered','unorderedretry','ordered') {
  1..3 | % {
    $f = "docs/journal/data/stage-02/02-c2-$m-run$_.txt"
    docker compose exec -T postgres psql -U broker -d broker -c "SELECT pg_stat_reset();" | Out-Null
    Start-Sleep 2
    dotnet $tool --case c2 --mode $m --accounts 2 --ops 2000 --parallel 50 --deposit 100000 2>&1 | Tee-Object $f
    Start-Sleep 3
    docker compose exec -T postgres psql -U broker -d broker -c "SELECT deadlocks, xact_commit, xact_rollback FROM pg_stat_database WHERE datname='broker';" | Tee-Object -Append $f
  }
}
```

`unordered` может идти несколько минут (каждый deadlock ждёт 1 с). Ожидаем:

| Режим | codes | p95 | `deadlocks` в `pg_stat_database` | `sum` |
|---|---|---|---|---|
| unordered | `200` и **`409 {"error":"deadlock"}` > 0** | порядка 1000 мс и выше (ожидание `deadlock_timeout`) | **> 0**, примерно равно числу 409 | `sum = expected` |
| unorderedretry | почти всё `200`, 409 нет или единицы | выше 1000 мс (ждали deadlock, повторили) | **> 0** (deadlock был, просто скрыт повтором) | `sum = expected` |
| ordered | только `200=2000` | десятки мс | **0** | `sum = expected` |

Строго: во всех режимах строка `sum=200000 expected=200000` (деньги не появляются и не пропадают, даже у отменённых транзакций); у `ordered` `deadlocks = 0` и нет 409. Подтверждение в логе Postgres:

```powershell
docker compose logs postgres | Select-String "deadlock detected" | Select-Object -First 3
```

Ожидаем: строки `ERROR:  deadlock detected` (от `unordered` и `unorderedretry`).

### F3. C3: горячий счёт под ростом нагрузки (k6)

**Что проверяем:** как выбранный способ защиты ведёт себя, когда все клиенты бьют в одну строку. Нагрузка растёт 10 → 50 → 200 виртуальных пользователей, ~3 минуты на режим.

```powershell
$ids = Get-Content loadtests/data/accounts.json | ConvertFrom-Json
foreach ($m in 'atomic','forupdate','optimistic','serializable') {
  k6 run -e BASE_URL=http://localhost:5000 -e MODE=$m -e ACCOUNT=$($ids[0]) loadtests/hot-account.js 2>&1 | Tee-Object "docs/journal/data/stage-02/02-c3-$m.txt"
  Start-Sleep 60
}
```

Во время одного из прогонов (минуты 2–3), окно №3:

```powershell
docker compose exec -T postgres psql -U broker -d broker -c "SELECT wait_event_type, wait_event, count(*) FROM pg_stat_activity WHERE datname='broker' GROUP BY 1,2 ORDER BY 3 DESC;"
```

Ожидаем: у `atomic`/`forupdate` много сессий в `Lock | transactionid` или `tuple` (очередь на одну строку): это и есть «горячий счёт», узкое место. Скорость растёт с VU слабо. В итогах k6: `http_reqs` (rps), `http_req_duration` p(95), `conflicts_409`. Ориентир: у `atomic` наибольший rps и `conflicts_409 = 0`; у `optimistic` и `serializable` rps ниже и заметная доля 409.

### F4. L1′: цена корректности (резерв + outbox) относительно этапа 1

**Что проверяем:** насколько замедлил обычный смешанный профиль L1 появившийся резерв при Buy-заявках и outbox. Сравнивать только с числами **той же машины** по этапу 1 (на машине A: S 207.7 rps, p95 10.4 мс).

Свежие данные и перезапуск API (резервы от прошлых шагов «съели» часть балансов):

```powershell
dotnet run -c Release --project tools/DataGen -- --scale S --seed 42 --out loadtests/data
```

Затем в окне №1: Ctrl+C и повторный запуск API (команды из D2). Прогрев как в `RUNBOOK.md` (F3) и 2 минуты ожидания:

```powershell
$ids = Get-Content loadtests/data/accounts.json | ConvertFrom-Json
1..300 | % { curl.exe -s -o NUL "http://localhost:5000/accounts/$($ids[(Get-Random -Max $ids.Count)])/balance" }
1..60  | % { curl.exe -s -o NUL "http://localhost:5000/accounts/$($ids[(Get-Random -Max $ids.Count)])/trades?page=1&size=50" }
docker compose exec postgres psql -U broker -d broker -c "ANALYZE;"
```

Три прогона (~6.5 минуты каждый), между ними пауза 60 с и сброс резервов (вернуть замороженные деньги, чтобы 422 не копились):

```powershell
1..3 | % {
  k6 run -e BASE_URL=http://localhost:5000 loadtests/baseline.js 2>&1 | Tee-Object "docs/journal/data/stage-02/02-l1-S-run$_.txt"
  docker compose exec -T postgres psql -U broker -d broker -c "UPDATE accounts.balances SET available = available + reserved, reserved = 0 WHERE reserved > 0; TRUNCATE accounts.reservations;"
  Start-Sleep 60
}
```

Ожидаем: пороги k6 пройдены (`http_req_failed` меньше 1%: ответы 422 теперь считаются ожидаемыми), в итогах есть счётчик `insufficient_funds` (доля меньше 1–2% от POST). Ожидание по скорости: rps близко к этапу 1 (цель 300 rps — потолок профиля), p95 может вырасти на единицы миллисекунд: каждый Buy теперь делает дополнительную транзакцию в Accounts и запись в outbox. Если p95 вырос в разы, это находка для journal (смотреть `pg_stat_statements`, как в F5 этапа 1).

### F5. Что записать (шаблоны для journal)

C1/C2 (медиана трёх прогонов):

| Кейс | Режим | ok (200) | 409 | lost | oversell | p95 мс | rps | deadlocks | Заметки |
|---|---|---|---|---|---|---|---|---|---|

C3 и L1′:

| Кейс | Режим / версия | RPS (`http_reqs`/с) | p50 | p95 | p99 | `conflicts_409` / `insufficient_funds` | Что насытилось |
|---|---|---|---|---|---|---|---|

Окружение (как «Environment machine» в этапе 1): команды из E3 `RUNBOOK.md`.

---

## Часть G. Просмотр данных

```powershell
# сообщения, которые ещё не отправлены (в покое должно быть 0)
docker compose exec -T postgres psql -U broker -d broker -c "SELECT 'accounts' s, count(*) FROM accounts.outbox WHERE published_at IS NULL UNION ALL SELECT 'orders', count(*) FROM orders.outbox WHERE published_at IS NULL"
# задержка доставки по типам
docker compose exec -T postgres psql -U broker -d broker -c "SELECT type, count(*), round(avg(extract(epoch FROM published_at - created_at))::numeric,3) AS avg_lag_s FROM accounts.outbox WHERE published_at IS NOT NULL GROUP BY type"
# замороженные средства и активные резервы
docker compose exec -T postgres psql -U broker -d broker -c "SELECT status, count(*), sum(amount) FROM accounts.reservations GROUP BY status"
```

Ожидаем: непубликованных 0 (через секунду после остановки нагрузки), средняя задержка доставки единицы или сотни миллисекунд. Метрики в Grafana (http://localhost:3000): добавьте панели из раздела 5.3 плана этапа (вручную, дашборд в код не включён).

---

## Часть H. Завершение

```powershell
# окно №1: Ctrl+C. Данные оставить: docker compose down. Удалить данные: docker compose down -v
git add -A
git commit -m "Stage 2: accounts, transactions, deadlock, outbox, measurements"
git push -u origin stage/02-accounts-transactions
```

Тег `v0.2` ставьте только после журнала (`docs/journal/02-accounts-transactions.md`) и ADR-0003/0004 (S13 плана).

---

## Частые ошибки и быстрые решения

| Симптом | Причина | Решение |
|---|---|---|
| `error CS...` при сборке | код этапа написан без компиляции | прислать ассистенту полный текст ошибок |
| `relation "accounts.reservations" does not exist` (DataGen, API) | не применена миграция `Stage02` | шаги C2 и C3 |
| `ef.ps1 add` создал миграцию с `DropTable`/`AlterColumn` | в модель попали посторонние изменения | удалить файлы миграции, не применять, прислать вывод |
| `400 {"error":"unknown mode"}` на всех запросах с `?mode=` | API запущен без `Accounts__AllowModeOverride=true` | шаг D2: переменная должна быть выставлена в окне №1 до `dotnet run` |
| C1 `naive` даёт `lost=0` | на этой машине гонка не поймалась | `--parallel 150` или больше `--ops` |
| C2 `unordered` не даёт 409 | слишком мало параллелизма | `--accounts 2 --parallel 100` |
| C2 `unordered` идёт очень долго | каждый deadlock ждёт `deadlock_timeout` 1 с | нормально; уменьшить `--ops` до 1000 |
| k6 `insufficient_funds` больше 5% | резервы накопились от прошлых прогонов | сброс резервов из F4 или DataGen S и перезапуск API |
| Тест `Two_publishers_do_not_duplicate_messages` падает по таймауту/дублям | фоновые публикаторы не приостановились | прислать вывод теста, разберём |
| `the input device is not a TTY` | `docker compose exec` в конвейере без `-T` | добавить `-T` |
| Остальное | см. таблицу в `docs/RUNBOOK.md` | |
