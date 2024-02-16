using Core.Models;
using Persistence.Helpers;

namespace Persistence.Specifications
{
    public class ArchivedFormsSpecification : Specification<Form>
    {
        public ArchivedFormsSpecification(FormArchivedParam param) : base(x => x.DailyId == null)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (!string.IsNullOrEmpty(param.CreatedBy))
            {
                AddCriteries(x => x.CreatedBy.Equals(param.CreatedBy));
            }

            AddOrderByDescending(x => x.Id);

            ApplyPaging(param.PageIndex, param.PageSize);
        }



    }

    public class ArchivedFormsCountSpecification : Specification<Form>
    {
        public ArchivedFormsCountSpecification(FormArchivedParam param) : base(x => x.DailyId == null)
        {
            if (!string.IsNullOrEmpty(param.Name))
            {
                AddCriteries(x => x.Name.Contains(param.Name));
            }
            if (!string.IsNullOrEmpty(param.CreatedBy))
            {
                AddCriteries(x => x.CreatedBy.Equals(param.CreatedBy));
            }

            this.PaginationEnabled = false;
            // AddOrderByDescending(x => x.Id);

            // ApplyPaging(param.PageIndex, param.PageSize);
        }



    }
}
