export interface PageResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export async function* listAll<T>(loader: (page: number) => Promise<PageResult<T>>): AsyncIterable<T> {
  for (let page = 1; ; page += 1) {
    const result = await loader(page);
    for (const item of result.items) {
      yield item;
    }

    if (page >= result.totalPages || result.items.length === 0) {
      return;
    }
  }
}
