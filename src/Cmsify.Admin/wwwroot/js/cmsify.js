(() => {
  const themeStorageKey = "cmsify.theme";
  const darkModeMediaQuery = window.matchMedia("(prefers-color-scheme: dark)");

  const normalizeTheme = (theme) => theme === "dark" || theme === "light" || theme === "auto"
    ? theme
    : "auto";

  const resolveTheme = (theme) => {
    const selectedTheme = normalizeTheme(theme);
    return selectedTheme === "auto"
      ? (darkModeMediaQuery.matches ? "dark" : "light")
      : selectedTheme;
  };

  const applyTheme = (theme) => {
    const effectiveTheme = resolveTheme(theme);
    document.documentElement.dataset.bsTheme = effectiveTheme;
    return effectiveTheme;
  };

  let mediaQueryListenerRegistered = false;

  window.cmsifyStorage = {
    area: (name) => name === "local" ? window.localStorage : window.sessionStorage,
    set: (name, key, value) => window.cmsifyStorage.area(name).setItem(key, value),
    get: (name, key) => window.cmsifyStorage.area(name).getItem(key),
    remove: (name, key) => window.cmsifyStorage.area(name).removeItem(key),
    getTheme: () => window.localStorage.getItem(themeStorageKey),
    getThemeState: () => {
      const theme = normalizeTheme(window.localStorage.getItem(themeStorageKey));
      return { theme, effectiveTheme: resolveTheme(theme) };
    },
    initTheme: () => {
      if (!mediaQueryListenerRegistered) {
        darkModeMediaQuery.addEventListener("change", () => {
          if (normalizeTheme(window.localStorage.getItem(themeStorageKey)) === "auto") {
            applyTheme("auto");
          }
        });
        mediaQueryListenerRegistered = true;
      }

      const themeState = window.cmsifyStorage.getThemeState();
      applyTheme(themeState.theme);
      return themeState;
    },
    setTheme: (theme) => {
      const selectedTheme = normalizeTheme(theme);
      window.localStorage.setItem(themeStorageKey, selectedTheme);
      return applyTheme(selectedTheme);
    }
  };

  window.cmsifyDownloads = {
    save: (fileName, contentType, contentBase64) => {
      const bytes = Uint8Array.from(atob(contentBase64), (value) => value.charCodeAt(0));
      const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      anchor.style.display = "none";
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), 0);
    }
  };

  window.cmsifyAuth = {
    getCsrfToken: () => {
      const meta = document.querySelector('meta[name="cmsify-csrf-token"]');
      return meta ? meta.getAttribute("content") || "" : "";
    },
    refreshClaimsAndNavigate: async (returnUrl) => {
      try {
        await fetch("/admin-auth/refresh-claims", {
          method: "POST",
          credentials: "same-origin",
          headers: { "RequestVerificationToken": window.cmsifyAuth.getCsrfToken() }
        });
      } catch (e) {
        // Ignore — we still want to navigate; the cookie will simply not be refreshed,
        // and GuardedRouteView will redirect back to change-password if needed.
      }
      window.location.assign(returnUrl || "/workspaces");
    },
    submitLogout: () => {
      const form = document.createElement("form");
      form.method = "post";
      form.action = "/admin-auth/logout";
      const csrf = document.createElement("input");
      csrf.type = "hidden";
      csrf.name = "__RequestVerificationToken";
      csrf.value = window.cmsifyAuth.getCsrfToken();
      form.appendChild(csrf);
      document.body.appendChild(form);
      form.submit();
    }
  };
})();
