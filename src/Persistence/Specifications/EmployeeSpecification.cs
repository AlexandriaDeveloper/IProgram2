
using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class EmployeeSpecification : Specification<Employee>
    {
        public EmployeeSpecification(EmployeeParam param)
        {

            AddInclude(x => x.Department);
            AddInclude(x => x.EmployeeBank);
            //  AddInclude(x => x.EmployeeRefernces);
            if (!string.IsNullOrEmpty(param.EmployeeId))
            {
                AddCriteries(x => x.Id.Contains(param.EmployeeId));
            }
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (!string.IsNullOrEmpty(param.Id))
            {
                AddCriteries(x => x.Id.Equals(param.Id));
            }
            if (param.TabCode.HasValue)
            {
                AddCriteries(x => x.TabCode.Equals(param.TabCode));
            }
            if (param.TegaraCode.HasValue)
            {
                AddCriteries(x => x.TegaraCode.Equals(param.TegaraCode));
            }
            if (!string.IsNullOrEmpty(param.DepartmentName))
            {
                AddCriteries(x => x.Department.Name.Contains(param.DepartmentName));
            }

            if (!string.IsNullOrEmpty(param.Collage))
            {
                AddCriteries(x => x.Collage.Contains(param.Collage));
            }
            if (param.DepartmentId.HasValue)
            {
                AddCriteries(x => x.DepartmentId.Equals(param.DepartmentId));
            }
            if (PaginationEnabled)
            {
                ApplyPaging(param.PageIndex, param.PageSize);
            }
            //  ApplyPaging(param.PageIndex, param.PageSize);

        }

    }


    public class EmployeeCountSpecification : Specification<Employee>
    {
        public EmployeeCountSpecification(EmployeeParam param)
        {
            AddInclude(x => x.Department);
            AddInclude(x => x.EmployeeBank);
            //  AddInclude(x => x.EmployeeRefernces);
            if (!string.IsNullOrEmpty(param.EmployeeId))
            {
                AddCriteries(x => x.Id == param.EmployeeId);
            }
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (!string.IsNullOrEmpty(param.Id))
            {
                AddCriteries(x => x.Id.Equals(param.Id));
            }
            // if (!string.IsNullOrEmpty(param.NationalId))
            // {
            //     AddCriteries(x => x.NationalId.Equals(param.NationalId));
            // }
            if (param.TabCode.HasValue)
            {
                AddCriteries(x => x.TabCode.Equals(param.TabCode));
            }
            if (param.TegaraCode.HasValue)
            {
                AddCriteries(x => x.TegaraCode.Equals(param.TegaraCode));
            }
            if (!string.IsNullOrEmpty(param.DepartmentName))
            {
                AddCriteries(x => x.Department.Name.Contains(param.DepartmentName));
            }
            if (!string.IsNullOrEmpty(param.Collage))
            {
                AddCriteries(x => x.Collage.Contains(param.Collage));
            }
            if (param.DepartmentId.HasValue)
            {
                AddCriteries(x => x.DepartmentId.Equals(param.DepartmentId));
            }

            this.PaginationEnabled = false;

        }

    }
}