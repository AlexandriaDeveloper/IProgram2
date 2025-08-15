

using System.ComponentModel.DataAnnotations;

namespace Core.Models
{
    public class Form : Entity
    {
        public int? DailyId { get; set; }
        public int? Index { get; set; }

        public string Description { get; set; }
        public Daily Daily { get; set; }
        public bool CompletedReviewed => FormDetails.All(fd => fd.IsReviewed);
        public ICollection<FormDetails> FormDetails { get; set; } = new List<FormDetails>();
        public ICollection<FormRefernce> FormRefernces { get; set; }
        public ApplicationUser User { get; set; }
    }
}