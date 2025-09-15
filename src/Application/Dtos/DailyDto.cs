using Application.Dtos;

public class DailyDto
{
    public int Id { get; set; }
    public string Name { get; set; }
    public DateTime DailyDate { get; set; }
    public bool Closed { get; internal set; }
    public virtual List<FormDto> Forms { get; set; }
    public virtual List<DailyReferenceDto> DailyReferences { get; set; }
}

public class DailyReferenceDto
{
    public int Id { get; set; }
    public int DailyId { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string ReferencePath { get; set; }
}
