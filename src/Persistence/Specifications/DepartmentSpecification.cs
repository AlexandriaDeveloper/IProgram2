using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class DepartmentSpecification : Specification<Department>
    {

        public DepartmentSpecification(DepartmentParam param)
        {
            if (param.Id.HasValue)
            {
                AddCriteries(x => x.Id == param.Id);
            }
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            AddOrderByDescending(x => x.Id);

            ApplyPaging(param.PageIndex, param.PageSize);
        }

    }

    public class DepartmentCountSpecification : Specification<Department>
    {
        public DepartmentCountSpecification(DepartmentParam param)
        {
            if (param.Id.HasValue)
            {
                AddCriteries(x => x.Id == param.Id);
            }
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            this.PaginationEnabled = false;


        }
    }
}