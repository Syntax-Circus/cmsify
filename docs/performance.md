# Performance notes

## Resolved content listing

The resolved-content page query was inspected with PostgreSQL 17 using 500 content items and 2,500 published versions. The dataset included five templates, overlapping bounded and unbounded versions, two locales, tags, translation groups, and deleted owners. `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for a 100-row page reported 33.405 ms execution time with all 2,500 correlated winner probes using the existing content-version indexes.

The small template tables and 500-row owner table used sequential scans; content-version selection did not. These measurements are diagnostic rather than a timing gate. Index decision: **existing indexes retained**.
