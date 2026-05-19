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
})();
