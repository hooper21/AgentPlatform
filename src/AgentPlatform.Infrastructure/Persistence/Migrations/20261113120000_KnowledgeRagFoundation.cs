using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Знания (задача 2.3): таблица rag_misses для метрики «вопросы без ответа»
    /// и нормализация размерности knowledge_chunks.embedding под MVP-эмбеддер.
    /// <para>
    /// Размерность эмбеддинга задаётся моделью (Ollama nomic-embed-text — 768).
    /// Столбец, созданный в фазе 1 миграцией как vector(1024), приводится к
    /// vector(768). Это осознанная смена схемы, а НЕ «подгонка» данных: при
    /// несовпадении размерности сервис обязан бросить исключение, поэтому
    /// столбец пересоздаётся, а старые векторы не обрезаются и не дополняются.
    /// </para>
    /// </summary>
    [DbContext(typeof(Persistence.AgentPlatformDbContext))]
    [Migration("20261113120000_KnowledgeRagFoundation")]
    public partial class KnowledgeRagFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pgvector: нормализуем размерность столбца до 768 (nomic-embed-text).
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    current_dim integer;
                BEGIN
                    SELECT atttypmod INTO current_dim
                    FROM pg_attribute
                    WHERE attrelid = 'knowledge_chunks'::regclass
                      AND attname = 'embedding'
                      AND NOT attisdropped;

                    IF current_dim IS NULL THEN
                        ALTER TABLE knowledge_chunks ADD COLUMN embedding vector(768);
                    ELSIF current_dim <> 768 THEN
                        DROP INDEX IF EXISTS ix_kc_embedding;
                        ALTER TABLE knowledge_chunks DROP COLUMN embedding;
                        ALTER TABLE knowledge_chunks ADD COLUMN embedding vector(768);
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_kc_embedding ON knowledge_chunks USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);");

            // Запросы, по которым RAG не нашёл ответа: источник метрики «вопросы без ответа».
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS rag_misses (
                    id bigserial PRIMARY KEY,
                    tenant_id uuid NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
                    query text NOT NULL,
                    ts timestamptz NOT NULL DEFAULT now()
                );
                """);

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_rag_misses_tenant_ts ON rag_misses (tenant_id, ts);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS rag_misses;");

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    current_dim integer;
                BEGIN
                    SELECT atttypmod INTO current_dim
                    FROM pg_attribute
                    WHERE attrelid = 'knowledge_chunks'::regclass
                      AND attname = 'embedding'
                      AND NOT attisdropped;

                    IF current_dim = 768 THEN
                        DROP INDEX IF EXISTS ix_kc_embedding;
                        ALTER TABLE knowledge_chunks DROP COLUMN embedding;
                        ALTER TABLE knowledge_chunks ADD COLUMN embedding vector(1024);
                        CREATE INDEX ix_kc_embedding ON knowledge_chunks USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
                    END IF;
                END
                $$;
                """);
        }
    }
}
