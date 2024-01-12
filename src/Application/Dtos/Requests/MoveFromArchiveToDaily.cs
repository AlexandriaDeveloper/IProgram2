namespace Application.Dtos.Requests
{
    public class MoveFromArchiveToDaily
    {
        public int DailyId { get; set; }
        public int[] FormIds { get; set; }
    }

}