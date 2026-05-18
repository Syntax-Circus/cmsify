import type { ProblemDetails } from "./types";

export class CmsifyApiError extends Error {
  readonly problem: ProblemDetails;
  readonly status: number;
  readonly traceId: string | undefined;
  readonly correlationId: string | undefined;

  constructor(problem: ProblemDetails, correlationId?: string) {
    super(problem.detail ?? problem.title ?? `Cmsify API request failed with ${problem.status ?? "unknown status"}`);
    this.name = "CmsifyApiError";
    this.problem = problem;
    this.status = problem.status ?? 0;
    this.traceId = typeof problem.traceId === "string" ? problem.traceId : undefined;
    this.correlationId = correlationId;
  }
}

export const isProblemDetails = (value: unknown): value is ProblemDetails =>
  typeof value === "object" && value !== null && ("status" in value || "title" in value || "type" in value);
