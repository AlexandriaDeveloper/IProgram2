namespace Application.Dtos
{
    public class FormArchivedDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int? DailyId { get; set; }

        public double TotalAmount { get; set; }
        public int Count { get; set; }
        public string CreatedBy { get; set; }
        public List<FormDetailsDto> FormDetails { get; set; }

        public FormArchivedDto()
        {
            if (FormDetails == null)
            {
                FormDetails = new List<FormDetailsDto>();
            }
        }
    }

}