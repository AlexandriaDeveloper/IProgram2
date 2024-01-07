using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class FormSpecification : Specification<Form>
    {
        public FormSpecification(int id, FormParam param) : base(x => x.DailyId == id)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            AddOrderByDescending(x => x.Id);

            ApplyPaging(param.PageIndex, param.PageSize);
        }



    }

    public class FormCountSpecification : Specification<Form>
    {
        public FormCountSpecification(int id, FormParam param) : base(x => x.DailyId == id)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            AddOrderByDescending(x => x.Id);

            ApplyPaging(param.PageIndex, param.PageSize);
        }



    }
}
