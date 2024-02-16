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
            AddInclude(x => x.FormDetails);
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }

            if (!string.IsNullOrEmpty(param.CreatedBy))
            {
                AddInclude(x => x.User);
                AddCriteries(x => x.User.DisplayName.Contains(param.CreatedBy));
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
            if (!string.IsNullOrEmpty(param.CreatedBy))
            {
                AddInclude(x => x.User);
                AddCriteries(x => x.User.DisplayName.Contains(param.CreatedBy));
            }


            this.PaginationEnabled = false;
            // AddOrderByDescending(x => x.Id);

            //  ApplyPaging(param.PageIndex, param.PageSize);
        }



    }




}
