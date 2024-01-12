namespace Persistence.Helpers
{
    public class DailyParam : Param
    {

        public string Name { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool? Closed { get; set; }

    }
}