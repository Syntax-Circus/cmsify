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
  page?: number;
  pageSize?: number;
  offset?: number;
  limit?: number;
  totalPages?: number;
}

export interface Workspace {
  id: string;
  name: string;
  slug: string;
  description?: string | null;
}

export interface ContentItem {
  id: string;
  workspaceId: string;
  templateVersionId: string;
  status: string;
  slug?: string | null;
  fields?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface Template {
  id: string;
  workspaceId: string;
  name: string;
  slug: string;
  description?: string | null;
  currentVersion?: unknown;
}

export interface MediaAsset {
  id: string;
  workspaceId: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  altText?: string | null;
}
