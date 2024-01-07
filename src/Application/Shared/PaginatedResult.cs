namespace Application.Helpers
{
    public class PaginatedResult<T>
    {

        public List<T> Data { get; private set; }
        public int PageIndex { get; private set; }
        public int PageSize { get; private set; }
        public int Count { get; private set; }

        private PaginatedResult(List<T> data, int PageIndex, int pageSize, int Count)
        {
            this.Data = data;
            this.PageSize = pageSize;
            this.PageIndex = PageIndex;
            this.Count = Count;
        }

        public static PaginatedResult<T> Create(List<T> data, int PageIndex, int pageSize, int Count)
        {
            return new PaginatedResult<T>(data, PageIndex, pageSize, Count);
        }

    }

}
