namespace Persistence.Helpers
{
    public class EmployeeParam : Param
    {
        public string EmployeeId { get; set; }
        public string Name { get; set; }
        public int? TegaraCode { get; set; }
        public int? TabCode { get; set; }
        public string Collage { get; set; }
        public int? DepartmentId { get; set; }
        public string DepartmentName { get; set; }
    }
}