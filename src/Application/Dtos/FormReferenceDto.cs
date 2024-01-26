using System.ComponentModel.DataAnnotations;

namespace Application.Dtos
{
    public class FormReferenceDto
    {
        public int? Id { get; set; }

        public int FormId { get; set; }
        [MaxLength(250)]
        public string ReferencePath { get; set; }

    }


}
