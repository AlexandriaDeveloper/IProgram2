namespace Application.Dtos
{
    public class FormDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int? DailyId { get; set; }
        public bool HasReferences { get; set; }

        public double TotalAmount { get; set; }
        public int Count { get; set; }
        public List<FormDetailsDto> FormDetails { get; set; }

        public FormDto()
        {
            if (FormDetails == null)
            {
                FormDetails = new List<FormDetailsDto>();
            }
        }
    }

}