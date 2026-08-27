BEGIN;

DO $cmsify_fixture$
DECLARE
  expected text[] := ARRAY[
    '20260517174817_InitialSchema',
    '20260517194907_AddUserSessions',
    '20260517222010_AddUserTheme',
    '20260518140420_AddWorkspaceAccessGrants',
    '20260519120338_AddContentVersions',
    '20260519230251_AddPickLists',
    '20260602135111_AddContentVersionEffectiveRanges',
    '20260820151206_AddComponentsAndPickListRevisions',
    '20260820172030_AddWebhookDeliveryLeases',
    '20260820172346_AddApiClientTokenIdentifiers',
    '20260821005219_AddPackageProvenanceToReusableModels'
  ];
  actual text[];
BEGIN
  SELECT array_agg("MigrationId" ORDER BY "MigrationId")
  INTO actual
  FROM "__EFMigrationsHistory";

  IF actual IS DISTINCT FROM expected THEN
    RAISE EXCEPTION 'v0.1.3 fixture seed requires the exact 11 published baseline migrations';
  END IF;
END
$cmsify_fixture$;

DELETE FROM user_sessions;
DELETE FROM audit_logs;
DELETE FROM webhook_delivery_logs;

UPDATE workspaces
SET created_at = '2026-08-20T12:00:00Z',
    updated_at = '2026-08-20T12:00:00Z';

UPDATE users
SET password_hash = crypt('Cmsify-fixture-user-only-0.1.3!', '$2a$04$abcdefghijklmnopqrstuu'),
    must_change_password = false,
    last_login_at = CASE WHEN id = :'admin_user'::uuid THEN '2026-08-20T12:00:30Z'::timestamptz ELSE NULL END,
    created_at = '2026-08-20T12:00:00Z',
    updated_at = '2026-08-20T12:00:00Z';

UPDATE pick_lists
SET package_namespace = 'fixture.synthetic',
    package_id = 'moving-baseline',
    package_version = '0.1.3',
    created_at = '2026-08-20T12:01:00Z',
    updated_at = '2026-08-20T12:02:00Z'
WHERE id = :'choice_set'::uuid;

UPDATE pick_list_revisions
SET created_at = CASE version_number
  WHEN 1 THEN '2026-08-20T12:01:00Z'::timestamptz
  ELSE '2026-08-20T12:02:00Z'::timestamptz
END
WHERE pick_list_id = :'choice_set'::uuid;

UPDATE components
SET package_namespace = 'fixture.synthetic',
    package_id = 'moving-baseline',
    package_version = '0.1.3',
    created_at = '2026-08-20T12:03:00Z',
    updated_at = '2026-08-20T12:04:00Z'
WHERE id = :'component'::uuid;

UPDATE component_versions
SET created_at = '2026-08-20T12:03:00Z',
    updated_at = '2026-08-20T12:04:00Z',
    published_at = '2026-08-20T12:04:00Z'
WHERE component_id = :'component'::uuid;

UPDATE templates
SET created_at = '2026-08-20T12:05:00Z',
    updated_at = '2026-08-20T12:06:00Z'
WHERE id = :'template'::uuid;

UPDATE template_versions
SET created_at = '2026-08-20T12:05:00Z',
    updated_at = '2026-08-20T12:06:00Z',
    published_at = '2026-08-20T12:06:00Z'
WHERE template_id = :'template'::uuid;

UPDATE content_items
SET created_at = CASE id
      WHEN :'draft_content'::uuid THEN '2026-08-20T12:07:00Z'::timestamptz
      WHEN :'published_content'::uuid THEN '2026-08-20T12:08:00Z'::timestamptz
      WHEN :'scheduled_content'::uuid THEN '2026-08-20T12:09:00Z'::timestamptz
      ELSE '2026-08-20T12:10:00Z'::timestamptz
    END,
    updated_at = CASE id
      WHEN :'draft_content'::uuid THEN '2026-08-20T12:07:00Z'::timestamptz
      WHEN :'published_content'::uuid THEN '2026-08-20T12:08:30Z'::timestamptz
      WHEN :'scheduled_content'::uuid THEN '2026-08-20T12:09:30Z'::timestamptz
      ELSE '2026-08-20T12:10:30Z'::timestamptz
    END,
    published_at = CASE
      WHEN id IN (:'published_content'::uuid, :'expired_content'::uuid) THEN '2026-08-20T12:08:30Z'::timestamptz
      ELSE NULL
    END,
    publish_at = CASE
      WHEN id = :'scheduled_content'::uuid THEN '2026-09-20T12:00:00Z'::timestamptz
      ELSE NULL
    END,
    pending_effective_start_at = NULL,
    pending_effective_end_at = NULL,
    archived_at = NULL;

UPDATE content_versions
SET published_at = CASE content_item_id
      WHEN :'published_content'::uuid THEN '2026-08-20T12:08:30Z'::timestamptz
      ELSE '2026-08-20T12:10:30Z'::timestamptz
    END,
    retired_at = NULL
WHERE content_item_id IN (:'published_content'::uuid, :'expired_content'::uuid);

UPDATE media_assets
SET file_name = 'fixture.txt',
    mime_type = 'text/plain',
    size_bytes = 30,
    storage_key = 'cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1-fixture.txt',
    storage_provider = 's3',
    alt_text = 'Deterministic text fixture',
    created_at = '2026-08-20T12:11:00Z',
    updated_at = '2026-08-20T12:11:00Z'
WHERE id = :'text_media'::uuid;

UPDATE media_assets
SET file_name = 'pixel.png',
    mime_type = 'image/png',
    size_bytes = 69,
    storage_key = 'cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2-pixel.png',
    storage_provider = 's3',
    alt_text = 'Deterministic one pixel image',
    created_at = '2026-08-20T12:12:00Z',
    updated_at = '2026-08-20T12:12:30Z',
    is_deleted = true,
    deleted_at = '2026-08-20T12:12:30Z',
    deleted_by_user_id = :'admin_user'::uuid
WHERE id = :'image_media'::uuid;

UPDATE api_clients
SET token_hash = crypt(:'reader_token', '$2a$04$abcdefghijklmnopqrstuu'),
    token_identifier = 'fixture-reader',
    role = 'Reader',
    workspace_id = :'primary_workspace'::uuid,
    is_active = true,
    expires_at = NULL,
    last_used_at = NULL,
    created_at = '2026-08-20T12:13:00Z',
    updated_at = '2026-08-20T12:13:00Z'
WHERE id = :'reader_client'::uuid;

UPDATE webhook_endpoints
SET url = 'https://fixture-webhook.example.test/cmsify-upgrade-fixture',
    secret = 'v1.AAECAwQFBgcICQoL.BFBpi/UrF42vy+zL9I8lnA==.0pqpkko3sHX/wlWOkRiRGAJYDcLrmg==',
    is_active = false,
    created_at = '2026-08-20T12:14:00Z',
    updated_at = '2026-08-20T12:14:00Z'
WHERE id = :'webhook'::uuid;

INSERT INTO webhook_delivery_logs (
  id, webhook_endpoint_id, event_type, payload, attempt_count, last_attempt_at,
  next_retry_at, status_code, is_delivered, is_failed, lease_expires_at, created_at
)
VALUES (
  'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2', :'webhook'::uuid, 'content.published',
  jsonb_build_object('contentItemId', :'published_content'::text, 'synthetic', true),
  10, '2026-08-20T12:15:00Z', NULL, 503, false, true, NULL, '2026-08-20T12:14:30Z'
);

INSERT INTO audit_logs (
  id, entity_type, entity_id, action, actor_user_id, actor_api_client_id,
  timestamp, change_delta, workspace_id
)
VALUES (
  'cccccccc-cccc-4ccc-8ccc-ccccccccccc1', 'ContentItem', :'published_content'::uuid,
  'StatusChanged', :'admin_user'::uuid, NULL, '2026-08-20T12:08:30Z',
  jsonb_build_object('correlationId', 'fixture-correlation-001', 'from', 'Approved', 'to', 'Published'),
  :'primary_workspace'::uuid
);

COMMIT;
