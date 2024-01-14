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
            if (!string.IsNullOrEmpty(param.Collage))
            {
                AddCriteries(x => x.Collage.Contains(param.Collage));
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


        }

    }
}