namespace Application.Dtos.Requests
{
    public class FormDetailsRequest
    {
        public int Id { get; set; }
        public int FormId { get; set; }
        public string EmployeeId { get; set; }
        public double Amount { get; set; }
        //   public int OrderNum { get; set; }

    }
}
