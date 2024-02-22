using Application.Dtos;

public class DailyDto
{
    public int Id { get; set; }
    public string Name { get; set; }
    public DateTime DailyDate { get; set; }
    public bool Closed { get; internal set; }
    public virtual List<FormDto> Forms { get; set; }
}