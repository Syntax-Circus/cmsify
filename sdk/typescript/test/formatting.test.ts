import { describe, expect, it } from "vitest";
import { getFormatHint, isProseHint, TEXT_FORMAT_HINTS, toMimeType } from "../src/formatting";

describe("formatting", () => {
  describe("getFormatHint", () => {
    it("defaults to plaintext when config is missing", () => {
      expect(getFormatHint(undefined)).toBe("plaintext");
      expect(getFormatHint(null)).toBe("plaintext");
      expect(getFormatHint({})).toBe("plaintext");
    });

    it("returns the declared hint when known", () => {
      expect(getFormatHint({ formatHint: "json" })).toBe("json");
      expect(getFormatHint({ formatHint: "html" })).toBe("html");
    });

    it("normalizes mixed-case hints", () => {
      expect(getFormatHint({ formatHint: "JSON" })).toBe("json");
    });

    it("falls back to plaintext for unknown hints (forward-compatible)", () => {
      expect(getFormatHint({ formatHint: "protobuf" })).toBe("plaintext");
    });
  });

  describe("toMimeType", () => {
    it("maps known hints to media types", () => {
      expect(toMimeType("json")).toBe("application/json");
      expect(toMimeType("html")).toBe("text/html");
      expect(toMimeType("markdown")).toBe("text/markdown");
    });

    it("returns text/plain for unknown or missing hints", () => {
      expect(toMimeType(undefined)).toBe("text/plain");
      expect(toMimeType("protobuf")).toBe("text/plain");
    });
  });

  describe("isProseHint", () => {
    it("treats plaintext, markdown, and html as prose", () => {
      expect(isProseHint("plaintext")).toBe(true);
      expect(isProseHint("markdown")).toBe(true);
      expect(isProseHint("html")).toBe(true);
    });

    it("treats structured formats as non-prose", () => {
      expect(isProseHint("json")).toBe(false);
      expect(isProseHint("xml")).toBe(false);
      expect(isProseHint("code")).toBe(false);
    });
  });

  it("exposes a complete enum list", () => {
    expect(TEXT_FORMAT_HINTS).toContain("plaintext");
    expect(TEXT_FORMAT_HINTS).toContain("regex");
    expect(TEXT_FORMAT_HINTS.length).toBeGreaterThanOrEqual(13);
  });
});
