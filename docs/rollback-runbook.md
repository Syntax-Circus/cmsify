# Rollback runbook

Use this when a deployed Cmsify version needs to be rolled back.

## Rolling back a deployment

1. Identify the last known-good version tag (the one running before the problematic deploy).
2. Update `CMSIFY_VERSION` in your `.env`/`.env.prod` to that prior version.
3. Pull and restart: `docker compose --env-file .env.prod -f docker-compose.prod.yml pull && docker compose --env-file .env.prod -f docker-compose.prod.yml up -d`.
4. Verify `/health/live`, `/health/ready`, Admin sign-in, and a representative content read before considering the rollback complete.

## Data considerations

Rolling back the application version does not roll back the database. If the problematic release included a breaking database migration, rolling back the image alone is not sufficient — restore the matched database (and media, if applicable) backup taken before that migration ran. Never restore only the database or only media independently; a mismatch between them can leave content pointing at missing or incorrect files.

If no backup was taken before the problematic release, do not attempt to reverse a data migration by hand. Investigate the specific migration's `Down()` method (if one exists) and treat this as an incident, not a routine rollback.
