import type { components } from "./generated/schema";

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  extensions?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export type Workspace = components["schemas"]["WorkspaceDto"];
export type ContentItem = components["schemas"]["ContentItemDetailResponse"];
export type ContentListItem = components["schemas"]["ContentItemSummaryResponse"];
export type Template = components["schemas"]["TemplateResponse"];
export type TemplateListItem = components["schemas"]["TemplateSummaryResponse"];
export type MediaAsset = components["schemas"]["MediaAssetResponse"];
