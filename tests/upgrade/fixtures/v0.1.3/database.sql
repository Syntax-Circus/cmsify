--
-- PostgreSQL database dump
--

\restrict cmsifyupgradefixturev013


SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: SCHEMA "public"; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON SCHEMA "public" IS 'standard public schema';


--
-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "public";


--
-- Name: EXTENSION "pgcrypto"; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON EXTENSION "pgcrypto" IS 'cryptographic functions';


SET default_tablespace = '';

SET default_table_access_method = "heap";

--
-- Name: __EFMigrationsHistory; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL
);


--
-- Name: api_clients; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."api_clients" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "name" character varying(200) NOT NULL,
    "description" character varying(1000),
    "token_hash" character varying(500) NOT NULL,
    "role" character varying(50) NOT NULL,
    "workspace_id" "uuid",
    "is_active" boolean NOT NULL,
    "expires_at" timestamp with time zone,
    "created_by_user_id" "uuid" NOT NULL,
    "last_used_at" timestamp with time zone,
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid",
    "token_identifier" character varying(64)
);


--
-- Name: audit_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."audit_logs" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "entity_type" character varying(200) NOT NULL,
    "entity_id" "uuid" NOT NULL,
    "action" character varying(50) NOT NULL,
    "actor_user_id" "uuid",
    "actor_api_client_id" "uuid",
    "timestamp" timestamp with time zone NOT NULL,
    "change_delta" "jsonb",
    "workspace_id" "uuid"
);


--
-- Name: component_fields; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."component_fields" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "component_version_id" "uuid" NOT NULL,
    "key" character varying(100) NOT NULL,
    "label" character varying(200) NOT NULL,
    "help_text" character varying(1000),
    "order" integer NOT NULL,
    "is_required" boolean NOT NULL,
    "min_occurrences" integer NOT NULL,
    "max_occurrences" integer,
    "primitive_type" character varying(50),
    "nested_component_id" "uuid",
    "field_config" "jsonb"
);


--
-- Name: component_versions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."component_versions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "component_id" "uuid" NOT NULL,
    "version_number" integer NOT NULL,
    "status" character varying(50) NOT NULL,
    "published_at" timestamp with time zone,
    "notes" character varying(2000),
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Name: components; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."components" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "name" character varying(200) NOT NULL,
    "slug" character varying(200) NOT NULL,
    "description" character varying(1000),
    "current_version_id" "uuid",
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid",
    "package_id" character varying(200),
    "package_namespace" character varying(200),
    "package_version" character varying(50)
);


--
-- Name: content_field_values; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."content_field_values" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "content_item_id" "uuid" NOT NULL,
    "field_id" "uuid" NOT NULL,
    "order" integer NOT NULL,
    "value_kind" character varying(50) NOT NULL,
    "text_value" "text",
    "bool_value" boolean,
    "media_asset_id" "uuid",
    "file_asset_id" "uuid",
    "child_content_item_id" "uuid",
    "json_value" "jsonb"
);


--
-- Name: content_item_tags; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."content_item_tags" (
    "content_item_id" "uuid" NOT NULL,
    "tag_id" "uuid" NOT NULL
);


--
-- Name: content_items; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."content_items" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "template_version_id" "uuid" NOT NULL,
    "status" character varying(50) NOT NULL,
    "slug" character varying(200),
    "locale_code" character varying(20),
    "translation_group_id" "uuid",
    "publish_at" timestamp with time zone,
    "published_at" timestamp with time zone,
    "archived_at" timestamp with time zone,
    "search_vector" "tsvector",
    "created_by_user_id" "uuid",
    "updated_by_user_id" "uuid",
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid",
    "pending_effective_end_at" timestamp with time zone,
    "pending_effective_start_at" timestamp with time zone
);


--
-- Name: content_version_field_values; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."content_version_field_values" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "content_version_id" "uuid" NOT NULL,
    "field_id" "uuid" NOT NULL,
    "order" integer NOT NULL,
    "value_kind" character varying(50) NOT NULL,
    "text_value" "text",
    "bool_value" boolean,
    "media_asset_id" "uuid",
    "file_asset_id" "uuid",
    "child_content_item_id" "uuid",
    "json_value" "jsonb",
    "display_label" character varying(200)
);


--
-- Name: content_versions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."content_versions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "content_item_id" "uuid" NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "version_number" integer NOT NULL,
    "status" character varying(50) NOT NULL,
    "template_version_id" "uuid" NOT NULL,
    "slug" character varying(200),
    "locale_code" character varying(20),
    "translation_group_id" "uuid",
    "tags" "text"[] NOT NULL,
    "published_at" timestamp with time zone NOT NULL,
    "retired_at" timestamp with time zone,
    "published_by_user_id" "uuid",
    "rolled_back_from_version_number" integer,
    "effective_end_at" timestamp with time zone,
    "effective_start_at" timestamp with time zone,
    CONSTRAINT "ck_content_versions_effective_range" CHECK (((("effective_start_at" IS NULL) AND ("effective_end_at" IS NULL)) OR (("effective_start_at" IS NOT NULL) AND ("effective_end_at" IS NOT NULL) AND ("effective_start_at" < "effective_end_at"))))
);


--
-- Name: media_assets; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."media_assets" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "file_name" character varying(255) NOT NULL,
    "mime_type" character varying(255) NOT NULL,
    "size_bytes" bigint NOT NULL,
    "storage_key" character varying(1000) NOT NULL,
    "storage_provider" character varying(50) NOT NULL,
    "alt_text" character varying(500),
    "created_by_user_id" "uuid",
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Name: pick_list_options; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."pick_list_options" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "pick_list_id" "uuid" NOT NULL,
    "label" character varying(200) NOT NULL,
    "value" character varying(200) NOT NULL,
    "order" integer NOT NULL
);


--
-- Name: pick_list_revision_options; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."pick_list_revision_options" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "pick_list_revision_id" "uuid" NOT NULL,
    "label" character varying(200) NOT NULL,
    "value" character varying(200) NOT NULL,
    "order" integer NOT NULL
);


--
-- Name: pick_list_revisions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."pick_list_revisions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "pick_list_id" "uuid" NOT NULL,
    "version_number" integer NOT NULL,
    "created_at" timestamp with time zone NOT NULL
);


--
-- Name: pick_lists; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."pick_lists" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "name" character varying(200) NOT NULL,
    "slug" character varying(100) NOT NULL,
    "description" character varying(1000),
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid",
    "current_revision_id" "uuid",
    "package_id" character varying(200),
    "package_namespace" character varying(200),
    "package_version" character varying(50)
);


--
-- Name: tags; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."tags" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "name" character varying(100) NOT NULL,
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Name: template_field_allowed_types; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."template_field_allowed_types" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "field_id" "uuid" NOT NULL,
    "primitive_type" character varying(50),
    "allowed_template_id" "uuid",
    CONSTRAINT "ck_template_field_allowed_types_type_shape" CHECK (((("primitive_type" IS NOT NULL) AND ("allowed_template_id" IS NULL)) OR (("primitive_type" IS NULL) AND ("allowed_template_id" IS NOT NULL))))
);


--
-- Name: template_fields; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."template_fields" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "template_version_id" "uuid" NOT NULL,
    "section_id" "uuid",
    "key" character varying(100) NOT NULL,
    "label" character varying(200) NOT NULL,
    "help_text" character varying(1000),
    "order" integer NOT NULL,
    "is_required" boolean NOT NULL,
    "min_occurrences" integer NOT NULL,
    "max_occurrences" integer,
    "is_open" boolean NOT NULL,
    "composition_mode" character varying(50) NOT NULL,
    "primitive_type" character varying(50),
    "template_id" "uuid",
    "field_config" "jsonb",
    "component_id" "uuid",
    CONSTRAINT "ck_template_fields_type_shape" CHECK (((("is_open" = true) AND ("primitive_type" IS NULL) AND ("template_id" IS NULL) AND ("component_id" IS NULL)) OR (("is_open" = false) AND ((("primitive_type" IS NOT NULL) AND ("template_id" IS NULL) AND ("component_id" IS NULL)) OR (("primitive_type" IS NULL) AND ("template_id" IS NOT NULL) AND ("component_id" IS NULL)) OR (("primitive_type" IS NULL) AND ("template_id" IS NULL) AND ("component_id" IS NOT NULL))))))
);


--
-- Name: template_sections; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."template_sections" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "template_version_id" "uuid" NOT NULL,
    "name" character varying(200) NOT NULL,
    "description" character varying(1000),
    "order" integer NOT NULL,
    "is_collapsible" boolean NOT NULL
);


--
-- Name: template_versions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."template_versions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "template_id" "uuid" NOT NULL,
    "version_number" integer NOT NULL,
    "status" character varying(50) NOT NULL,
    "published_at" timestamp with time zone,
    "created_by_user_id" "uuid",
    "notes" character varying(2000),
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Name: templates; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."templates" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "name" character varying(200) NOT NULL,
    "slug" character varying(100) NOT NULL,
    "description" character varying(1000),
    "package_namespace" character varying(200),
    "package_id" character varying(200),
    "package_version" character varying(50),
    "title_field_key" character varying(100),
    "current_version_id" "uuid",
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Name: user_sessions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."user_sessions" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "user_id" "uuid" NOT NULL,
    "token_hash" character varying(128) NOT NULL,
    "created_at" timestamp with time zone NOT NULL,
    "expires_at" timestamp with time zone NOT NULL,
    "last_seen_at" timestamp with time zone,
    "ip_address" character varying(128)
);


--
-- Name: user_workspace_accesses; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."user_workspace_accesses" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "user_id" "uuid" NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "access_level" character varying(20) NOT NULL
);


--
-- Name: users; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."users" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "email" character varying(320) NOT NULL,
    "display_name" character varying(200) NOT NULL,
    "password_hash" character varying(500) NOT NULL,
    "role" character varying(50) NOT NULL,
    "must_change_password" boolean NOT NULL,
    "time_zone_id" character varying(100),
    "is_active" boolean NOT NULL,
    "last_login_at" timestamp with time zone,
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid",
    "theme" character varying(20),
    "is_super_admin" boolean DEFAULT false NOT NULL
);


--
-- Name: webhook_delivery_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."webhook_delivery_logs" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "webhook_endpoint_id" "uuid" NOT NULL,
    "event_type" character varying(200) NOT NULL,
    "payload" "jsonb" NOT NULL,
    "attempt_count" integer NOT NULL,
    "last_attempt_at" timestamp with time zone,
    "next_retry_at" timestamp with time zone,
    "status_code" integer,
    "is_delivered" boolean NOT NULL,
    "is_failed" boolean NOT NULL,
    "created_at" timestamp with time zone NOT NULL,
    "lease_expires_at" timestamp with time zone
);


--
-- Name: webhook_endpoints; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."webhook_endpoints" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "workspace_id" "uuid" NOT NULL,
    "name" character varying(200) NOT NULL,
    "url" character varying(2000) NOT NULL,
    "secret" character varying(1000) NOT NULL,
    "is_active" boolean NOT NULL,
    "created_by_user_id" "uuid" NOT NULL,
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Name: webhook_subscriptions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."webhook_subscriptions" (
    "webhook_endpoint_id" "uuid" NOT NULL,
    "event_type" character varying(200) NOT NULL
);


--
-- Name: workspaces; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE "public"."workspaces" (
    "id" "uuid" DEFAULT "gen_random_uuid"() NOT NULL,
    "name" character varying(200) NOT NULL,
    "slug" character varying(100) NOT NULL,
    "description" character varying(1000),
    "created_at" timestamp with time zone NOT NULL,
    "updated_at" timestamp with time zone NOT NULL,
    "is_deleted" boolean DEFAULT false NOT NULL,
    "deleted_at" timestamp with time zone,
    "deleted_by_user_id" "uuid"
);


--
-- Data for Name: __EFMigrationsHistory; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;
20260517174817_InitialSchema	10.0.11
20260517194907_AddUserSessions	10.0.11
20260517222010_AddUserTheme	10.0.11
20260518140420_AddWorkspaceAccessGrants	10.0.11
20260519120338_AddContentVersions	10.0.11
20260519230251_AddPickLists	10.0.11
20260602135111_AddContentVersionEffectiveRanges	10.0.11
20260820151206_AddComponentsAndPickListRevisions	10.0.11
20260820172030_AddWebhookDeliveryLeases	10.0.11
20260820172346_AddApiClientTokenIdentifiers	10.0.11
20260821005219_AddPackageProvenanceToReusableModels	10.0.11
\.


--
-- Data for Name: api_clients; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."api_clients" ("id", "name", "description", "token_hash", "role", "workspace_id", "is_active", "expires_at", "created_by_user_id", "last_used_at", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id", "token_identifier") FROM stdin;
33333333-3333-4333-8333-333333333331	Fixture Reader	Synthetic least-privilege upgrade reader	$2a$04$abcdefghijklmnopqrstuuOFSABxGAqA7xSCEBo2xp2pVHh97a6GG	Reader	11111111-1111-4111-8111-111111111111	t	\N	22222222-2222-4222-8222-222222222221	\N	2026-08-20 12:13:00+00	2026-08-20 12:13:00+00	f	\N	\N	fixture-reader
\.


--
-- Data for Name: audit_logs; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."audit_logs" ("id", "entity_type", "entity_id", "action", "actor_user_id", "actor_api_client_id", "timestamp", "change_delta", "workspace_id") FROM stdin;
cccccccc-cccc-4ccc-8ccc-ccccccccccc1	ContentItem	77777777-7777-4777-8777-777777777772	StatusChanged	22222222-2222-4222-8222-222222222221	\N	2026-08-20 12:08:30+00	{"to": "Published", "from": "Approved", "correlationId": "fixture-correlation-001"}	11111111-1111-4111-8111-111111111111
\.


--
-- Data for Name: component_fields; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."component_fields" ("id", "component_version_id", "key", "label", "help_text", "order", "is_required", "min_occurrences", "max_occurrences", "primitive_type", "nested_component_id", "field_config") FROM stdin;
55555555-5555-4555-8555-555555555553	55555555-5555-4555-8555-555555555552	summary	Summary	\N	0	t	1	1	Text	\N	\N
55555555-5555-4555-8555-555555555554	55555555-5555-4555-8555-555555555552	accent	Accent	\N	1	t	1	1	PickList	\N	{"multiple": false, "picklistId": "66666666-6666-4666-8666-666666666661", "picklistRevisionId": "66666666-6666-4666-8666-666666666662"}
\.


--
-- Data for Name: component_versions; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."component_versions" ("id", "component_id", "version_number", "status", "published_at", "notes", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
55555555-5555-4555-8555-555555555552	55555555-5555-4555-8555-555555555551	1	Published	2026-08-20 12:04:00+00	Initial component version	2026-08-20 12:03:00+00	2026-08-20 12:04:00+00	f	\N	\N
\.


--
-- Data for Name: components; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."components" ("id", "workspace_id", "name", "slug", "description", "current_version_id", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id", "package_id", "package_namespace", "package_version") FROM stdin;
55555555-5555-4555-8555-555555555551	11111111-1111-4111-8111-111111111111	Fixture Card	fixture-card	Inline acyclic fixture component	55555555-5555-4555-8555-555555555552	2026-08-20 12:03:00+00	2026-08-20 12:04:00+00	f	\N	\N	moving-baseline	fixture.synthetic	0.1.3
\.


--
-- Data for Name: content_field_values; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."content_field_values" ("id", "content_item_id", "field_id", "order", "value_kind", "text_value", "bool_value", "media_asset_id", "file_asset_id", "child_content_item_id", "json_value") FROM stdin;
0358fd28-ddba-404c-8a48-b31f67c5c4ae	77777777-7777-4777-8777-777777777771	44444444-4444-4444-8444-444444444445	0	Component	\N	\N	\N	\N	\N	{"accent": "alpha", "summary": "Inline draft"}
08dea1aa-a5bf-4a4d-8388-f22c285f729e	77777777-7777-4777-8777-777777777773	44444444-4444-4444-8444-444444444443	0	Text	Fixture scheduled	\N	\N	\N	\N	\N
098c9014-047b-49b1-8c6a-6df29e92a840	77777777-7777-4777-8777-777777777772	44444444-4444-4444-8444-444444444444	0	PickList	alpha	\N	\N	\N	\N	\N
0b2b7511-0126-4814-89e2-30f4efb972d6	77777777-7777-4777-8777-777777777771	44444444-4444-4444-8444-444444444444	0	PickList	alpha	\N	\N	\N	\N	\N
37868eb2-fd52-4ed8-83f4-fd78be592815	77777777-7777-4777-8777-777777777774	44444444-4444-4444-8444-444444444444	0	PickList	alpha	\N	\N	\N	\N	\N
58e9e099-d7c6-4beb-8ec5-0f7d6ce54fd4	77777777-7777-4777-8777-777777777772	44444444-4444-4444-8444-444444444443	0	Text	Fixture published	\N	\N	\N	\N	\N
6407bf0e-806b-4eba-8ae7-c06840c09349	77777777-7777-4777-8777-777777777774	44444444-4444-4444-8444-444444444445	0	Component	\N	\N	\N	\N	\N	{"accent": "alpha", "summary": "Inline expired"}
6dea4e76-102b-4104-83e5-fd44a66fa5c6	77777777-7777-4777-8777-777777777773	44444444-4444-4444-8444-444444444444	0	PickList	alpha	\N	\N	\N	\N	\N
7ee6c629-a6b2-4be9-8da6-ab1a46018bac	77777777-7777-4777-8777-777777777772	44444444-4444-4444-8444-444444444445	0	Component	\N	\N	\N	\N	\N	{"accent": "alpha", "summary": "Inline published"}
8568008f-3bc9-4367-85ed-199d11e4d052	77777777-7777-4777-8777-777777777773	44444444-4444-4444-8444-444444444445	0	Component	\N	\N	\N	\N	\N	{"accent": "alpha", "summary": "Inline scheduled"}
9c4e52a2-d412-44f0-8f9e-4a91fcc130d8	77777777-7777-4777-8777-777777777771	44444444-4444-4444-8444-444444444443	0	Text	Fixture draft	\N	\N	\N	\N	\N
cc92f672-a922-46de-8536-d6b30e9b08e7	77777777-7777-4777-8777-777777777774	44444444-4444-4444-8444-444444444443	0	Text	Fixture expired	\N	\N	\N	\N	\N
\.


--
-- Data for Name: content_item_tags; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."content_item_tags" ("content_item_id", "tag_id") FROM stdin;
\.


--
-- Data for Name: content_items; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."content_items" ("id", "workspace_id", "template_version_id", "status", "slug", "locale_code", "translation_group_id", "publish_at", "published_at", "archived_at", "search_vector", "created_by_user_id", "updated_by_user_id", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id", "pending_effective_end_at", "pending_effective_start_at") FROM stdin;
77777777-7777-4777-8777-777777777771	11111111-1111-4111-8111-111111111111	44444444-4444-4444-8444-444444444442	Draft	fixture-draft	en-US	\N	\N	\N	\N	'alpha':5 'draft':2,4 'fixture':1,3	22222222-2222-4222-8222-222222222221	22222222-2222-4222-8222-222222222221	2026-08-20 12:07:00+00	2026-08-20 12:07:00+00	f	\N	\N	\N	\N
77777777-7777-4777-8777-777777777772	11111111-1111-4111-8111-111111111111	44444444-4444-4444-8444-444444444442	Published	fixture-published	en-US	\N	\N	2026-08-20 12:08:30+00	\N	'alpha':5 'fixture':1,3 'published':2,4	22222222-2222-4222-8222-222222222221	22222222-2222-4222-8222-222222222221	2026-08-20 12:08:00+00	2026-08-20 12:08:30+00	f	\N	\N	\N	\N
77777777-7777-4777-8777-777777777773	11111111-1111-4111-8111-111111111111	44444444-4444-4444-8444-444444444442	Approved	fixture-scheduled	en-US	\N	2026-09-20 12:00:00+00	\N	\N	'alpha':5 'fixture':1,3 'scheduled':2,4	22222222-2222-4222-8222-222222222221	22222222-2222-4222-8222-222222222221	2026-08-20 12:09:00+00	2026-08-20 12:09:30+00	f	\N	\N	\N	\N
77777777-7777-4777-8777-777777777774	11111111-1111-4111-8111-111111111111	44444444-4444-4444-8444-444444444442	Published	fixture-expired	en-US	\N	\N	2026-08-20 12:08:30+00	\N	'alpha':5 'expired':2,4 'fixture':1,3	22222222-2222-4222-8222-222222222221	22222222-2222-4222-8222-222222222221	2026-08-20 12:10:00+00	2026-08-20 12:10:30+00	f	\N	\N	\N	\N
\.


--
-- Data for Name: content_version_field_values; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."content_version_field_values" ("id", "content_version_id", "field_id", "order", "value_kind", "text_value", "bool_value", "media_asset_id", "file_asset_id", "child_content_item_id", "json_value", "display_label") FROM stdin;
16069f05-5b0c-4d9d-8afe-46a287290ca8	77777777-7777-4777-8777-777777777782	44444444-4444-4444-8444-444444444443	0	Text	Fixture expired	\N	\N	\N	\N	\N	\N
4d69bc1e-f16a-40d7-8028-1a558af162e0	77777777-7777-4777-8777-777777777782	44444444-4444-4444-8444-444444444445	0	Component	\N	\N	\N	\N	\N	{"accent": "alpha", "summary": "Inline expired"}	\N
5fd9ba8d-8d3f-437c-862b-97d8e9cd7896	77777777-7777-4777-8777-777777777781	44444444-4444-4444-8444-444444444443	0	Text	Fixture published	\N	\N	\N	\N	\N	\N
67d51e31-2053-4253-87b7-a24fbfdcbe06	77777777-7777-4777-8777-777777777781	44444444-4444-4444-8444-444444444445	0	Component	\N	\N	\N	\N	\N	{"accent": "alpha", "summary": "Inline published"}	\N
aee891de-2ec5-477a-84e7-e3843d68edef	77777777-7777-4777-8777-777777777781	44444444-4444-4444-8444-444444444444	0	PickList	alpha	\N	\N	\N	\N	\N	Alpha (original)
c0fb5dd9-b8ea-4b03-84cd-213a902b78b8	77777777-7777-4777-8777-777777777782	44444444-4444-4444-8444-444444444444	0	PickList	alpha	\N	\N	\N	\N	\N	Alpha (original)
\.


--
-- Data for Name: content_versions; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."content_versions" ("id", "content_item_id", "workspace_id", "version_number", "status", "template_version_id", "slug", "locale_code", "translation_group_id", "tags", "published_at", "retired_at", "published_by_user_id", "rolled_back_from_version_number", "effective_end_at", "effective_start_at") FROM stdin;
77777777-7777-4777-8777-777777777781	77777777-7777-4777-8777-777777777772	11111111-1111-4111-8111-111111111111	1	Published	44444444-4444-4444-8444-444444444442	fixture-published	en-US	\N	{}	2026-08-20 12:08:30+00	\N	22222222-2222-4222-8222-222222222221	\N	\N	\N
77777777-7777-4777-8777-777777777782	77777777-7777-4777-8777-777777777774	11111111-1111-4111-8111-111111111111	1	Published	44444444-4444-4444-8444-444444444442	fixture-expired	en-US	\N	{}	2026-08-20 12:10:30+00	\N	22222222-2222-4222-8222-222222222221	\N	2026-08-19 12:00:00+00	2026-08-18 12:00:00+00
\.


--
-- Data for Name: media_assets; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."media_assets" ("id", "workspace_id", "file_name", "mime_type", "size_bytes", "storage_key", "storage_provider", "alt_text", "created_by_user_id", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1	11111111-1111-4111-8111-111111111111	fixture.txt	text/plain	30	cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1-fixture.txt	s3	Deterministic text fixture	22222222-2222-4222-8222-222222222221	2026-08-20 12:11:00+00	2026-08-20 12:11:00+00	f	\N	\N
aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2	11111111-1111-4111-8111-111111111111	pixel.png	image/png	69	cmsify/media/11111111-1111-4111-8111-111111111111/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2-pixel.png	s3	Deterministic one pixel image	22222222-2222-4222-8222-222222222221	2026-08-20 12:12:00+00	2026-08-20 12:12:00+00	f	\N	\N
\.


--
-- Data for Name: pick_list_options; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."pick_list_options" ("id", "pick_list_id", "label", "value", "order") FROM stdin;
06944d11-d92a-4aa2-8686-09a38ecd5d91	66666666-6666-4666-8666-666666666661	Alpha (renamed)	alpha	0
b0a90ee5-f863-40e9-8a5a-a58ed25bec96	66666666-6666-4666-8666-666666666661	Beta	beta	1
\.


--
-- Data for Name: pick_list_revision_options; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."pick_list_revision_options" ("id", "pick_list_revision_id", "label", "value", "order") FROM stdin;
141919d2-45db-415e-8785-e4488ecae023	66666666-6666-4666-8666-666666666662	Beta	beta	1
a5b4594e-d846-4a02-8a29-ab0ebd64fabe	66666666-6666-4666-8666-666666666663	Beta	beta	1
b6159b16-c4fb-4b03-86e0-165c85f7f843	66666666-6666-4666-8666-666666666662	Alpha (original)	alpha	0
f595d1b8-b7c8-4ecc-86cf-bef2eedb337f	66666666-6666-4666-8666-666666666663	Alpha (renamed)	alpha	0
\.


--
-- Data for Name: pick_list_revisions; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."pick_list_revisions" ("id", "pick_list_id", "version_number", "created_at") FROM stdin;
66666666-6666-4666-8666-666666666662	66666666-6666-4666-8666-666666666661	1	2026-08-20 12:01:00+00
66666666-6666-4666-8666-666666666663	66666666-6666-4666-8666-666666666661	2	2026-08-20 12:02:00+00
\.


--
-- Data for Name: pick_lists; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."pick_lists" ("id", "workspace_id", "name", "slug", "description", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id", "current_revision_id", "package_id", "package_namespace", "package_version") FROM stdin;
66666666-6666-4666-8666-666666666661	11111111-1111-4111-8111-111111111111	Fixture Choices	fixture-choices	Immutable choice revision fixture	2026-08-20 12:01:00+00	2026-08-20 12:02:00+00	f	\N	\N	66666666-6666-4666-8666-666666666663	moving-baseline	fixture.synthetic	0.1.3
\.


--
-- Data for Name: tags; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."tags" ("id", "workspace_id", "name", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
\.


--
-- Data for Name: template_field_allowed_types; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."template_field_allowed_types" ("id", "field_id", "primitive_type", "allowed_template_id") FROM stdin;
\.


--
-- Data for Name: template_fields; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."template_fields" ("id", "template_version_id", "section_id", "key", "label", "help_text", "order", "is_required", "min_occurrences", "max_occurrences", "is_open", "composition_mode", "primitive_type", "template_id", "field_config", "component_id") FROM stdin;
44444444-4444-4444-8444-444444444443	44444444-4444-4444-8444-444444444442	\N	title	Title	\N	0	t	1	1	f	Inline	Text	\N	\N	\N
44444444-4444-4444-8444-444444444444	44444444-4444-4444-8444-444444444442	\N	choice	Choice	\N	1	t	1	1	f	Inline	PickList	\N	{"multiple": false, "picklistId": "66666666-6666-4666-8666-666666666661", "picklistRevisionId": "66666666-6666-4666-8666-666666666662"}	\N
44444444-4444-4444-8444-444444444445	44444444-4444-4444-8444-444444444442	\N	card	Card	\N	2	t	1	1	f	Inline	\N	\N	\N	55555555-5555-4555-8555-555555555551
\.


--
-- Data for Name: template_sections; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."template_sections" ("id", "template_version_id", "name", "description", "order", "is_collapsible") FROM stdin;
\.


--
-- Data for Name: template_versions; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."template_versions" ("id", "template_id", "version_number", "status", "published_at", "created_by_user_id", "notes", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
44444444-4444-4444-8444-444444444442	44444444-4444-4444-8444-444444444441	1	Published	2026-08-20 12:06:00+00	\N	\N	2026-08-20 12:05:00+00	2026-08-20 12:06:00+00	f	\N	\N
\.


--
-- Data for Name: templates; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."templates" ("id", "workspace_id", "name", "slug", "description", "package_namespace", "package_id", "package_version", "title_field_key", "current_version_id", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
44444444-4444-4444-8444-444444444441	11111111-1111-4111-8111-111111111111	Fixture Article	fixture-article	Primitive and inline component fixture	\N	\N	\N	\N	44444444-4444-4444-8444-444444444442	2026-08-20 12:05:00+00	2026-08-20 12:06:00+00	f	\N	\N
\.


--
-- Data for Name: user_sessions; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."user_sessions" ("id", "user_id", "token_hash", "created_at", "expires_at", "last_seen_at", "ip_address") FROM stdin;
\.


--
-- Data for Name: user_workspace_accesses; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."user_workspace_accesses" ("id", "user_id", "workspace_id", "access_level") FROM stdin;
aea6e464-f26e-4ac3-8659-21fdec66dae6	22222222-2222-4222-8222-222222222222	11111111-1111-4111-8111-111111111111	Write
\.


--
-- Data for Name: users; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."users" ("id", "email", "display_name", "password_hash", "role", "must_change_password", "time_zone_id", "is_active", "last_login_at", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id", "theme", "is_super_admin") FROM stdin;
22222222-2222-4222-8222-222222222221	fixture-admin@example.test	Fixture Admin	$2a$04$abcdefghijklmnopqrstuukf6r375/qlUpFWPip090tUQOYTGriXG	Admin	f	\N	t	2026-08-20 12:00:30+00	2026-08-20 12:00:00+00	2026-08-20 12:00:00+00	f	\N	\N	\N	t
22222222-2222-4222-8222-222222222222	fixture-editor@example.test	Fixture Editor	$2a$04$abcdefghijklmnopqrstuukf6r375/qlUpFWPip090tUQOYTGriXG	Editor	f	UTC	t	\N	2026-08-20 12:00:00+00	2026-08-20 12:00:00+00	f	\N	\N	\N	f
\.


--
-- Data for Name: webhook_delivery_logs; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."webhook_delivery_logs" ("id", "webhook_endpoint_id", "event_type", "payload", "attempt_count", "last_attempt_at", "next_retry_at", "status_code", "is_delivered", "is_failed", "created_at", "lease_expires_at") FROM stdin;
bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2	bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1	content.published	{"synthetic": true, "contentItemId": "77777777-7777-4777-8777-777777777772"}	10	2026-08-20 12:15:00+00	\N	503	f	t	2026-08-20 12:14:30+00	\N
\.


--
-- Data for Name: webhook_endpoints; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."webhook_endpoints" ("id", "workspace_id", "name", "url", "secret", "is_active", "created_by_user_id", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1	11111111-1111-4111-8111-111111111111	Fixture Webhook	https://fixture-webhook.example.test/cmsify-upgrade-fixture	v1.AAECAwQFBgcICQoL.BFBpi/UrF42vy+zL9I8lnA==.0pqpkko3sHX/wlWOkRiRGAJYDcLrmg==	f	22222222-2222-4222-8222-222222222221	2026-08-20 12:14:00+00	2026-08-20 12:14:00+00	f	\N	\N
\.


--
-- Data for Name: webhook_subscriptions; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."webhook_subscriptions" ("webhook_endpoint_id", "event_type") FROM stdin;
bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1	content.published
\.


--
-- Data for Name: workspaces; Type: TABLE DATA; Schema: public; Owner: -
--

COPY "public"."workspaces" ("id", "name", "slug", "description", "created_at", "updated_at", "is_deleted", "deleted_at", "deleted_by_user_id") FROM stdin;
11111111-1111-4111-8111-111111111111	Upgrade Fixture	upgrade-fixture	Default Cmsify workspace	2026-08-20 12:00:00+00	2026-08-20 12:00:00+00	f	\N	\N
11111111-1111-4111-8111-111111111112	Restricted Fixture	restricted-fixture	Synthetic denied-access workspace	2026-08-20 12:00:00+00	2026-08-20 12:00:00+00	f	\N	\N
\.


--
-- Name: __EFMigrationsHistory pk___ef_migrations_history; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."__EFMigrationsHistory"
    ADD CONSTRAINT "pk___ef_migrations_history" PRIMARY KEY ("MigrationId");


--
-- Name: api_clients pk_api_clients; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."api_clients"
    ADD CONSTRAINT "pk_api_clients" PRIMARY KEY ("id");


--
-- Name: audit_logs pk_audit_logs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."audit_logs"
    ADD CONSTRAINT "pk_audit_logs" PRIMARY KEY ("id");


--
-- Name: component_fields pk_component_fields; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."component_fields"
    ADD CONSTRAINT "pk_component_fields" PRIMARY KEY ("id");


--
-- Name: component_versions pk_component_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."component_versions"
    ADD CONSTRAINT "pk_component_versions" PRIMARY KEY ("id");


--
-- Name: components pk_components; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."components"
    ADD CONSTRAINT "pk_components" PRIMARY KEY ("id");


--
-- Name: content_field_values pk_content_field_values; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_field_values"
    ADD CONSTRAINT "pk_content_field_values" PRIMARY KEY ("id");


--
-- Name: content_item_tags pk_content_item_tags; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_item_tags"
    ADD CONSTRAINT "pk_content_item_tags" PRIMARY KEY ("content_item_id", "tag_id");


--
-- Name: content_items pk_content_items; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_items"
    ADD CONSTRAINT "pk_content_items" PRIMARY KEY ("id");


--
-- Name: content_version_field_values pk_content_version_field_values; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_version_field_values"
    ADD CONSTRAINT "pk_content_version_field_values" PRIMARY KEY ("id");


--
-- Name: content_versions pk_content_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_versions"
    ADD CONSTRAINT "pk_content_versions" PRIMARY KEY ("id");


--
-- Name: media_assets pk_media_assets; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."media_assets"
    ADD CONSTRAINT "pk_media_assets" PRIMARY KEY ("id");


--
-- Name: pick_list_options pk_pick_list_options; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_list_options"
    ADD CONSTRAINT "pk_pick_list_options" PRIMARY KEY ("id");


--
-- Name: pick_list_revision_options pk_pick_list_revision_options; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_list_revision_options"
    ADD CONSTRAINT "pk_pick_list_revision_options" PRIMARY KEY ("id");


--
-- Name: pick_list_revisions pk_pick_list_revisions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_list_revisions"
    ADD CONSTRAINT "pk_pick_list_revisions" PRIMARY KEY ("id");


--
-- Name: pick_lists pk_pick_lists; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_lists"
    ADD CONSTRAINT "pk_pick_lists" PRIMARY KEY ("id");


--
-- Name: tags pk_tags; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."tags"
    ADD CONSTRAINT "pk_tags" PRIMARY KEY ("id");


--
-- Name: template_field_allowed_types pk_template_field_allowed_types; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_field_allowed_types"
    ADD CONSTRAINT "pk_template_field_allowed_types" PRIMARY KEY ("id");


--
-- Name: template_fields pk_template_fields; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_fields"
    ADD CONSTRAINT "pk_template_fields" PRIMARY KEY ("id");


--
-- Name: template_sections pk_template_sections; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_sections"
    ADD CONSTRAINT "pk_template_sections" PRIMARY KEY ("id");


--
-- Name: template_versions pk_template_versions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_versions"
    ADD CONSTRAINT "pk_template_versions" PRIMARY KEY ("id");


--
-- Name: templates pk_templates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."templates"
    ADD CONSTRAINT "pk_templates" PRIMARY KEY ("id");


--
-- Name: user_sessions pk_user_sessions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."user_sessions"
    ADD CONSTRAINT "pk_user_sessions" PRIMARY KEY ("id");


--
-- Name: user_workspace_accesses pk_user_workspace_accesses; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."user_workspace_accesses"
    ADD CONSTRAINT "pk_user_workspace_accesses" PRIMARY KEY ("id");


--
-- Name: users pk_users; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "pk_users" PRIMARY KEY ("id");


--
-- Name: webhook_delivery_logs pk_webhook_delivery_logs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_delivery_logs"
    ADD CONSTRAINT "pk_webhook_delivery_logs" PRIMARY KEY ("id");


--
-- Name: webhook_endpoints pk_webhook_endpoints; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_endpoints"
    ADD CONSTRAINT "pk_webhook_endpoints" PRIMARY KEY ("id");


--
-- Name: webhook_subscriptions pk_webhook_subscriptions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_subscriptions"
    ADD CONSTRAINT "pk_webhook_subscriptions" PRIMARY KEY ("webhook_endpoint_id", "event_type");


--
-- Name: workspaces pk_workspaces; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."workspaces"
    ADD CONSTRAINT "pk_workspaces" PRIMARY KEY ("id");


--
-- Name: ix_api_clients_created_by_user_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_api_clients_created_by_user_id" ON "public"."api_clients" USING "btree" ("created_by_user_id");


--
-- Name: ix_api_clients_token_identifier; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_api_clients_token_identifier" ON "public"."api_clients" USING "btree" ("token_identifier") WHERE ("token_identifier" IS NOT NULL);


--
-- Name: ix_api_clients_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_api_clients_workspace_id" ON "public"."api_clients" USING "btree" ("workspace_id");


--
-- Name: ix_audit_logs_entity_type_entity_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_audit_logs_entity_type_entity_id" ON "public"."audit_logs" USING "btree" ("entity_type", "entity_id");


--
-- Name: ix_audit_logs_timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_audit_logs_timestamp" ON "public"."audit_logs" USING "btree" ("timestamp");


--
-- Name: ix_audit_logs_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_audit_logs_workspace_id" ON "public"."audit_logs" USING "btree" ("workspace_id");


--
-- Name: ix_component_fields_component_version_id_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_component_fields_component_version_id_key" ON "public"."component_fields" USING "btree" ("component_version_id", "key");


--
-- Name: ix_component_fields_nested_component_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_component_fields_nested_component_id" ON "public"."component_fields" USING "btree" ("nested_component_id");


--
-- Name: ix_component_versions_component_id; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_component_versions_component_id" ON "public"."component_versions" USING "btree" ("component_id") WHERE ((("status")::"text" = 'Draft'::"text") AND ("is_deleted" = false));


--
-- Name: ix_component_versions_component_id_version_number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_component_versions_component_id_version_number" ON "public"."component_versions" USING "btree" ("component_id", "version_number");


--
-- Name: ix_components_current_version_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_components_current_version_id" ON "public"."components" USING "btree" ("current_version_id");


--
-- Name: ix_components_workspace_id_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_components_workspace_id_slug" ON "public"."components" USING "btree" ("workspace_id", "slug") WHERE ("is_deleted" = false);


--
-- Name: ix_content_field_values_child_content_item_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_field_values_child_content_item_id" ON "public"."content_field_values" USING "btree" ("child_content_item_id");


--
-- Name: ix_content_field_values_content_item_id_field_id_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_field_values_content_item_id_field_id_order" ON "public"."content_field_values" USING "btree" ("content_item_id", "field_id", "order");


--
-- Name: ix_content_field_values_field_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_field_values_field_id" ON "public"."content_field_values" USING "btree" ("field_id");


--
-- Name: ix_content_field_values_file_asset_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_field_values_file_asset_id" ON "public"."content_field_values" USING "btree" ("file_asset_id");


--
-- Name: ix_content_field_values_media_asset_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_field_values_media_asset_id" ON "public"."content_field_values" USING "btree" ("media_asset_id");


--
-- Name: ix_content_item_tags_tag_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_item_tags_tag_id" ON "public"."content_item_tags" USING "btree" ("tag_id");


--
-- Name: ix_content_items_search_vector; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_items_search_vector" ON "public"."content_items" USING "gin" ("search_vector");


--
-- Name: ix_content_items_status_publish_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_items_status_publish_at" ON "public"."content_items" USING "btree" ("status", "publish_at");


--
-- Name: ix_content_items_status_publish_at_pending_effective_start_at_; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_items_status_publish_at_pending_effective_start_at_" ON "public"."content_items" USING "btree" ("status", "publish_at", "pending_effective_start_at", "pending_effective_end_at");


--
-- Name: ix_content_items_template_version_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_items_template_version_id" ON "public"."content_items" USING "btree" ("template_version_id");


--
-- Name: ix_content_items_translation_group_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_items_translation_group_id" ON "public"."content_items" USING "btree" ("translation_group_id");


--
-- Name: ix_content_items_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_items_workspace_id" ON "public"."content_items" USING "btree" ("workspace_id");


--
-- Name: ix_content_items_workspace_id_template_version_id_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_content_items_workspace_id_template_version_id_slug" ON "public"."content_items" USING "btree" ("workspace_id", "template_version_id", "slug") WHERE (("slug" IS NOT NULL) AND ("is_deleted" = false));


--
-- Name: ix_content_version_field_values_content_version_id_field_id_or; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_version_field_values_content_version_id_field_id_or" ON "public"."content_version_field_values" USING "btree" ("content_version_id", "field_id", "order");


--
-- Name: ix_content_versions_content_item_id; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_content_versions_content_item_id" ON "public"."content_versions" USING "btree" ("content_item_id") WHERE ((("status")::"text" = 'Published'::"text") AND ("effective_start_at" IS NULL) AND ("effective_end_at" IS NULL));


--
-- Name: ix_content_versions_content_item_id_status_effective_start_at_; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_versions_content_item_id_status_effective_start_at_" ON "public"."content_versions" USING "btree" ("content_item_id", "status", "effective_start_at", "effective_end_at");


--
-- Name: ix_content_versions_content_item_id_version_number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_content_versions_content_item_id_version_number" ON "public"."content_versions" USING "btree" ("content_item_id", "version_number");


--
-- Name: ix_content_versions_template_version_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_versions_template_version_id" ON "public"."content_versions" USING "btree" ("template_version_id");


--
-- Name: ix_content_versions_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_content_versions_workspace_id" ON "public"."content_versions" USING "btree" ("workspace_id");


--
-- Name: ix_media_assets_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_media_assets_workspace_id" ON "public"."media_assets" USING "btree" ("workspace_id");


--
-- Name: ix_pick_list_options_pick_list_id_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_pick_list_options_pick_list_id_order" ON "public"."pick_list_options" USING "btree" ("pick_list_id", "order");


--
-- Name: ix_pick_list_options_pick_list_id_value; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_pick_list_options_pick_list_id_value" ON "public"."pick_list_options" USING "btree" ("pick_list_id", "value");


--
-- Name: ix_pick_list_revision_options_pick_list_revision_id_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_pick_list_revision_options_pick_list_revision_id_order" ON "public"."pick_list_revision_options" USING "btree" ("pick_list_revision_id", "order");


--
-- Name: ix_pick_list_revision_options_pick_list_revision_id_value; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_pick_list_revision_options_pick_list_revision_id_value" ON "public"."pick_list_revision_options" USING "btree" ("pick_list_revision_id", "value");


--
-- Name: ix_pick_list_revisions_pick_list_id_version_number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_pick_list_revisions_pick_list_id_version_number" ON "public"."pick_list_revisions" USING "btree" ("pick_list_id", "version_number");


--
-- Name: ix_pick_lists_current_revision_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_pick_lists_current_revision_id" ON "public"."pick_lists" USING "btree" ("current_revision_id");


--
-- Name: ix_pick_lists_workspace_id_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_pick_lists_workspace_id_slug" ON "public"."pick_lists" USING "btree" ("workspace_id", "slug") WHERE ("is_deleted" = false);


--
-- Name: ix_tags_workspace_id_name; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_tags_workspace_id_name" ON "public"."tags" USING "btree" ("workspace_id", "name") WHERE ("is_deleted" = false);


--
-- Name: ix_template_field_allowed_types_allowed_template_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_template_field_allowed_types_allowed_template_id" ON "public"."template_field_allowed_types" USING "btree" ("allowed_template_id");


--
-- Name: ix_template_field_allowed_types_field_id_allowed_template_id; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_template_field_allowed_types_field_id_allowed_template_id" ON "public"."template_field_allowed_types" USING "btree" ("field_id", "allowed_template_id") WHERE ("allowed_template_id" IS NOT NULL);


--
-- Name: ix_template_field_allowed_types_field_id_primitive_type; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_template_field_allowed_types_field_id_primitive_type" ON "public"."template_field_allowed_types" USING "btree" ("field_id", "primitive_type") WHERE ("primitive_type" IS NOT NULL);


--
-- Name: ix_template_fields_component_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_template_fields_component_id" ON "public"."template_fields" USING "btree" ("component_id");


--
-- Name: ix_template_fields_section_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_template_fields_section_id" ON "public"."template_fields" USING "btree" ("section_id");


--
-- Name: ix_template_fields_template_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_template_fields_template_id" ON "public"."template_fields" USING "btree" ("template_id");


--
-- Name: ix_template_fields_template_version_id_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_template_fields_template_version_id_key" ON "public"."template_fields" USING "btree" ("template_version_id", "key");


--
-- Name: ix_template_sections_template_version_id_order; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_template_sections_template_version_id_order" ON "public"."template_sections" USING "btree" ("template_version_id", "order");


--
-- Name: ix_template_versions_one_draft_per_template; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_template_versions_one_draft_per_template" ON "public"."template_versions" USING "btree" ("template_id") WHERE ((("status")::"text" = 'Draft'::"text") AND ("is_deleted" = false));


--
-- Name: ix_template_versions_template_id_version_number; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_template_versions_template_id_version_number" ON "public"."template_versions" USING "btree" ("template_id", "version_number");


--
-- Name: ix_templates_current_version_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_templates_current_version_id" ON "public"."templates" USING "btree" ("current_version_id");


--
-- Name: ix_templates_workspace_id_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_templates_workspace_id_slug" ON "public"."templates" USING "btree" ("workspace_id", "slug") WHERE ("is_deleted" = false);


--
-- Name: ix_user_sessions_expires_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_user_sessions_expires_at" ON "public"."user_sessions" USING "btree" ("expires_at");


--
-- Name: ix_user_sessions_token_hash; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_user_sessions_token_hash" ON "public"."user_sessions" USING "btree" ("token_hash");


--
-- Name: ix_user_sessions_user_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_user_sessions_user_id" ON "public"."user_sessions" USING "btree" ("user_id");


--
-- Name: ix_user_workspace_accesses_user_id_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_user_workspace_accesses_user_id_workspace_id" ON "public"."user_workspace_accesses" USING "btree" ("user_id", "workspace_id");


--
-- Name: ix_user_workspace_accesses_workspace_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_user_workspace_accesses_workspace_id" ON "public"."user_workspace_accesses" USING "btree" ("workspace_id");


--
-- Name: ix_users_email; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_users_email" ON "public"."users" USING "btree" ("email") WHERE ("is_deleted" = false);


--
-- Name: ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at_; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_webhook_delivery_logs_is_delivered_is_failed_next_retry_at_" ON "public"."webhook_delivery_logs" USING "btree" ("is_delivered", "is_failed", "next_retry_at", "lease_expires_at");


--
-- Name: ix_webhook_delivery_logs_webhook_endpoint_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_webhook_delivery_logs_webhook_endpoint_id" ON "public"."webhook_delivery_logs" USING "btree" ("webhook_endpoint_id");


--
-- Name: ix_webhook_endpoints_created_by_user_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "ix_webhook_endpoints_created_by_user_id" ON "public"."webhook_endpoints" USING "btree" ("created_by_user_id");


--
-- Name: ix_webhook_endpoints_workspace_id_name; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_webhook_endpoints_workspace_id_name" ON "public"."webhook_endpoints" USING "btree" ("workspace_id", "name") WHERE ("is_deleted" = false);


--
-- Name: ix_workspaces_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "ix_workspaces_slug" ON "public"."workspaces" USING "btree" ("slug");


--
-- Name: api_clients fk_api_clients_users_created_by_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."api_clients"
    ADD CONSTRAINT "fk_api_clients_users_created_by_user_id" FOREIGN KEY ("created_by_user_id") REFERENCES "public"."users"("id") ON DELETE RESTRICT;


--
-- Name: api_clients fk_api_clients_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."api_clients"
    ADD CONSTRAINT "fk_api_clients_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE SET NULL;


--
-- Name: component_fields fk_component_fields_component_versions_component_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."component_fields"
    ADD CONSTRAINT "fk_component_fields_component_versions_component_version_id" FOREIGN KEY ("component_version_id") REFERENCES "public"."component_versions"("id") ON DELETE CASCADE;


--
-- Name: component_fields fk_component_fields_components_nested_component_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."component_fields"
    ADD CONSTRAINT "fk_component_fields_components_nested_component_id" FOREIGN KEY ("nested_component_id") REFERENCES "public"."components"("id") ON DELETE RESTRICT;


--
-- Name: component_versions fk_component_versions_components_component_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."component_versions"
    ADD CONSTRAINT "fk_component_versions_components_component_id" FOREIGN KEY ("component_id") REFERENCES "public"."components"("id") ON DELETE CASCADE;


--
-- Name: components fk_components_component_versions_current_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."components"
    ADD CONSTRAINT "fk_components_component_versions_current_version_id" FOREIGN KEY ("current_version_id") REFERENCES "public"."component_versions"("id") ON DELETE RESTRICT;


--
-- Name: components fk_components_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."components"
    ADD CONSTRAINT "fk_components_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: content_field_values fk_content_field_values_content_items_child_content_item_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_field_values"
    ADD CONSTRAINT "fk_content_field_values_content_items_child_content_item_id" FOREIGN KEY ("child_content_item_id") REFERENCES "public"."content_items"("id") ON DELETE RESTRICT;


--
-- Name: content_field_values fk_content_field_values_content_items_content_item_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_field_values"
    ADD CONSTRAINT "fk_content_field_values_content_items_content_item_id" FOREIGN KEY ("content_item_id") REFERENCES "public"."content_items"("id") ON DELETE CASCADE;


--
-- Name: content_field_values fk_content_field_values_media_assets_file_asset_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_field_values"
    ADD CONSTRAINT "fk_content_field_values_media_assets_file_asset_id" FOREIGN KEY ("file_asset_id") REFERENCES "public"."media_assets"("id") ON DELETE RESTRICT;


--
-- Name: content_field_values fk_content_field_values_media_assets_media_asset_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_field_values"
    ADD CONSTRAINT "fk_content_field_values_media_assets_media_asset_id" FOREIGN KEY ("media_asset_id") REFERENCES "public"."media_assets"("id") ON DELETE RESTRICT;


--
-- Name: content_field_values fk_content_field_values_template_fields_field_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_field_values"
    ADD CONSTRAINT "fk_content_field_values_template_fields_field_id" FOREIGN KEY ("field_id") REFERENCES "public"."template_fields"("id") ON DELETE RESTRICT;


--
-- Name: content_item_tags fk_content_item_tags_content_items_content_item_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_item_tags"
    ADD CONSTRAINT "fk_content_item_tags_content_items_content_item_id" FOREIGN KEY ("content_item_id") REFERENCES "public"."content_items"("id") ON DELETE CASCADE;


--
-- Name: content_item_tags fk_content_item_tags_tags_tag_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_item_tags"
    ADD CONSTRAINT "fk_content_item_tags_tags_tag_id" FOREIGN KEY ("tag_id") REFERENCES "public"."tags"("id") ON DELETE CASCADE;


--
-- Name: content_items fk_content_items_template_versions_template_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_items"
    ADD CONSTRAINT "fk_content_items_template_versions_template_version_id" FOREIGN KEY ("template_version_id") REFERENCES "public"."template_versions"("id") ON DELETE RESTRICT;


--
-- Name: content_items fk_content_items_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_items"
    ADD CONSTRAINT "fk_content_items_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: content_version_field_values fk_content_version_field_values_content_versions_content_versi; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_version_field_values"
    ADD CONSTRAINT "fk_content_version_field_values_content_versions_content_versi" FOREIGN KEY ("content_version_id") REFERENCES "public"."content_versions"("id") ON DELETE CASCADE;


--
-- Name: content_versions fk_content_versions_content_items_content_item_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_versions"
    ADD CONSTRAINT "fk_content_versions_content_items_content_item_id" FOREIGN KEY ("content_item_id") REFERENCES "public"."content_items"("id") ON DELETE CASCADE;


--
-- Name: content_versions fk_content_versions_template_versions_template_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_versions"
    ADD CONSTRAINT "fk_content_versions_template_versions_template_version_id" FOREIGN KEY ("template_version_id") REFERENCES "public"."template_versions"("id") ON DELETE RESTRICT;


--
-- Name: content_versions fk_content_versions_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."content_versions"
    ADD CONSTRAINT "fk_content_versions_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: media_assets fk_media_assets_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."media_assets"
    ADD CONSTRAINT "fk_media_assets_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: pick_list_options fk_pick_list_options_pick_lists_pick_list_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_list_options"
    ADD CONSTRAINT "fk_pick_list_options_pick_lists_pick_list_id" FOREIGN KEY ("pick_list_id") REFERENCES "public"."pick_lists"("id") ON DELETE CASCADE;


--
-- Name: pick_list_revision_options fk_pick_list_revision_options_pick_list_revisions_pick_list_re; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_list_revision_options"
    ADD CONSTRAINT "fk_pick_list_revision_options_pick_list_revisions_pick_list_re" FOREIGN KEY ("pick_list_revision_id") REFERENCES "public"."pick_list_revisions"("id") ON DELETE CASCADE;


--
-- Name: pick_list_revisions fk_pick_list_revisions_pick_lists_pick_list_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_list_revisions"
    ADD CONSTRAINT "fk_pick_list_revisions_pick_lists_pick_list_id" FOREIGN KEY ("pick_list_id") REFERENCES "public"."pick_lists"("id") ON DELETE CASCADE;


--
-- Name: pick_lists fk_pick_lists_pick_list_revisions_current_revision_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_lists"
    ADD CONSTRAINT "fk_pick_lists_pick_list_revisions_current_revision_id" FOREIGN KEY ("current_revision_id") REFERENCES "public"."pick_list_revisions"("id") ON DELETE RESTRICT;


--
-- Name: pick_lists fk_pick_lists_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."pick_lists"
    ADD CONSTRAINT "fk_pick_lists_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: tags fk_tags_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."tags"
    ADD CONSTRAINT "fk_tags_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: template_field_allowed_types fk_template_field_allowed_types_template_fields_field_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_field_allowed_types"
    ADD CONSTRAINT "fk_template_field_allowed_types_template_fields_field_id" FOREIGN KEY ("field_id") REFERENCES "public"."template_fields"("id") ON DELETE CASCADE;


--
-- Name: template_field_allowed_types fk_template_field_allowed_types_templates_allowed_template_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_field_allowed_types"
    ADD CONSTRAINT "fk_template_field_allowed_types_templates_allowed_template_id" FOREIGN KEY ("allowed_template_id") REFERENCES "public"."templates"("id") ON DELETE RESTRICT;


--
-- Name: template_fields fk_template_fields_components_component_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_fields"
    ADD CONSTRAINT "fk_template_fields_components_component_id" FOREIGN KEY ("component_id") REFERENCES "public"."components"("id") ON DELETE RESTRICT;


--
-- Name: template_fields fk_template_fields_template_sections_section_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_fields"
    ADD CONSTRAINT "fk_template_fields_template_sections_section_id" FOREIGN KEY ("section_id") REFERENCES "public"."template_sections"("id") ON DELETE SET NULL;


--
-- Name: template_fields fk_template_fields_template_versions_template_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_fields"
    ADD CONSTRAINT "fk_template_fields_template_versions_template_version_id" FOREIGN KEY ("template_version_id") REFERENCES "public"."template_versions"("id") ON DELETE CASCADE;


--
-- Name: template_fields fk_template_fields_templates_template_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_fields"
    ADD CONSTRAINT "fk_template_fields_templates_template_id" FOREIGN KEY ("template_id") REFERENCES "public"."templates"("id") ON DELETE RESTRICT;


--
-- Name: template_sections fk_template_sections_template_versions_template_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_sections"
    ADD CONSTRAINT "fk_template_sections_template_versions_template_version_id" FOREIGN KEY ("template_version_id") REFERENCES "public"."template_versions"("id") ON DELETE CASCADE;


--
-- Name: template_versions fk_template_versions_templates_template_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."template_versions"
    ADD CONSTRAINT "fk_template_versions_templates_template_id" FOREIGN KEY ("template_id") REFERENCES "public"."templates"("id") ON DELETE CASCADE;


--
-- Name: templates fk_templates_template_versions_current_version_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."templates"
    ADD CONSTRAINT "fk_templates_template_versions_current_version_id" FOREIGN KEY ("current_version_id") REFERENCES "public"."template_versions"("id") ON DELETE SET NULL;


--
-- Name: templates fk_templates_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."templates"
    ADD CONSTRAINT "fk_templates_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: user_sessions fk_user_sessions_users_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."user_sessions"
    ADD CONSTRAINT "fk_user_sessions_users_user_id" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE CASCADE;


--
-- Name: user_workspace_accesses fk_user_workspace_accesses_users_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."user_workspace_accesses"
    ADD CONSTRAINT "fk_user_workspace_accesses_users_user_id" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE CASCADE;


--
-- Name: user_workspace_accesses fk_user_workspace_accesses_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."user_workspace_accesses"
    ADD CONSTRAINT "fk_user_workspace_accesses_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: webhook_delivery_logs fk_webhook_delivery_logs_webhook_endpoints_webhook_endpoint_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_delivery_logs"
    ADD CONSTRAINT "fk_webhook_delivery_logs_webhook_endpoints_webhook_endpoint_id" FOREIGN KEY ("webhook_endpoint_id") REFERENCES "public"."webhook_endpoints"("id") ON DELETE CASCADE;


--
-- Name: webhook_endpoints fk_webhook_endpoints_users_created_by_user_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_endpoints"
    ADD CONSTRAINT "fk_webhook_endpoints_users_created_by_user_id" FOREIGN KEY ("created_by_user_id") REFERENCES "public"."users"("id") ON DELETE RESTRICT;


--
-- Name: webhook_endpoints fk_webhook_endpoints_workspaces_workspace_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_endpoints"
    ADD CONSTRAINT "fk_webhook_endpoints_workspaces_workspace_id" FOREIGN KEY ("workspace_id") REFERENCES "public"."workspaces"("id") ON DELETE CASCADE;


--
-- Name: webhook_subscriptions fk_webhook_subscriptions_webhook_endpoints_webhook_endpoint_id; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY "public"."webhook_subscriptions"
    ADD CONSTRAINT "fk_webhook_subscriptions_webhook_endpoints_webhook_endpoint_id" FOREIGN KEY ("webhook_endpoint_id") REFERENCES "public"."webhook_endpoints"("id") ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--

\unrestrict cmsifyupgradefixturev013
