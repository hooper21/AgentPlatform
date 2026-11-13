using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPlatform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Контракт IKnowledgeProvider (задача 2.5.1): у источника появляется тип
    /// провайдера, jsonb-конфигурация подключения, ссылка на секрет, курсор
    /// инкрементальной синхронизации и cron-расписание синхронизации.
    /// <para>
    /// provider_type для существующих строк остаётся 'legacy': пайплайн находит
    /// провайдера по legacy-колонке type (document | site | faq), поэтому
    /// поведение источников задачи 2.1 не меняется.
    /// </para>
    /// </summary>
    public partial class AddKnowledgeProviderContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "config",
                table: "knowledge_sources",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "credentials_ref",
                table: "knowledge_sources",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_cursor",
                table: "knowledge_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_type",
                table: "knowledge_sources",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "sync_schedule",
                table: "knowledge_sources",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ks_tenant_provider",
                table: "knowledge_sources",
                columns: new[] { "tenant_id", "provider_type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ks_tenant_provider",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "config",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "credentials_ref",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "last_cursor",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "provider_type",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "sync_schedule",
                table: "knowledge_sources");
        }
    }
}
