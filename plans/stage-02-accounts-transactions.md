# stage-02-accounts-transactions.md — Этап 2. Accounts and transactions: isolation, deadlocks, outbox

> Обновлено: 2026-10-30 · Ветка: `stage/02-accounts-transactions` · Тег по завершении: `v0.2` · Статус: Проверен по согласованию с пользователем; интеграционные и нагрузочные критерии, не подтверждённые в этой среде, остаются оговорёнными в [отчёте этапа 2](../docs/journal/stage-02-accounts-transactions-report-ru.md) · Сроки: нед 3–4 (2026-11-03 → 2026-11-16) · Родитель: [plan_highload_broker.md](plan_highload_broker.md)

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

- [x] **S1** [U] Проект `SimpleBroker.Platform` (раздел 4), `Reservation`/`WithdrawMode`/`AccountsMetrics`, `WithdrawAsync` режимы `Naive` и `Atomic`, endpoint `/accounts/{id}/withdraw`, `Version` как токен. Тесты: 200/404/422.
- [x] **S2** [U] `tools/Contention` (6.1), кейс C1 на `Naive`. Записать 3 прогона: `lost`, `ok`, `final`.
- [x] **S3** [U] Режим `ForUpdate`; C1 на `Atomic` и `ForUpdate`; сравнить p95 и throughput.
- [x] **S4** [U] `TxRetry`, режимы `Optimistic`, `RepeatableRead`, `Serializable`; метрики retries/conflicts; C1 на всех режимах. Опционально: тест write skew на `Serializable` (два параллельных списания с разных счетов при условии «сумма ≥ 0»).
- [x] **S5** [U] Перевод `Unordered` без retry, C2: воспроизвести deadlock, снять `pg_stat_database.deadlocks` и лог Postgres (5.2).
- [x] **S6** [U] `UnorderedRetry` и `Ordered`; C2 на трёх режимах; инвариант суммы. При параллелизме 2 UnorderedRetry провёл 2 000/2 000 запросов; при параллелизме 16 остались 409 — смотреть отчёт и сырой лог.
- [x] **S7** [U] Резервы: `IAccountsApi.ReserveAsync/ReleaseAsync`, таблица `reservations`, изменения `OrdersService`; обновить тесты этапа 1 (риск 5); тесты резерва.
- [x] **S8** [U] Outbox: `OutboxMessage`, `AddOutbox`, запись `TransferCompleted` и `OrderCreated` в той же транзакции.
- [x] **S9** [U] `OutboxPublisher`, `LoggingMessageSink`, метрики lag; тест «два публикатора, нет дублей».
- [x] **S10** [U] Миграции `Stage02` (5.1), применить на compose; архитектурный тест для `Platform`; `dotnet build` решения целиком.
- [ ] **S11** [U] C3: `loadtests/hot-account.js` (6.2), панели Grafana (5.3), прогоны по режимам. Панели добавлены; C3 k6 ещё выполняется.
- [ ] **S12** [U] L1 на S после этапа: «цена корректности» относительно baseline (раздел 8). Перед каждым прогоном DataGen S (резервы «съедают» балансы).
- [x] **S13** [U] `docs/journal/02-accounts-transactions.md`, ADR-0003 (стратегии конкурентного доступа), ADR-0004 (резерв без распределённой транзакции + outbox), строка в README. [A] ревью EN.
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

**Окружение:** машина B — Intel Core i5-1135G7 (4 ядра / 8 логических процессоров), 15.7 GiB RAM, WDC PC SN530 NVMe; .NET SDK 10.0.402, Docker 29.9.2, k6 2.2.0, PostgreSQL 17.11. PostgreSQL ограничен 2 CPU / 4 GiB; API, k6 и Docker работают на том же компьютере. Все результаты ниже — только машина B; сравнивать их с машиной A нельзя.

**Подтверждённая проверка:** `dotnet build SimpleBroker.slnx -c Release -v q --nologo` — Build succeeded, 0 ошибок, 5 предупреждений анализаторов. `dotnet test SimpleBroker.slnx --nologo -v q --filter "Category!=Demo"` — архитектурные тесты: 10 пройдено; интеграционные: 0 из 42, все завершились ошибкой до запуска проверок. Изолированный тест подтвердил, что Testcontainers получает HTTP 500 от Docker Engine при старте Resource Reaper (`BrokerFixture.InitializeAsync`), затем cleanup падает с `NullReferenceException`. Повтор одного теста с `TESTCONTAINERS_RYUK_DISABLED=true` завис после подключения к Docker до старта fixture и был остановлен без результата.

**C1: обычное списание** (в файлах есть только один прогон на режим; это значения наблюдённого прогона, не медиана)

| Кейс | Режим | ok | expected | actual | lost | oversell | p95 ms | rps | Заметки |
|---|---|---|---|---|---|---|---|---|---|
| C1 | Naive | 5000 | 5000 | 9889 | 4889 | 0 | 311.3 | 474 | Обновления потеряны; n=1 |
| C1 | Atomic | 5000 | 5000 | 5000 | 0 | 0 | 295.8 | 513 | Инвариант соблюдён; n=1 |
| C1 | ForUpdate | 5000 | 5000 | 5000 | 0 | 0 | 1166.8 | 152 | Инвариант соблюдён; n=1 |
| C1 | Optimistic | 1269 | 8731 | 8731 | 0 | 0 | 434.2 | 183 | 3731 ответов 409; n=1 |
| C1 | RepeatableRead | 1981 | 8019 | 8019 | 0 | 0 | 2456.4 | 39 | 3019 ответов 409; n=1 |
| C1 | Serializable | 1762 | 8238 | 8238 | 0 | 0 | 1795.5 | 45 | 3238 ответов 409; n=1 |

**C1: вариант перепродажи** (один прогон на режим; депозит 1000, 5000 попыток)

| Режим | HTTP-коды | actual | lost | oversell | p95 ms | rps |
|---|---|---:|---:|---:|---:|---:|
| Naive | 200=5000 | 903 | 4903 | 4000 | 305.4 | 481 |
| Atomic | 200=1000, 422=4000 | 0 | 0 | 0 | 157.5 | 1197 |
| ForUpdate | 200=1000, 422=4000 | 0 | 0 | 0 | 305.8 | 455 |
| Optimistic | 200=1000, 409=2751, 422=1249 | 0 | 0 | 0 | 330.2 | 300 |
| RepeatableRead | 200=1000, 409=1803, 422=2197 | 0 | 0 | 0 | 1377.9 | 96 |
| Serializable | 200=1000, 409=1715, 422=2285 | 0 | 0 | 0 | 1677.2 | 81 |

**C2: встречные переводы** (Unordered и Ordered — по 3 прогона; UnorderedRetry не завершил ни одного клиентского прогона)

| Режим | Медиана rps | Медиана p95 ms | HTTP-результат | deadlocks из pg_stat_database | Инвариант |
|---|---:|---:|---|---:|---|
| Unordered | 8 | 21125.1 | В каждом прогоне были 409; в прогонах 1 и 3 также 500 | 1901 / 1994 / 1967 | Во всех трёх `sum=200000` |
| UnorderedRetry | — | — | Все три harness-прогона завершились `TaskCanceledException` по `HttpClient.Timeout`=100 с; итоговые коды/сумма не получены | 912 / 2648 / 876* | Не подтверждён |
| Ordered | 183 | 841.5 | Во всех прогонах `200=2000` | 0 / 0 / 0 | Во всех трёх `sum=200000` |

*Счётчики deadlock записаны в выходных файлах, но их семантика здесь — значения после соответствующего прогона, а не подтверждение успешности `UnorderedRetry`.

**Остальное**

| # | Кейс | Версия | RPS | p50 | p95 | p99 | Ошибки | Что насытилось | Заметки |
|---|---|---|---|---|---|---|---|---|---|
| 1 | C3 | atomic | 497.45 | 28.1 ms | 629.06 ms | 1.11 s | 0/89561 | До 199 VU; высокие задержки при горячей строке | один прогон |
| 2 | C3 | forupdate | 151.86 | 27.16 ms | 2.27 s | 4.12 s | 0/27351 | В послепрогонном снимке: 97 ожиданий `Lock/tuple` | один прогон |
| 3 | C3 | optimistic | 69.82 | 294.86 ms | 4.37 s | 5.02 s | 0/12573 | 7344 конфликтов 409 (40.78/с); снимок: 94 `MultiXactMemberSLRU` | один прогон |
| 4 | C3 | serializable | 34.76 | 227.51 ms | 12.63 s | 20.32 s | 109/6655 (1.63%) | 200 VU; 2539 конфликтов 409 (13.26/с) | 6545 из 6654 проверок прошли; один прогон |
| 5 | L1′ | S, после этапа 2, run 1 | 41.27 | 15.02 s | 15.51 s | 29.43 s | 8647/17333 (49.88%) | 500 VU; 63636 dropped iterations | превышены оба порога |
| 6 | L1′ | S, после этапа 2, run 2 | 29.70 | 15.06 s | 15.56 s | 60 s | 10968/12478 (87.89%) | 500 VU; 68481 dropped iterations | превышены оба порога |
| 7 | L1′ | S, после этапа 2, run 3 | 27.81 | 15.05 s | 15.46 s | 60 s | 11352/11690 (97.10%) | 500 VU; 69277 dropped iterations | превышены оба порога |

В `loadtests/common.js` ответы 422 уже исключены из `http_req_failed`; поэтому ими нельзя объяснить провал L1′. В логах L1 есть таймауты запросов, но конкретные доли 422 и 5xx не сохранены. Снимки `pg_stat_activity` для C3 сделаны после прогонов, а не синхронно во время них.

**Материалы:** исходные логи находятся в [`docs/journal/data/stage-02/`](../docs/journal/data/stage-02/); подробный разбор — в [отчёте на английском](../docs/journal/stage-02-accounts-transactions-report.md) и [отчёте на русском](../docs/journal/stage-02-accounts-transactions-report-ru.md).

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
| 9 | Интеграционные тесты не стартуют: Docker Engine отвечает HTTP 500 при запуске Testcontainers Resource Reaper; retry без reaper завис до старта fixture | Проверить Docker Desktop/Testcontainers на машине B и повторить тесты; не считать интеграцию пройденной до результата |
| 8 | `at-least-once` даёт дубликаты в sink | Тест на два публикатора фиксирует поведение; потребитель этапа 9 идемпотентен по `id` |

---

## Итог

Stage 2 реализация проходит архитектурные тесты (10/10); C1 показывает, что `Naive` теряет обновления и перепродаёт баланс, а корректные режимы сохраняют инварианты. `Ordered` устранил deadlock в трёх C2-прогонах; три C3 и три L1′ измерения записаны на машине B. Интеграционные тесты пока заблокированы запуском Testcontainers: Docker Engine возвращает HTTP 500 для Resource Reaper. Этап не завершён: C1 не повторён трижды, `UnorderedRetry` падает по 100-секундному таймауту, L1′ превышает пороги; нужны smoke-проверки и ADR до решения о теге.
