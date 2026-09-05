import { describe, expect, it } from "vitest";
import { renderCmsifyTemplate } from "../src/templating";

describe("renderCmsifyTemplate", () => {
  it("substitutes a known variable", () => {
    expect(
      renderCmsifyTemplate("Email ${{supportEmail}} for help.", {
        supportEmail: "support@example.com",
      }),
    ).toBe("Email support@example.com for help.");
  });

  it("leaves an unknown token as a literal", () => {
    expect(
      renderCmsifyTemplate("Contact ${{supprtEmail}} for help.", {
        supportEmail: "support@example.com",
      }),
    ).toBe("Contact ${{supprtEmail}} for help.");
  });

  it("renders an explicit null/undefined value as an empty string", () => {
    expect(renderCmsifyTemplate("Prefix[${{maybeBlank}}]Suffix", { maybeBlank: null })).toBe(
      "Prefix[]Suffix",
    );
    expect(renderCmsifyTemplate("Prefix[${{maybeBlank}}]Suffix", { maybeBlank: undefined })).toBe(
      "Prefix[]Suffix",
    );
  });

  it("returns the original string unchanged when no tokens are present", () => {
    const template = "Nothing to render here.";
    expect(renderCmsifyTemplate(template, { unused: "value" })).toBe(template);
  });

  it("substitutes multiple tokens, mixed known and unknown", () => {
    expect(
      renderCmsifyTemplate("Hi ${{name}}, email ${{supportEmail}} or call ${{phone}}.", {
        name: "Jon",
        supportEmail: "support@example.com",
      }),
    ).toBe("Hi Jon, email support@example.com or call ${{phone}}.");
  });

  it("tolerates whitespace inside braces", () => {
    expect(
      renderCmsifyTemplate("Email ${{ supportEmail }} for help.", {
        supportEmail: "support@example.com",
      }),
    ).toBe("Email support@example.com for help.");
  });

  it("is case-sensitive", () => {
    expect(
      renderCmsifyTemplate("Email ${{SupportEmail}} for help.", {
        supportEmail: "support@example.com",
      }),
    ).toBe("Email ${{SupportEmail}} for help.");
  });

  it("leaves malformed tokens alone", () => {
    expect(renderCmsifyTemplate("${{}}", { "123abc": "should not match" })).toBe("${{}}");
    expect(renderCmsifyTemplate("${{123abc}}", { "123abc": "should not match" })).toBe(
      "${{123abc}}",
    );
  });
});
