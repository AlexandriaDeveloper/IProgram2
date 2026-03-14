namespace Application.Dtos.Requests
{
    public class UpdateBeneficiaryNetPayRequest
    {
        public string EmployeeId { get; set; }
        public double? NetPay { get; set; }
    }
}
