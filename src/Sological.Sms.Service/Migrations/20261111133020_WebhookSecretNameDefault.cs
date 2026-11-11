using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sological.Sms.Service.Migrations
{
    /// <summary>Data repair (2026-11-11): a channel created through the admin API with a webhook URL got no
    /// secret name, so none of its events could be signed and every one deferred (Azure dev's AIWorkforce
    /// channel). Fills in the naming convention, `SologicalSms:Webhook:{key}` — the admin API now does the
    /// same on create and update. No schema change.</summary>
    public partial class WebhookSecretNameDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE channels
                SET webhook_secret_name = 'SologicalSms:Webhook:' || key
                WHERE webhook_url IS NOT NULL AND webhook_url <> ''
                  AND (webhook_secret_name IS NULL OR webhook_secret_name = '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A repair is not undone: which rows it filled is not recorded, and an empty name only breaks signing.
        }
    }
}
