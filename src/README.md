КОНТЕКСТ ПРОЕКТА
Продукт: Agent Platform — мультиарендная платформа для создания, хостинга
и эксплуатации AI-агентов. Платформа НЕ содержит бизнес-логики решений:
бизнес-сценарии (например, «агент продаж») собираются из её примитивов
как Solution Profiles (шаблоны конфигураций). Документ проекта: v2.1.

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

ИСТОЧНИКИ ЗНАНИЙ:
- Инжест-пайплайн универсален: приёмник контента описан контрактом
  IKnowledgeProvider (отдаёт поток страниц/документов с метаданными
  и инкрементальными курсорами). Встроенные типы document | site | faq —
  первые реализации контракта.
- Провайдер внешней системы знаний = плагин: способ подключения
  (OAuth / API-ключ), тип синхронизации (полная + инкремент), вебхуки.
  Права доступа НЕ переносятся в чат: индексируется то, к чему есть
  доступ у подключённого токена; предупреждение показывается при установке.
- RAG-приоритет (живые данные из тулов главнее знаний) и анти-инъекция
  не зависят от типа источника.

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
  IPolicyEngine, IKnowledgeProvider, ConnectorManifest. Расширение контракта —
  отдельная задача, не попутная правка.
- После каждой задачи выводи: список изменённых файлов, как запустить,
  как проверить.
