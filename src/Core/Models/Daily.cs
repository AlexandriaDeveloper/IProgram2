using System.ComponentModel.DataAnnotations;

namespace Core.Models
{
    public class Daily : Entity
    {
        [Required]
        [MaxLength(200, ErrorMessage = "البيان طويل الحد الاقصى 200 حرف")]
        override public string Name { get; set; }
        [Required]
        public DateTime DailyDate { get; set; }

        public bool Closed { get; set; } = false;
        public ICollection<Form> Forms { get; set; }
    }
}