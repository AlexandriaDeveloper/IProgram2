using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Application.Dtos
{
    public class JsonDataDto
    {

        [Required]
        [MaxLength(200, ErrorMessage = "البيان طويل الحد الاقصى 200 حرف")]
        public string Name { get; set; }
        [Required]
        public DateTime DailyDate { get; set; }

        public bool Closed { get; set; } = false;
        public ICollection<JsonForm> Forms { get; set; } = new List<JsonForm>();

        public class JsonForm
        {
            public string Name { get; set; }
            public int? Index { get; set; }
            public string Description { get; set; }
            public ICollection<JsonFormDetails> FormDetails { get; set; } = new List<JsonFormDetails>();


            public class JsonFormDetails
            {
                public string EmployeeId { get; set; }
                public double Amount { get; set; }
                public int OrderNum { get; set; }
                public bool IsReviewed { get; set; }
                public string IsReviewedBy { get; set; }
                public DateTime? ReviewedAt { get; set; }
                public string ReviewComments { get; set; }
                public bool IsSummaryReviewed { get; set; }
                public string IsSummaryReviewedBy { get; set; }
                public DateTime? SummaryReviewedAt { get; set; }
                public string SummaryComments { get; set; }


            }

        }


    }
}