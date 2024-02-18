namespace Application.Dtos
{


    public class FormDetailsDto
    {
        public virtual int Id { get; set; }
        public virtual int FormId { get; set; }
        public int EmployeeId { get; set; }
        public double Amount { get; set; }
        public string Department { get; set; }
        public int? TabCode { get; set; }
        public int? TegaraCode { get; set; }
        public string NationalId { get; set; }
        public string Name { get; set; }
        //  public Form Form { get; set; }
        // public Employee Employee { get; set; }

    }


}
