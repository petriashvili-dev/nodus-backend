using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nodus.Api.Infrastructure.Persistence.Migrations;

/// <summary>Normalise machine keys only; content, provider identifiers and cached responses remain byte-identical.</summary>
public partial class SnakeCaseStorageKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_users_plan", table: "users");
        migrationBuilder.Sql("""
                UPDATE users SET plan = CASE plan WHEN 'Standard' THEN 'standard' WHEN 'Plus' THEN 'plus' ELSE plan END WHERE plan IN ('Standard', 'Plus');
                UPDATE users SET plan_source = CASE plan_source WHEN 'Manual' THEN 'manual' ELSE plan_source END WHERE plan_source IN ('Manual');
                UPDATE refresh_tokens SET revoked_reason = CASE revoked_reason WHEN 'Rotated' THEN 'rotated' WHEN 'Logout' THEN 'logout' WHEN 'ReuseDetected' THEN 'reuse_detected' ELSE revoked_reason END WHERE revoked_reason IN ('Rotated', 'Logout', 'ReuseDetected');
                UPDATE email_verification_codes SET purpose = CASE purpose WHEN 'Link' THEN 'link' WHEN 'SignIn' THEN 'sign_in' ELSE purpose END WHERE purpose IN ('Link', 'SignIn');
                UPDATE nodes SET type = CASE type WHEN 'OneTime' THEN 'one_time' WHEN 'Recurring' THEN 'recurring' WHEN 'Anniversary' THEN 'anniversary' ELSE type END WHERE type IN ('OneTime', 'Recurring', 'Anniversary');
                UPDATE node_occurrence_states SET state = CASE state WHEN 'Tied' THEN 'tied' WHEN 'Untied' THEN 'untied' ELSE state END WHERE state IN ('Tied', 'Untied');
                UPDATE sync_changes SET entity_type = CASE entity_type WHEN 'User' THEN 'user' WHEN 'Node' THEN 'node' WHEN 'OccurrenceState' THEN 'occurrence_state' WHEN 'Connection' THEN 'connection' WHEN 'AiDraftJob' THEN 'ai_draft_job' WHEN 'Suggestion' THEN 'suggestion' WHEN 'ImportJob' THEN 'import_job' ELSE entity_type END WHERE entity_type IN ('User', 'Node', 'OccurrenceState', 'Connection', 'AiDraftJob', 'Suggestion', 'ImportJob');
                UPDATE sync_changes SET operation = CASE operation WHEN 'Upsert' THEN 'upsert' WHEN 'Delete' THEN 'delete' ELSE operation END WHERE operation IN ('Upsert', 'Delete');
                UPDATE devices SET apns_environment = CASE apns_environment WHEN 'Unknown' THEN 'unknown' WHEN 'Sandbox' THEN 'sandbox' WHEN 'Production' THEN 'production' ELSE apns_environment END WHERE apns_environment IN ('Unknown', 'Sandbox', 'Production');
                UPDATE connections SET status = CASE status WHEN 'Pending' THEN 'pending' WHEN 'Active' THEN 'active' WHEN 'Removed' THEN 'removed' ELSE status END WHERE status IN ('Pending', 'Active', 'Removed');
                UPDATE connection_invites SET status = CASE status WHEN 'Pending' THEN 'pending' WHEN 'Accepted' THEN 'accepted' WHEN 'Revoked' THEN 'revoked' ELSE status END WHERE status IN ('Pending', 'Accepted', 'Revoked');
                UPDATE ai_draft_jobs SET status = CASE status WHEN 'Queued' THEN 'queued' WHEN 'Processing' THEN 'processing' WHEN 'Ready' THEN 'ready' WHEN 'Confirmed' THEN 'confirmed' WHEN 'Failed' THEN 'failed' WHEN 'Cancelled' THEN 'cancelled' WHEN 'Expired' THEN 'expired' ELSE status END WHERE status IN ('Queued', 'Processing', 'Ready', 'Confirmed', 'Failed', 'Cancelled', 'Expired');
                UPDATE node_suggestions SET status = CASE status WHEN 'Ready' THEN 'ready' WHEN 'Accepted' THEN 'accepted' WHEN 'Dismissed' THEN 'dismissed' WHEN 'Skipped' THEN 'skipped' WHEN 'Stale' THEN 'stale' ELSE status END WHERE status IN ('Ready', 'Accepted', 'Dismissed', 'Skipped', 'Stale');
                UPDATE node_suggestions SET origin = CASE origin WHEN 'Template' THEN 'template' WHEN 'Cache' THEN 'cache' WHEN 'Llm' THEN 'llm' ELSE origin END WHERE origin IN ('Template', 'Cache', 'Llm');
                UPDATE import_jobs SET status = CASE status WHEN 'Queued' THEN 'queued' WHEN 'Processing' THEN 'processing' WHEN 'Ready' THEN 'ready' WHEN 'Completed' THEN 'completed' WHEN 'Failed' THEN 'failed' WHEN 'Cancelled' THEN 'cancelled' WHEN 'Expired' THEN 'expired' ELSE status END WHERE status IN ('Queued', 'Processing', 'Ready', 'Completed', 'Failed', 'Cancelled', 'Expired');
                UPDATE outbox_events SET type = CASE type WHEN 'sync.changed' THEN 'sync_changed' WHEN 'push.requested' THEN 'push_requested' WHEN 'ai.draft.requested' THEN 'ai_draft_requested' WHEN 'ai.continuation.requested' THEN 'ai_continuation_requested' WHEN 'import.requested' THEN 'import_requested' ELSE type END;
                UPDATE ai_draft_jobs SET last_error = lower(regexp_replace(regexp_replace(last_error, '([A-Z]+)([A-Z][a-z])', '\1_\2', 'g'), '([a-z0-9])([A-Z])', '\1_\2', 'g')) WHERE last_error IS NOT NULL;
                UPDATE import_jobs SET last_error = lower(regexp_replace(regexp_replace(last_error, '([A-Z]+)([A-Z][a-z])', '\1_\2', 'g'), '([a-z0-9])([A-Z])', '\1_\2', 'g')) WHERE last_error IS NOT NULL;
                UPDATE outbox_events SET last_error = lower(regexp_replace(regexp_replace(last_error, '([A-Z]+)([A-Z][a-z])', '\1_\2', 'g'), '([a-z0-9])([A-Z])', '\1_\2', 'g')) WHERE last_error IS NOT NULL;
                ALTER TABLE users ALTER COLUMN plan SET DEFAULT 'standard';
                """);
        migrationBuilder.AddCheckConstraint(name: "ck_users_plan", table: "users",
            sql: "\"plan\" IN ('standard', 'plus')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_users_plan", table: "users");
        migrationBuilder.Sql("""
                UPDATE users SET plan = CASE plan WHEN 'standard' THEN 'Standard' WHEN 'plus' THEN 'Plus' ELSE plan END WHERE plan IN ('standard', 'plus');
                UPDATE users SET plan_source = CASE plan_source WHEN 'manual' THEN 'Manual' ELSE plan_source END WHERE plan_source IN ('manual');
                UPDATE refresh_tokens SET revoked_reason = CASE revoked_reason WHEN 'rotated' THEN 'Rotated' WHEN 'logout' THEN 'Logout' WHEN 'reuse_detected' THEN 'ReuseDetected' ELSE revoked_reason END WHERE revoked_reason IN ('rotated', 'logout', 'reuse_detected');
                UPDATE email_verification_codes SET purpose = CASE purpose WHEN 'link' THEN 'Link' WHEN 'sign_in' THEN 'SignIn' ELSE purpose END WHERE purpose IN ('link', 'sign_in');
                UPDATE nodes SET type = CASE type WHEN 'one_time' THEN 'OneTime' WHEN 'recurring' THEN 'Recurring' WHEN 'anniversary' THEN 'Anniversary' ELSE type END WHERE type IN ('one_time', 'recurring', 'anniversary');
                UPDATE node_occurrence_states SET state = CASE state WHEN 'tied' THEN 'Tied' WHEN 'untied' THEN 'Untied' ELSE state END WHERE state IN ('tied', 'untied');
                UPDATE sync_changes SET entity_type = CASE entity_type WHEN 'user' THEN 'User' WHEN 'node' THEN 'Node' WHEN 'occurrence_state' THEN 'OccurrenceState' WHEN 'connection' THEN 'Connection' WHEN 'ai_draft_job' THEN 'AiDraftJob' WHEN 'suggestion' THEN 'Suggestion' WHEN 'import_job' THEN 'ImportJob' ELSE entity_type END WHERE entity_type IN ('user', 'node', 'occurrence_state', 'connection', 'ai_draft_job', 'suggestion', 'import_job');
                UPDATE sync_changes SET operation = CASE operation WHEN 'upsert' THEN 'Upsert' WHEN 'delete' THEN 'Delete' ELSE operation END WHERE operation IN ('upsert', 'delete');
                UPDATE devices SET apns_environment = CASE apns_environment WHEN 'unknown' THEN 'Unknown' WHEN 'sandbox' THEN 'Sandbox' WHEN 'production' THEN 'Production' ELSE apns_environment END WHERE apns_environment IN ('unknown', 'sandbox', 'production');
                UPDATE connections SET status = CASE status WHEN 'pending' THEN 'Pending' WHEN 'active' THEN 'Active' WHEN 'removed' THEN 'Removed' ELSE status END WHERE status IN ('pending', 'active', 'removed');
                UPDATE connection_invites SET status = CASE status WHEN 'pending' THEN 'Pending' WHEN 'accepted' THEN 'Accepted' WHEN 'revoked' THEN 'Revoked' ELSE status END WHERE status IN ('pending', 'accepted', 'revoked');
                UPDATE ai_draft_jobs SET status = CASE status WHEN 'queued' THEN 'Queued' WHEN 'processing' THEN 'Processing' WHEN 'ready' THEN 'Ready' WHEN 'confirmed' THEN 'Confirmed' WHEN 'failed' THEN 'Failed' WHEN 'cancelled' THEN 'Cancelled' WHEN 'expired' THEN 'Expired' ELSE status END WHERE status IN ('queued', 'processing', 'ready', 'confirmed', 'failed', 'cancelled', 'expired');
                UPDATE node_suggestions SET status = CASE status WHEN 'ready' THEN 'Ready' WHEN 'accepted' THEN 'Accepted' WHEN 'dismissed' THEN 'Dismissed' WHEN 'skipped' THEN 'Skipped' WHEN 'stale' THEN 'Stale' ELSE status END WHERE status IN ('ready', 'accepted', 'dismissed', 'skipped', 'stale');
                UPDATE node_suggestions SET origin = CASE origin WHEN 'template' THEN 'Template' WHEN 'cache' THEN 'Cache' WHEN 'llm' THEN 'Llm' ELSE origin END WHERE origin IN ('template', 'cache', 'llm');
                UPDATE import_jobs SET status = CASE status WHEN 'queued' THEN 'Queued' WHEN 'processing' THEN 'Processing' WHEN 'ready' THEN 'Ready' WHEN 'completed' THEN 'Completed' WHEN 'failed' THEN 'Failed' WHEN 'cancelled' THEN 'Cancelled' WHEN 'expired' THEN 'Expired' ELSE status END WHERE status IN ('queued', 'processing', 'ready', 'completed', 'failed', 'cancelled', 'expired');
                UPDATE outbox_events SET type = CASE type WHEN 'sync_changed' THEN 'sync.changed' WHEN 'push_requested' THEN 'push.requested' WHEN 'ai_draft_requested' THEN 'ai.draft.requested' WHEN 'ai_continuation_requested' THEN 'ai.continuation.requested' WHEN 'import_requested' THEN 'import.requested' ELSE type END;
                ALTER TABLE users ALTER COLUMN plan SET DEFAULT 'Standard';
                """);
        migrationBuilder.AddCheckConstraint(name: "ck_users_plan", table: "users",
            sql: "\"plan\" IN ('Standard', 'Plus')");
        // Historical diagnostic text stays normalised; it does not affect reads by the previous application.
    }
}
