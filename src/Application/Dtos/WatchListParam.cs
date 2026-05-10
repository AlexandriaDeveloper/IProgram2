using Persistence.Helpers;

namespace Application.Dtos
{
    public class WatchListParam : Param
    {
        public string Search { get; set; }
        public string NationalId { get; set; }
        public int? TegaraCode { get; set; }
        public int? TabCode { get; set; }
    }
}
