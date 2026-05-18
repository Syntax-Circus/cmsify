export class ETagStore {
  private readonly values = new Map<string, string>();

  get(key: string): string | undefined {
    return this.values.get(key);
  }

  set(key: string, etag: string | null): void {
    if (etag) {
      this.values.set(key, etag);
    }
  }
}
