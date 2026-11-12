КОНТЕКСТ ПРОЕКТА
Продукт: Agent Platform — мультиарендная платформа для создания, хостинга
и эксплуатации AI-агентов. Платформа НЕ содержит бизнес-логики решений:
бизнес-сценарии (например, «агент продаж») собираются из её примитивов
как Solution Profiles (шаблоны конфигураций). Документ проекта: v2.0.

ПЯТЬ МЕХАНИЗМОВ ПЛАТФОРМЫ:
1. Runtime агентов (диалоги, тулы, HITL-гейты, чекпоинты, эскалация, takeover)
2. Коннекторы — внешние системы подключаются как MCP-серверы по манифесту
   (у манифеста есть JSON Schema и валидация при установке)
3. Каналы — плагины по контракту IChannelAdapter (виджет, Telegram; далее — другие)
4. Знания — RAG-инфраструктура (инжест, pgvector); контент — только данные
5. Управление — кабинет тенанта + панель оператора платформы

СТЕК (фиксированный, не менять без обсуждения):
- .NET 10, ASP.NET Core (minimal APIs), C#
- Оркестрация: Microsoft.Agents.AI.Workflows (Agent Framework 1.x GA)
- Абстракция модели: Microsoft.Extensions.AI (IChatClient)
- MCP: официальный SDK ModelContextProtocol 2.x (C#), transport — streamable HTTP
- БД: PostgreSQL (данные + чекпоинты), Redis (кеши, очереди, rate-limit)
- Наблюдаемость: OpenTelemetry → Jaeger/Grafana
- Развёртывание: Docker Compose, один VPS в РФ (152-ФЗ)

АРХИТЕКТУРНЫЕ КОНСТАНТЫ:
- Слои: Gateway / Agent Runtime / LLM Gateway / Connector Host / Knowledge
  Service / Tenant App / Controller Service + AgentPlatform.Core (общие
  контракты). Слои не вызывают внешние API напрямую — только через Connector Host.
- Оркестрация — заменяемый модуль за интерфейсом IOrchestrationRuntime:
  RunAsync(session, message), PauseAsync(session, gatePayload),
  ResumeAsync(session, decision). Текущая реализация — MafOrchestrationRuntime.
- Мультиарендность: tenant_id во всех сущностях и запросах; изоляция через
  TenantScopedRepository<T> (ITenantContext из DI).
- Тулы классифицируются политиками read / write / destructive.
  Политики проверяются СЕРВЕРНО (Policy Engine), не промптом. Повышение
  политики конфигурацией запрещено, понижение разрешено. Write/destructive
  проходят через gate-узел с подтверждением (approvals, статус Pending).
- State сессий сериализуется в НЕЙТРАЛЬНЫЙ JSON-контракт платформы
  (messages, pending_gate, counters, version) — НЕ в нативные структуры
  рантайма. Требование: смена оркестратора не мигрирует историю сессий.
- Коннектор описывается манифестом: name, version, auth, tools[]
  (schema, policy, cacheClass), limits, cacheClasses. Платформа уважает
  декларированные лимиты (очередь на пару tenant×connector, ретраи, кеш read).
- Секреты тенантов: AES-256, доступ коннекторов через Secret Service;
  в логах и трейсах секретов нет. ПДн маскируются.
- Планировщик (Scheduler): cron-запуск сессий агентов без входящего
  сообщения — для внутренних агентов (например, агент качества).

ВНУТРЕННИЕ КОННЕКТОРЫ:
- EchoTools: тестовый read+write коннектор для проверки графа.
- platform-ops: read-тулы над данными платформы (ops_get_dialogs,
  ops_get_tool_stats, ops_get_escalations, ops_get_unanswered_questions)
  и write-тул ops_propose_agent_update (через гейт). Доступ — только
  по флагу тенанта/агента в tool_settings.

ПРАВИЛА РАБОТЫ ДЛЯ АГЕНТА-КОДЕРА:
- Пиши в стиле clean architecture: слой — отдельный проект в solution.
- Покрывай юнит-тестами гейты, политики, лимиты, edge-cases resume (xUnit).
- Не добавляй библиотеки без указания в задаче.
- Не выдумывай API внешних систем: контракт не описан — интерфейс с заглушкой
  и пометкой // TODO: contract.
- Соблюдай контракты Core: IOrchestrationRuntime, IChannelAdapter,
  IPolicyEngine, ConnectorManifest. Расширение контракта — отдельная задача,
  не попутная правка.
- После каждой задачи выводи: список изменённых файлов, как запустить,
  как проверить.

---

# ЭКСПЛУАТАЦИЯ И ПРОВЕРКА (задача 1.1)

## 1. Состав solution

| Проект | Тип | Назначение | Ссылки |
|---|---|---|---|
| `AgentPlatform.Core` | classlib | контракты (IOrchestrationRuntime, IChannelAdapter, IPolicyEngine, ITenantContext), DTO, enums, StateContract, валидаторы jsonb | нет |
| `AgentPlatform.Infrastructure` | classlib | EF Core 10 + Npgsql, модель данных (18 таблиц), миграции, TenantScopedRepository, seed | Core |
| `AgentPlatform.Gateway` | ASP.NET Core | приём сообщений каналов (заглушка) | Core |
| `AgentPlatform.Runtime` | classlib | `MafOrchestrationRuntime` на Microsoft.Agents.AI.Workflows (заглушка) | Core |
| `AgentPlatform.LlmGateway` | classlib | обёртка `IChatClient` (Microsoft.Extensions.AI), StubChatClient | Core |
| `AgentPlatform.ConnectorHost` | ASP.NET Core | MCP-сервер (ModelContextProtocol 2.x), EchoTools + манифест | Core |
| `AgentPlatform.Knowledge` | ASP.NET Core | инжест/поиск (заглушка, контракт `/api/v1/knowledge/search`) | Core, Infrastructure |
| `AgentPlatform.TenantApp` | ASP.NET Core | API кабинета тенанта (заглушка) + CLI миграций/seed | Core, Infrastructure |
| `AgentPlatform.Controller` | ASP.NET Core | внутренний API платформы (заглушка) + CLI миграций/seed | Core, Infrastructure |
| `AgentPlatform.Scheduler` | Worker | планировщик сессий (заглушка) | Core, Infrastructure |
| `AgentPlatform.Tests` | xUnit | tenant-изоляция, инварианты сохранения, контракты Core | все |

## 2. Требования

- .NET SDK 10.0.x
- Docker + Docker Compose (postgres 17 + pgvector, redis 7)
- `dotnet-ef` 10 (`dotnet tool install --global dotnet-ef`)

## 3. Переменные окружения (единственный источник строк подключения и секретов)

| Переменная | Где используется | Назначение |
|---|---|---|
| `ConnectionStrings__Postgres` | TenantApp, Controller, Knowledge, Scheduler, `dotnet ef` | строка подключения к PostgreSQL |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_HOST`, `POSTGRES_PORT` | docker compose / dev-фолбэк | параметры локальной БД |
| `Redis__ConnectionString` | Gateway | Redis (кеши, rate-limit) |
| `AGENTPLATFORM_SEED_ADMIN_PASSWORD` | `--seed` | пароль admin-пользователя (обязателен) |
| `AGENTPLATFORM_SEED_ADMIN_EMAIL` | `--seed` | email admin (по умолчанию `admin@demo.local`) |
| `AGENTPLATFORM_SEED_TENANT_NAME` | `--seed` | имя тестового тенанта (по умолчанию `Demo Tenant`) |
| `AGENTPLATFORM_DB_DETAILED_ERRORS` | TenantApp, Controller | подробные ошибки Npgsql (только для разработки) |

В `appsettings.json` / `appsettings.Development.json` строк подключения и секретов нет — только логирование.

## 4. Запуск инфраструктуры и сервисов

```powershell
cd src
Copy-Item .env.example .env    # задать POSTGRES_PASSWORD и AGENTPLATFORM_SEED_ADMIN_PASSWORD
docker compose up --build
```

| Сервис | URL |
|---|---|
| Gateway | http://localhost:8080/health |
| Controller | http://localhost:8081/health |
| Tenant App | http://localhost:8082/health |
| Connector Host (MCP) | http://localhost:8083/health, MCP на `http://localhost:8083/` |
| Knowledge | http://localhost:8084/health |
| PostgreSQL | localhost:5432 |
| Redis | localhost:6379 |

Одноразовый сервис `migrate` накатывает миграции и выполняет seed; `tenant-app` стартует
только после его успешного завершения (`service_completed_successfully`).

## 5. Миграции EF Core

```powershell
cd src
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=agentplatform;Username=agentplatform;Password=<пароль>"

# накатить на пустую БД
dotnet ef database update --project AgentPlatform.Infrastructure

# проверка без БД: генерация SQL
dotnet ef migrations script --project AgentPlatform.Infrastructure --output migration.sql
```

Строка подключения берётся из env; при локальном запуске без env используется dev-фолбэк
на `localhost:5432` с дефолтами docker compose (см. `ConnectionStringProvider`).

## 6. Проверочный seed

```powershell
cd src
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=agentplatform;Username=agentplatform;Password=<пароль>"
$env:AGENTPLATFORM_SEED_ADMIN_PASSWORD = "<пароль admin>"

# миграции + seed (идемпотентно)
dotnet run --project AgentPlatform.TenantApp -- --migrate --seed

# только seed
dotnet run --project AgentPlatform.Controller -- --seed
```

Проверка:

```powershell
curl http://localhost:8081/api/v1/internal/tenants
curl http://localhost:8081/api/v1/internal/tenants/<tenantId>/users
```

или в psql:

```sql
select id, name, plan, status from tenants;
select tenant_id, email, role, left(password_hash, 14) from users;
```

Повторный запуск seed не создаёт дублей (тенант ищется по имени, пользователь — по `(tenant_id, email)`).

## 7. Тесты

```powershell
cd src
dotnet build AgentPlatform.sln
dotnet test AgentPlatform.Tests
```

Покрыто: фильтрация по tenant_id, исключение без tenant-контекста, запрет cross-tenant вставок,
глобальные таблицы без фильтра, append-only `tool_audit`, REQUIRED-валидация `gate_payload` и
`rules`, идемпотентность seed, сериализация StateContract, токены enum для БД.

## 8. Решения, принятые сверх задания

1. **Отдельный проект `AgentPlatform.Infrastructure`** (EF Core, репозитории, seed). В списке проектов
   задания слоя персистентности не было, а DbContext нужен минимум трём сервисам — общий проект
   дешевле дублирования и сохраняет правило «Core без EF».
2. **`bigint GENERATED BY DEFAULT AS IDENTITY` вместо `bigserial`** — современный эквивалент,
   который генерирует EF Core; `approvals.checkpoint_id` объявлен в DDL как `bigserial`, но это
   nullable FK — sequence на нём давал бы мусорные значения, поэтому столбец `bigint NULL`.
3. **pgvector сырым SQL в миграции** (`CREATE EXTENSION vector`, `embedding vector(1024)`,
   ivfflat-индекс): пакет `Pgvector` не входит в разрешённый список, столбец в EF-модель не маппится.
4. **Дублирующие индексы объединены**: в DDL рядом стоят `UNIQUE (agent_id, version)` + `ix_av_agent`
   и `UNIQUE (session_id, superstep)` + `ix_cp_session (… DESC)` — оставлено по одному уникальному
   индексу (второй дублирует доступ по тем же столбцам).
5. **Добавлены tenant-first индексы, которых нет в DDL**: `messages (tenant_id, ts)`,
   `checkpoints (tenant_id, session_id)`, `scheduled_runs (tenant_id, enabled)` — требование
   «первый столбец составных индексов — tenant_id» для всех tenant-таблиц.
6. **jsonb-поля в EF-модели — строки** (`HasColumnType("jsonb")`) с типизированными контрактами и
   валидаторами в Core; схемы проверяются кодом, а не CHECK-ограничениями (как требует задание).
7. **snake_case-конвенция имён столбцов** реализована в `OnModelCreating` (без пакета
   `EFCore.NamingConventions`), чтобы DDL и миграции совпадали один-в-один.
8. **Пароли — PBKDF2-HMAC-SHA256** (210k итераций, формат `PBKDF2-SHA256$iter$salt$hash`):
   ASP.NET Identity / BCrypt не входят в разрешённый список библиотек.
9. **Тесты данных на `Microsoft.EntityFrameworkCore.InMemory`** — integration-тесты на реальном
   PostgreSQL появятся отдельной задачей; InMemory добавлен только в тестовый проект.
10. **Заглушка tenant-контекста через заголовок `X-Tenant-Id`** (TenantApp) с TODO на JWT:
    требования к аутентификации в задаче не описаны.
11. **Один Dockerfile на все сервисы** (`build.args.PROJECT`) вместо девяти почти одинаковых файлов.
12. **`tool_settings` и `agent_versions` не входят в `TenantScopedRepository`**: в DDL у них нет
    `tenant_id`, изоляция — через `connector_installations` / `agents` join (появится с Policy Engine).
13. **Инварианты вместо CHECK**: `tool_audit` append-only, `tenant_id` обязателен на вставке,
    `gate_payload` и `rules` валидируются в `AgentPlatformDbContext.SaveChanges`.
14. **Дополнительный сервис `migrate` в compose** — миграции и seed выполняются отдельным
    one-shot контейнером до старта `tenant-app` (никакой гонки миграций).



