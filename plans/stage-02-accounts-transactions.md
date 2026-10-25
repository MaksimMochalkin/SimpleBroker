# stage-02-accounts-transactions.md — Этап 2. Accounts and transactions: isolation, deadlocks, outbox

> Обновлено: 2026-10-22 · Ветка: `stage/02-accounts-transactions` · Тег по завершении: `v0.2` · Статус: Не начат (детализирован заранее, старт после тега `v0.1`) · Сроки: нед 3–4 (2026-11-03 → 2026-11-16) · Родитель: [plan_highload_broker.md](plan_highload_broker.md)

**Пошаговый запуск и проверки с ожидаемыми результатами: [docs/RUNBOOK-stage-02.md](../docs/RUNBOOK-stage-02.md).**

**Как читать:** разделы 0–3 и текущую сессию. Раздел 4 (список файлов) и кейсы (6) открываем, когда сессия до них дошла. Реализованный скрипт заменяем ссылкой на путь в репозитории. Код этапа написан заранее без сборки (раздел 4).

---

## 0. Цель и границы

**Цель:** воспроизвести на балансах потерянное обновление и deadlock, затем устранить их разными средствами (атомарный SQL, `FOR UPDATE`, оптимистичный контроль, уровни изоляции, порядок блокировок, retry) и сравнить цену каждого. Добавить резервирование средств при заявке и transactional outbox.

**Не делаем:**
- Резерв под Sell и позиции (этап 5), списание резерва при исполнении (`Consumed` в enum есть, не используется), отмену заявки (этап 5).
- Реальный брокер сообщений (этап 9): получатель outbox — заглушка с логом.
- Reconciler «резерв без заявки» (этап 10), `Idempotency-Key` на HTTP (этап 10).
- Индексы по trades и починку M-baseline (этап 3).

**Готово, когда:**
1. C1: режим `Naive` воспроизводит потерю обновлений (`lost ≠ 0` или `ok > deposit`) в 3 из 3 прогонов; остальные 5 режимов: `lost = 0`, перепродажи нет, баланс ≥ 0.
2. C2: `Unordered` даёт deadlock (`Δ pg_stat_database.deadlocks > 0`, ответы 409 `deadlock`); `Ordered` и `UnorderedRetry` дают 0 клиентских ошибок и неизменную сумму балансов; p95 трёх режимов записан.
3. Buy-заявка резервирует `price × quantity`, при нехватке 422 и заявка не создаётся; `OrderCreated` и `TransferCompleted` доходят до sink без дублей при двух публикаторах; все тесты зелёные.
4. `docs/journal/02-accounts-transactions.md`, ADR-0003, ADR-0004 (EN), ветка, тег `v0.2`.

---

## 1. Контекст и исходные цифры

Из этапа 1 (машина A, scale S, L1): 207.7 rps, p95 10.4 ms, ошибок 0. M-baseline сломан (41 rps) из-за trades, это работа этапа 3; в этапе 2 замеры идут на S и на собственных счетах C1–C3.

Что есть в коде сейчас: `Deposit` атомарный (`ExecuteUpdate` с `available + @a`); `Balance.Version` растёт только при deposit; списания и переводов нет; `Orders` не резервирует средства; `IAccountsApi` содержит только `ExistsAsync`. DataGen кладёт балансы 1 000–100 000, `reserved = 0`.

**Всё в этапе меряем на одной машине** (решение 20 основного плана), машину указать в разделе 8.

---

## 2. Решения этапа

| # | Решение | Почему |
|---|---|---|
| 1 | Режим списания/перевода выбирается `?mode=` только при `Accounts:AllowModeOverride=true`; по умолчанию `Atomic` / `Ordered` (`Accounts:WithdrawMode`, `Accounts:TransferMode`) | Один API для всех замеров, в проде нет рубильника |
| 2 | Эталон для «одна строка, простое условие» — `UPDATE ... WHERE available >= @a`; `FOR UPDATE` нужен, когда между чтением и записью есть логика (перевод) | Показать, что самый дешёвый способ часто и самый быстрый |
| 3 | `Balance.Version` = EF concurrency token (`IsConcurrencyToken`), `xmin` не используем | Version уже есть в API и данных; `ExecuteUpdate` токен игнорирует, поэтому `Naive` и `Atomic` на нём не ломаются |
| 4 | Свой `TxRetry` (40001, 40P01; максимум 5 попыток; backoff с джиттером), а не `EnableRetryOnFailure` | Видим каждую попытку в метриках; стратегия EF скрывает конфликт |
| 5 | Резерв без распределённой транзакции: `Orders` → `IAccountsApi.ReserveAsync` (своя tx в Accounts) → insert заявки; при сбое insert — `ReleaseAsync` | Один `DbContext` на модуль (ADR-0001) — общую транзакцию не делаем, иначе модуль не вынести. Окно «резерв без заявки» остаётся (риск 2) |
| 6 | Резерв: Buy Limit = `price × qty`; Buy Market требует `price` как потолок цены (иначе 400); Sell не резервирует | Нет позиций до этапа 5 |
| 7 | Резерв идемпотентен по `reservationId` (Guid, генерирует Orders); `reservation_id` сохраняется в заявке | Повтор после таймаута не списывает дважды |
| 8 | Общий проект `SimpleBroker.Platform` (`TxRetry`, outbox, метрики), не зависит от модулей; таблица `outbox` в схеме каждого модуля | Без дублирования кода; границы модулей целы |
| 9 | Публикатор: опрос 200 мс, `FOR UPDATE SKIP LOCKED`, батч 100; гарантия at-least-once, порядок между публикаторами не гарантируется, потребитель идемпотентен по `id` | Простейшая схема, масштабируется на N инстансов |
| 10 | `CHECK (available >= 0 AND reserved >= 0)` на `balances` | Страховка уровня БД; в `Naive` она не срабатывает (пишем абсолют), в этом суть демонстрации |

---

## 3. Шаги: 14 сессий по ~2 часа

Недели 3–4. Старт только после `v0.1`: `git checkout main && git checkout -b stage/02-accounts-transactions`. Код уже написан (раздел 4): задачи `[U]` = собрать, исправить, прогнать, замерить.

- [ ] **S1** [U] Проект `SimpleBroker.Platform` (раздел 4), `Reservation`/`WithdrawMode`/`AccountsMetrics`, `WithdrawAsync` режимы `Naive` и `Atomic`, endpoint `/accounts/{id}/withdraw`, `Version` как токен. Тесты: 200/404/422.
- [ ] **S2** [U] `tools/Contention` (6.1), кейс C1 на `Naive`. Записать 3 прогона: `lost`, `ok`, `final`.
- [ ] **S3** [U] Режим `ForUpdate`; C1 на `Atomic` и `ForUpdate`; сравнить p95 и throughput.
- [ ] **S4** [U] `TxRetry`, режимы `Optimistic`, `RepeatableRead`, `Serializable`; метрики retries/conflicts; C1 на всех режимах. Опционально: тест write skew на `Serializable` (два параллельных списания с разных счетов при условии «сумма ≥ 0»).
- [ ] **S5** [U] Перевод `Unordered` без retry, C2: воспроизвести deadlock, снять `pg_stat_database.deadlocks` и лог Postgres (5.2).
- [ ] **S6** [U] `UnorderedRetry` и `Ordered`; C2 на трёх режимах; инвариант суммы.
- [ ] **S7** [U] Резервы: `IAccountsApi.ReserveAsync/ReleaseAsync`, таблица `reservations`, изменения `OrdersService`; обновить тесты этапа 1 (риск 5); тесты резерва.
- [ ] **S8** [U] Outbox: `OutboxMessage`, `AddOutbox`, запись `TransferCompleted` и `OrderCreated` в той же транзакции.
- [ ] **S9** [U] `OutboxPublisher`, `LoggingMessageSink`, метрики lag; тест «два публикатора, нет дублей».
- [ ] **S10** [U] Миграции `Stage02` (5.1), применить на compose; архитектурный тест для `Platform`; `dotnet build` решения целиком.
- [ ] **S11** [U] C3: `loadtests/hot-account.js` (6.2), панели Grafana (5.3), прогоны по режимам.
- [ ] **S12** [U] L1 на S после этапа: «цена корректности» относительно baseline (раздел 8). Перед каждым прогоном DataGen S (резервы «съедают» балансы).
- [ ] **S13** [U] `docs/journal/02-accounts-transactions.md`, ADR-0003 (стратегии конкурентного доступа), ADR-0004 (резерв без распределённой транзакции + outbox), строка в README. [A] ревью EN.
- [ ] **S14** Буфер, чистка, `docker compose down -v && up`, тег `v0.2`, статусы в планах.

**Как проверить:**
- `Contention --case c1 --mode naive` печатает `lost > 0`; тот же прогон с `--mode atomic` печатает `lost=0`.
- `Contention --case c2 --mode unordered --accounts 2`: часть ответов 409 `deadlock`, `Δdeadlocks > 0`; с `--mode ordered` 0 ошибок.
- `POST /orders` Buy на сумму больше `available` → 422, строки в `orders.orders` нет; после успешной заявки `GET balance`: `reserved = price × qty`.
- Через ~1 с после операции строка в `outbox` получает `published_at`, в логе приложения видна доставка.

---

## 4. Код этапа (написан заранее, не собирался)

Весь код этапа уже лежит в репозитории. Сессии `[U]` = собрать, поправить ошибки компиляции, прогнать тесты, снять замеры. Ошибки сборки возвращаем ассистенту.

| Путь | Что |
|---|---|
| `src/SimpleBroker.Platform/` | Новый проект: `TxRetry`, `PlatformMetrics`, `Outbox/` (`OutboxMessage`, `AddOutbox`, `OutboxPublisher<T>`, `IMessageSink`, `LoggingMessageSink`, `OutboxControl`) |
| `src/SimpleBroker.Accounts.Contracts/AccountsContracts.cs` | `ReserveAsync`/`ReleaseAsync`, `ReserveResult`, `WithdrawRequest`, `TransferRequest/Response` |
| `src/SimpleBroker.Accounts/Internal/` | `Modes.cs`, `AccountsMetrics.cs`, `Reservation` в `Entities.cs`, `AccountsDbContext` (токен `Version`, CHECK, outbox, резервы), `AccountsService` (6 режимов списания, 3 режима перевода, резервы) |
| `src/SimpleBroker.Accounts/AccountsModule.cs` | Endpoints `/accounts/{id}/withdraw`, `/transfers`; `?mode=` только при `Accounts:AllowModeOverride=true` |
| `src/SimpleBroker.Orders/` | `Order.ReservationId`, outbox в контексте, `OrdersService` (резерв, компенсация, `OrderCreated` в одной транзакции), 422 в модуле |
| `src/SimpleBroker.Host/appsettings.json` | Секция `Accounts` (`AllowModeOverride=false`, `WithdrawMode=Atomic`, `TransferMode=Ordered`) |
| `tools/Contention/`, `loadtests/hot-account.js` | Харнесс C1/C2 и сценарий C3 |
| `tools/ef.sh`, `tools/ef.ps1`, `tools/DataGen/Program.cs`, `docker-compose.yml`, `SimpleBroker.slnx` | `add <Name> [Module...]`; TRUNCATE новых таблиц (требует миграцию `Stage02`); флаги Postgres; новые проекты в решении |
| `tests/SimpleBroker.IntegrationTests/` | `Api.cs`, `WithdrawTests`, `TransferTests`, `ReservationTests`, `OutboxTests`, правки `OrdersTests` и `BrokerFixture` |
| `tests/SimpleBroker.ArchitectureTests/` | `Platform_does_not_reference_modules` |

Тесты `Category=Demo` (`Naive_mode_shows_lost_updates`, `Unordered_opposing_transfers_demo`) показывают проблему в выводе и не являются регрессией; для CI исключить: `--filter "Category!=Demo"`.

**Чего в коде нет:** миграции `Stage02` (генерируется командой из 5.1), панелей Grafana (5.3), write-skew теста (опционально в S4).

---

## 5. Инфраструктура и инструкции

### 5.1 Миграции и конфигурация

`tools/ef.sh` и `tools/ef.ps1` уже принимают имя и список модулей. Миграция `Stage02`: `accounts.reservations`, `accounts.outbox`, `orders.outbox`, `orders.orders.reservation_id`, `ck_balances_nonneg`; `Trades` не трогаем. Применить: `.\\tools\\ef.ps1 add -Name Stage02 -Modules Accounts,Orders`, затем `.\\tools\\ef.ps1 update`. Для замеров: `$env:Accounts__AllowModeOverride="true"`.

### 5.2 Postgres и диагностика

В `docker-compose.yml` в `command` Postgres добавить `"-c", "log_lock_waits=on", "-c", "deadlock_timeout=1s"` (1 s — значение по умолчанию, фиксируем явно: жертва deadlock ждёт его, это попадёт в p95 C2).

```powershell
docker compose exec postgres psql -U broker -d broker -c "SELECT pg_stat_reset();"      # перед прогоном
docker compose exec postgres psql -U broker -d broker -c "SELECT deadlocks, xact_commit, xact_rollback FROM pg_stat_database WHERE datname='broker';"
docker compose logs postgres | Select-String "deadlock detected"
docker compose exec postgres psql -U broker -d broker -c "SELECT wait_event_type, wait_event, count(*) FROM pg_stat_activity WHERE datname='broker' GROUP BY 1,2 ORDER BY 3 DESC;"   # во время C3
```

### 5.3 Панели Grafana (добавить в `infra/grafana/dashboards/simple-broker.json`)

p95 `simple_broker_accounts_withdraw_duration_ms` по `mode`; rate `simple_broker_db_retries_total` по `reason`; rate `simple_broker_accounts_optimistic_conflicts_total`; rate `simple_broker_outbox_published_total`; p95 `simple_broker_outbox_lag_seconds`. Точные имена сверить в Prometheus UI.

### 5.4 Команды

```powershell
dotnet run -c Release --project tools/Contention -- --case c1 --mode naive --deposit 10000 --ops 5000 --parallel 50
dotnet run -c Release --project tools/Contention -- --case c2 --mode unordered --accounts 2 --ops 2000 --parallel 50 --deposit 100000
k6 run -e MODE=atomic -e ACCOUNT=1 loadtests/hot-account.js
```

Прогоны только в Release, на прогретом приложении (одна пробная серия отбрасывается), по 3 прогона на режим, берём медиану.

---

## 6. Кейсы нагрузки

| ID | Сценарий | Профиль | Метрики | Цель | Скрипт |
|---|---|---|---|---|---|
| C1 | N параллельных списаний по 1.00 с одного счёта (`deposit` 10 000, 5 000 операций; вариант oversell: `deposit` 1 000, 5 000 операций) | 50 параллельных, один раз до конца | `ok`, `expected`, `actual`, `lost`, p50/p95/p99, throughput | `Naive`: `lost ≠ 0` / `ok > deposit`; остальные: 0 | `tools/Contention` |
| C2 | Встречные переводы по 1.00 между 2 счетами (и вариант 20 счетов) | 50 параллельных, 2 000 операций | 409 `deadlock`, `Δ pg_stat_database.deadlocks`, p95, сумма балансов | `Unordered` ломается, `Ordered`/`UnorderedRetry`: 0 ошибок | `tools/Contention` |
| C3 | Горячий счёт: списания по 0.01 с одного счёта при росте конкуренции | ramping-vus 10→50→200, ~3 мин на режим | RPS, p95, retries/conflicts, `wait_event` | цена режимов при конкуренции | `loadtests/hot-account.js` |
| L1′ | L1 этапа 1 после этапа 2 (Buy теперь резервирует) | как L1, scale S, DataGen перед прогоном | RPS, p95, доля 422 | цена резерва + outbox относительно 207.7 rps / p95 10.4 ms | `loadtests/baseline.js` |

Для L1′: в `loadtests/common.js` `orderBody` ограничить `quantity` до 1–10 (макс. резерв ≈ 1 100), добавить `http.setResponseCallback(http.expectedStatuses({ min: 200, max: 299 }, 422))` и счётчик 422, иначе порог `http_req_failed < 1%` посчитает 422 ошибкой.

### 6.1 Harness C1/C2

Реализация: `tools/Contention/Program.cs` (уже в `SimpleBroker.slnx`).

### 6.2 Горячий счёт

Реализация: `loadtests/hot-account.js` (черновик готов; не запускался).

---

## 7. Схемы

**Заявка Buy: резерв, заявка, outbox**

```mermaid
sequenceDiagram
  participant C as Client
  participant O as Orders
  participant A as Accounts (Contracts)
  participant DB as PostgreSQL
  C->>O: POST /orders (Buy Limit)
  O->>A: ReserveAsync(reservationId, accountId, price*qty)
  A->>DB: tx: INSERT reservation; UPDATE balances WHERE available >= amount
  A-->>O: Reserved
  O->>DB: tx: INSERT order (reservation_id) + INSERT outbox
  alt insert failed
    O->>A: ReleaseAsync(reservationId)
  end
  O-->>C: 201
  Note over DB: OutboxPublisher: SELECT ... FOR UPDATE SKIP LOCKED -> sink -> published_at
```

**Deadlock на встречных переводах (`Unordered`)**

```mermaid
sequenceDiagram
  participant T1 as Tx1 A to B
  participant T2 as Tx2 B to A
  participant DB as PostgreSQL
  T1->>DB: FOR UPDATE A (ok)
  T2->>DB: FOR UPDATE B (ok)
  T1->>DB: FOR UPDATE B (wait)
  T2->>DB: FOR UPDATE A (wait)
  Note over DB: цикл; через deadlock_timeout одна tx получает 40P01
```

**Изменения схемы:** `accounts.reservations` (id uuid PK, account_id, amount numeric(28,8), status text, created_at); `accounts.outbox` и `orders.outbox` (id, type, payload jsonb, created_at, published_at; частичный индекс по `published_at IS NULL`); `orders.orders.reservation_id uuid NULL`; `CHECK` на `balances`. FK между схемами по-прежнему нет.

---

## 8. Результаты замеров

**Окружение:** _машина, CPU/RAM, лимиты Docker (cpus 2, mem 4g), .NET, Postgres 17; все замеры этапа на одной машине._

**C1/C2: инвариант** (медиана 3 прогонов)

| Кейс | Режим | ok | expected | actual | lost | oversell | p95 ms | rps | Заметки |
|---|---|---|---|---|---|---|---|---|---|
| C1 | Naive | | | | | | | | |
| C1 | Atomic | | | | | | | | |
| C1 | ForUpdate | | | | | | | | |
| C1 | Optimistic | | | | | | | | 409 после 5 попыток: |
| C1 | RepeatableRead | | | | | | | | retries: |
| C1 | Serializable | | | | | | | | retries: |
| C2 | Unordered | | | | | | | | Δdeadlocks: · 409: |
| C2 | UnorderedRetry | | | | | | | | retries: |
| C2 | Ordered | | | | | | | | |

**Остальное**

| # | Кейс | Версия | RPS | p50 | p95 | p99 | Ошибки | Что насытилось | Заметки |
|---|---|---|---|---|---|---|---|---|---|
| 1 | C3 | atomic | | | | | | | |
| 2 | C3 | forupdate | | | | | | | |
| 3 | C3 | optimistic | | | | | | | |
| 4 | C3 | serializable | | | | | | | |
| 5 | L1′ | S, после этапа 2 | | | | | | | baseline S: 207.7 rps, p95 10.4 ms |

---

## 9. Риски и вопросы этапа

| # | Что | Действие |
|---|---|---|
| 1 | `Naive` не ломается на быстрой машине | Поднять `--parallel` (100–200); запасной вариант: параметр `delayMs` между чтением и записью под флагом `AllowModeOverride` |
| 2 | Резерв без заявки при падении процесса между `Reserve` и insert (`Release` не вызовется) | Принято на этапе 2 (решение 5); фиксируем в ADR-0004, reconciler на этапе 10 |
| 3 | L1′ несравним с L1: появились резервы, балансы «съедаются» | DataGen S перед каждым прогоном, 422 учитываем отдельно, сравниваем только внутри одной машины |
| 4 | `Serializable`/`RepeatableRead` на горячем счёте дают шторм retry | Это результат, а не ошибка: фиксируем долю 409 после 5 попыток и retries/сек |
| 5 | Тесты этапа 1 ломаются: Buy без депозита (теперь 422), Market Buy без `price` (теперь 400) | Обновить `OrdersTests` в S7: депозит перед Buy, `price` у Market Buy |
| 6 | Жертва deadlock ждёт `deadlock_timeout` (1 s): p95 `Unordered` определяется им, а не работой | В разделе 8 писать p95 и долю deadlock отдельно; не сравнивать p95 `Unordered` с остальными |
| 7 | Порядок блокировок через `ORDER BY ... FOR UPDATE` зависит от плана | Проверить `EXPLAIN` (узел `LockRows` над `Sort`); если план изменится, брать замки двумя запросами по возрастанию id |
| 9 | Код этапа ни разу не собирался и не запускался | Первый `dotnet build -v q --nologo` и тесты на машине пользователя, ошибки вернуть ассистенту |
| 8 | `at-least-once` даёт дубликаты в sink | Тест на два публикатора фиксирует поведение; потребитель этапа 9 идемпотентен по `id` |

---

## Итог

*(заполняется по завершении, 2–5 строк: что сделано, что отличается от плана, что перенесено в `CLAUDE.md`; в journal-запись `docs/journal/02-accounts-transactions.md` идёт версия на английском)*
