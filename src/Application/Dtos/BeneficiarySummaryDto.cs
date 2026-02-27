namespace Application.Dtos
{
    public class BeneficiaryDetailDto
    {
        public int FormDetailId { get; set; }
        public string FormName { get; set; }
        public int? FormIndex { get; set; }
        public double Amount { get; set; }
        public bool IsReviewed { get; set; }
        public string IsReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public bool IsSummaryReviewed { get; set; }
        public string IsSummaryReviewedBy { get; set; }
        public DateTime? SummaryReviewedAt { get; set; }
        public string SummaryComment { get; set; }
    }

    public class BeneficiarySummaryDto
    {
        public string EmployeeId { get; set; }
        public string EmployeeName { get; set; }
        public string Department { get; set; }
        public string TabCode { get; set; }
        public string TegaraCode { get; set; }
        public double TotalAmount { get; set; }
        public bool IsFullyReviewed { get; set; }
        public string Comment { get; set; }
        public List<BeneficiaryDetailDto> Details { get; set; } = new List<BeneficiaryDetailDto>();
    }

    public class DailyBeneficiarySummaryResponse
    {
        public string DailyName { get; set; }
        public DateTime DailyDate { get; set; }
        public double TotalAmount { get; set; }
        public int TotalBeneficiaries { get; set; }
        public List<BeneficiarySummaryDto> Beneficiaries { get; set; } = new List<BeneficiarySummaryDto>();
    }
}
