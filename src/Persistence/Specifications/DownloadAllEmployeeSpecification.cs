
using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class DownloadAllEmployeeSpecification : Specification<Employee>
    {
        public DownloadAllEmployeeSpecification(DownloadAllEmployeesParam param)
        {

            AddInclude(x => x.Department);
            AddInclude(x => x.EmployeeBank);
            //  AddInclude(x => x.EmployeeRefernces);
            if (!string.IsNullOrEmpty(param.BankName))
            {
                AddCriteries(x => x.EmployeeBank.BankName.Equals(param.BankName));
            }
            if (!string.IsNullOrEmpty(param.BranchName))
            {
                AddCriteries(x => x.EmployeeBank.BranchName.Equals(param.BranchName));
            }
            if (param.DepartmentId.HasValue)
            {
                AddCriteries(x => x.DepartmentId.Equals(param.DepartmentId));
            }

            if (!string.IsNullOrEmpty(param.Collage))
            {
                AddCriteries(x => x.Collage.Equals(param.Collage));
            }

            if (!string.IsNullOrEmpty(param.Section))
            {
                AddCriteries(x => x.Section.Equals(param.Section));
            }


        }

    }
}