window.cmsifyStorage = {
  area: (name) => name === "local" ? window.localStorage : window.sessionStorage,
  set: (name, key, value) => window.cmsifyStorage.area(name).setItem(key, value),
  get: (name, key) => window.cmsifyStorage.area(name).getItem(key),
  remove: (name, key) => window.cmsifyStorage.area(name).removeItem(key),
  getTheme: () => window.localStorage.getItem("cmsify.theme"),
  setTheme: (theme) => {
    window.localStorage.setItem("cmsify.theme", theme);
    document.documentElement.dataset.bsTheme = theme === "auto"
      ? (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light")
      : theme;
  }
};
