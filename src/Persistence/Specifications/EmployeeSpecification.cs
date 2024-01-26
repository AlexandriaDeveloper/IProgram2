using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
            if (param.Id.HasValue)
            {
                AddCriteries(x => x.Id == param.Id);
            }
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (!string.IsNullOrEmpty(param.NationalId))
            {
                AddCriteries(x => x.NationalId.Equals(param.NationalId));
            }
            if (param.TabCode.HasValue)
            {
                AddCriteries(x => x.TabCode.Equals(param.TabCode));
            }
            if (param.TegaraCode.HasValue)
            {
                AddCriteries(x => x.TegaraCode.Equals(param.TegaraCode));
            }
            if (param.DepartmentId.HasValue)
            {
                AddCriteries(x => x.DepartmentId.Equals(param.DepartmentId));
            }
            if (!string.IsNullOrEmpty(param.Collage))
            {
                AddCriteries(x => x.Collage.Contains(param.Collage));
            }
            if (!string.IsNullOrEmpty(param.DepartmentName))
            {
                AddCriteries(x => x.Department.Name.Contains(param.DepartmentName));
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

            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (!string.IsNullOrEmpty(param.NationalId))
            {
                AddCriteries(x => x.NationalId.Equals(param.NationalId));
            }
            if (param.TabCode.HasValue)
            {
                AddCriteries(x => x.TabCode.Equals(param.TabCode.Value.ToString()));
            }
            if (param.TegaraCode.HasValue)
            {
                AddCriteries(x => x.TegaraCode.Equals(param.TegaraCode.Value.ToString()));
            }
            if (param.DepartmentId.HasValue)
            {
                AddCriteries(x => x.DepartmentId.Equals(param.DepartmentId));
            }
            if (!string.IsNullOrEmpty(param.Collage))
            {
                AddCriteries(x => x.Collage.Contains(param.Collage));
            }

            this.PaginationEnabled = false;

        }

    }
}